using System.Text.Json;
using MySqlConnector;

namespace ZarayskVipSystem;

public sealed record VipMember(ulong SteamId, string PlayerName, string GroupId, DateTime? ExpiresAt, VipPreferences Preferences)
{
    public bool IsActive => ExpiresAt is null || ExpiresAt > DateTime.UtcNow;
    public HashSet<string> DisabledFeatures => Preferences.Disabled;
}

public sealed class VipStore(VipConfig config, string localPath = "vip-local.json")
{
    private readonly Zaraysk.Storage.LocalJsonStore<VipMember>? local = config.Database.Provider == "json" ? new(localPath) : null;
    private readonly string prefix = SanitizePrefix(config.Database.TablePrefix);
    private readonly string connectionString = new MySqlConnectionStringBuilder
    {
        Server = config.Database.Host,
        Port = (uint)Math.Clamp(config.Database.Port, 1, 65535),
        Database = config.Database.Database,
        UserID = config.Database.User,
        Password = config.Database.Password,
        SslMode = config.Database.Ssl ? MySqlSslMode.Preferred : MySqlSslMode.Disabled,
        Pooling = true,
        MaximumPoolSize = 20,
        ConnectionTimeout = 8,
        DefaultCommandTimeout = 15,
        CharacterSet = "utf8mb4"
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (config.Database.Provider is not ("json" or "mysql")) throw new InvalidDataException("Unknown VIP storage provider");
        if (local is not null) { local.Use(rows => rows.Count, true); return; }
        await using var connection = await OpenAsync();
        var sql = $"""
            CREATE TABLE IF NOT EXISTS `{prefix}vip_members` (
              `steam_id` BIGINT UNSIGNED NOT NULL PRIMARY KEY,
              `player_name` VARCHAR(128) NOT NULL,
              `group_id` VARCHAR(64) NOT NULL,
              `expires_at` DATETIME NULL,
              `disabled_features` TEXT NOT NULL,
              `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
              `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
              INDEX `idx_vip_expires_at` (`expires_at`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<VipMember?> GetAsync(ulong steamId)
    {
        if (local is not null) return local.Use(rows => rows.FirstOrDefault(row => row.SteamId == steamId && row.IsActive));
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"SELECT steam_id,player_name,group_id,expires_at,disabled_features FROM `{prefix}vip_members` WHERE steam_id=@steam AND (expires_at IS NULL OR expires_at > UTC_TIMESTAMP())", connection);
        command.Parameters.AddWithValue("@steam", steamId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new VipMember(reader.GetUInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetDateTime(3), ParsePreferences(reader.GetString(4)));
    }

    public async Task UpsertAsync(ulong steamId, string playerName, string groupId, int days)
    {
        if (local is not null) { local.Use(rows => { rows.RemoveAll(row => row.SteamId == steamId); rows.Add(new(steamId, playerName, groupId, days == 0 ? null : DateTime.UtcNow.AddDays(days), new())); return true; }, true); return; }
        await using var connection = await OpenAsync();
        var sql = $"""
            INSERT INTO `{prefix}vip_members` (steam_id,player_name,group_id,expires_at,disabled_features)
            VALUES (@steam,@name,@group,IF(@days=0,NULL,DATE_ADD(UTC_TIMESTAMP(), INTERVAL @days DAY)),'[]')
            ON DUPLICATE KEY UPDATE player_name=VALUES(player_name),group_id=VALUES(group_id),expires_at=VALUES(expires_at),disabled_features='[]'
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@steam", steamId);
        command.Parameters.AddWithValue("@name", playerName[..Math.Min(128, playerName.Length)]);
        command.Parameters.AddWithValue("@group", groupId);
        command.Parameters.AddWithValue("@days", Math.Max(0, days));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> RemoveAsync(ulong steamId)
    {
        if (local is not null) return local.Use(rows => rows.RemoveAll(row => row.SteamId == steamId) > 0, true);
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"DELETE FROM `{prefix}vip_members` WHERE steam_id=@steam", connection);
        command.Parameters.AddWithValue("@steam", steamId);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task SavePreferencesAsync(ulong steamId, VipPreferences preferences)
    {
        if (local is not null) { local.Use(rows => { var index = rows.FindIndex(row => row.SteamId == steamId); if (index >= 0) rows[index] = rows[index] with { Preferences = preferences }; return true; }, true); return; }
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"UPDATE `{prefix}vip_members` SET disabled_features=@features WHERE steam_id=@steam", connection);
        command.Parameters.AddWithValue("@steam", steamId);
        command.Parameters.AddWithValue("@features", JsonSerializer.Serialize(preferences));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static VipPreferences ParsePreferences(string json)
    {
        try
        {
            if (json.TrimStart().StartsWith('['))
                return new VipPreferences { Disabled = new HashSet<string>(JsonSerializer.Deserialize<string[]>(json) ?? [], StringComparer.OrdinalIgnoreCase) };
            var preferences = JsonSerializer.Deserialize<VipPreferences>(json) ?? new VipPreferences();
            preferences.Disabled = new HashSet<string>(preferences.Disabled, StringComparer.OrdinalIgnoreCase);
            return preferences;
        }
        catch (JsonException) { return new VipPreferences(); }
    }

    private static string SanitizePrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new InvalidOperationException("Database.TablePrefix may contain only letters, digits and underscores.");
        return value;
    }
}
