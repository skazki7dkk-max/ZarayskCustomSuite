using MySqlConnector;

namespace ZarayskCosmetics;

public sealed class CosmeticsStore(CosmeticsConfig config, string localPath = "cosmetics-local.json")
{
    private readonly Zaraysk.Storage.LocalJsonStore<CosmeticGrant>? local = config.Database.Provider == "json" ? new(localPath) : null;
    private readonly string prefix = Sanitize(config.Database.TablePrefix);
    private readonly string connectionString = new MySqlConnectionStringBuilder
    {
        Server = config.Database.Host, Port = (uint)Math.Clamp(config.Database.Port, 1, 65535), Database = config.Database.Database,
        UserID = config.Database.User, Password = config.Database.Password, SslMode = config.Database.Ssl ? MySqlSslMode.Preferred : MySqlSslMode.Disabled,
        Pooling = true, MaximumPoolSize = 20, ConnectionTimeout = 8, DefaultCommandTimeout = 15, CharacterSet = "utf8mb4"
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (config.Database.Provider is not ("json" or "mysql")) throw new InvalidDataException("Unknown cosmetics storage provider");
        if (local is not null) { local.Use(rows => rows.Count, true); return; }
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"""
            CREATE TABLE IF NOT EXISTS `{prefix}cosmetics` (
              `steam_id` BIGINT UNSIGNED NOT NULL, `cosmetic_id` VARCHAR(64) NOT NULL, `quantity` INT NOT NULL DEFAULT 1,
              `expires_at` DATETIME NULL, `equipped` BOOLEAN NOT NULL DEFAULT FALSE,
              `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
              PRIMARY KEY (`steam_id`,`cosmetic_id`), INDEX `idx_cosmetic_expiry` (`expires_at`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<CosmeticGrant>> GetAsync(ulong steamId)
    {
        if (local is not null) return local.Use(rows => rows.Where(row => row.SteamId == steamId && row.Quantity > 0 && (row.ExpiresAt is null || row.ExpiresAt > DateTime.UtcNow)).ToList());
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"SELECT steam_id,cosmetic_id,quantity,expires_at,equipped FROM `{prefix}cosmetics` WHERE steam_id=@steam AND quantity>0 AND (expires_at IS NULL OR expires_at>UTC_TIMESTAMP())", connection);
        command.Parameters.AddWithValue("@steam", steamId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<CosmeticGrant>();
        while (await reader.ReadAsync()) result.Add(new(reader.GetUInt64(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetDateTime(3), reader.GetBoolean(4)));
        return result;
    }

    public async Task GrantAsync(ulong steamId, string id, int quantity, int days)
    {
        if (local is not null) { local.Use(rows => { var index = rows.FindIndex(row => row.SteamId == steamId && row.CosmeticId == id); var previous = index < 0 ? null : rows[index]; var grant = new CosmeticGrant(steamId, id, (previous?.Quantity ?? 0) + Math.Clamp(quantity, 1, 999), days == 0 ? null : DateTime.UtcNow.AddDays(days), previous?.Equipped ?? false); if (index < 0) rows.Add(grant); else rows[index] = grant; return true; }, true); return; }
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"""
            INSERT INTO `{prefix}cosmetics` (steam_id,cosmetic_id,quantity,expires_at) VALUES (@steam,@id,@qty,IF(@days=0,NULL,DATE_ADD(UTC_TIMESTAMP(),INTERVAL @days DAY)))
            ON DUPLICATE KEY UPDATE quantity=quantity+VALUES(quantity),expires_at=VALUES(expires_at)
            """, connection);
        command.Parameters.AddWithValue("@steam", steamId); command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@qty", Math.Clamp(quantity, 1, 999)); command.Parameters.AddWithValue("@days", Math.Max(0, days));
        await command.ExecuteNonQueryAsync();
    }

    public async Task EquipAsync(ulong steamId, string? id, IReadOnlyCollection<string> slotIds)
    {
        if (local is not null) { local.Use(rows => { for (var i = 0; i < rows.Count; i++) if (rows[i].SteamId == steamId && slotIds.Contains(rows[i].CosmeticId)) rows[i] = rows[i] with { Equipped = rows[i].CosmeticId == id && rows[i].Quantity > 0 && (rows[i].ExpiresAt is null || rows[i].ExpiresAt > DateTime.UtcNow) }; return true; }, true); return; }
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (slotIds.Count > 0)
        {
            var parameters = slotIds.Select((_, index) => "@slot" + index).ToArray();
            await using var clear = new MySqlCommand($"UPDATE `{prefix}cosmetics` SET equipped=FALSE WHERE steam_id=@steam AND cosmetic_id IN ({string.Join(',', parameters)})", connection, transaction);
            clear.Parameters.AddWithValue("@steam", steamId);
            var index = 0; foreach (var slotId in slotIds) clear.Parameters.AddWithValue("@slot" + index++, slotId);
            await clear.ExecuteNonQueryAsync();
        }
        if (id is not null)
        {
            await using var equip = new MySqlCommand($"UPDATE `{prefix}cosmetics` SET equipped=TRUE WHERE steam_id=@steam AND cosmetic_id=@id AND quantity>0 AND (expires_at IS NULL OR expires_at>UTC_TIMESTAMP())", connection, transaction);
            equip.Parameters.AddWithValue("@steam", steamId); equip.Parameters.AddWithValue("@id", id); await equip.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task RemoveAsync(ulong steamId, string id, int quantity)
    {
        if (local is not null) { local.Use(rows => { var index = rows.FindIndex(row => row.SteamId == steamId && row.CosmeticId == id); if (index >= 0) { var count = Math.Max(0, rows[index].Quantity - Math.Clamp(quantity, 1, 999)); rows[index] = rows[index] with { Quantity = count, Equipped = count > 0 && rows[index].Equipped }; } return true; }, true); return; }
        await using var connection = await OpenAsync();
        await using var command = new MySqlCommand($"UPDATE `{prefix}cosmetics` SET quantity=GREATEST(0,quantity-@qty),equipped=IF(quantity>0,equipped,FALSE) WHERE steam_id=@steam AND cosmetic_id=@id", connection);
        command.Parameters.AddWithValue("@steam", steamId); command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@qty", Math.Clamp(quantity, 1, 999));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<MySqlConnection> OpenAsync() { var connection = new MySqlConnection(connectionString); await connection.OpenAsync(); return connection; }
    private static string Sanitize(string value) => !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsLetterOrDigit(c) || c == '_') ? value : throw new InvalidOperationException("Invalid table prefix.");
}
