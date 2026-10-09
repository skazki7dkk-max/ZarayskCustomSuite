using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace ZarayskVipSystem;

public sealed class VipConfig : BasePluginConfig
{
    [JsonPropertyName("Database")]
    public VipDatabaseConfig Database { get; set; } = new();

    [JsonPropertyName("Settings")]
    public VipSettings Settings { get; set; } = new();

    [JsonPropertyName("Groups")]
    public Dictionary<string, VipGroup> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VIP"] = new()
        {
            DisplayName = "VIP",
            ScoreboardTag = "[VIP]",
            Health = 110,
            Armor = 100,
            Helmet = true,
            RoundMoney = 1000,
            Speed = 1.05f,
            Gravity = 0.95f,
            AntiFlash = true,
            BunnyHop = true,
            BhopMaxSpeedLimit = 400,
            DefuseKit = true,
            NoFallDamage = true,
            DamageHud = true,
            HealthshotsEveryRounds = 3,
            HealthshotCount = 1
        },
        ["PREMIUM"] = new()
        {
            DisplayName = "PREMIUM",
            ScoreboardTag = "[PREMIUM]",
            Health = 120,
            Armor = 120,
            Helmet = true,
            RoundMoney = 2000,
            Speed = 1.10f,
            Gravity = 0.90f,
            AntiFlash = true,
            BunnyHop = true,
            BhopMaxSpeedLimit = 500,
            DefuseKit = true,
            NoFallDamage = true,
            DamageHud = true,
            HealthshotsEveryRounds = 3,
            HealthshotCount = 2,
            RgbModel = true,
            RgbSmoke = true,
            KillScreen = true,
            CustomKillFeed = true,
            Fov = true,
            Auras = true,
            GrenadeTrails = true
        }
    };
}

public sealed class VipDatabaseConfig
{
    public string Provider { get; set; } = "mysql";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "cs2_admin";
    public string User { get; set; } = "cs2";
    public string Password { get; set; } = "change_me";
    public string TablePrefix { get; set; } = "zaraysk_";
    public bool Ssl { get; set; }
}

public sealed class VipSettings
{
    public bool LocalTestMode { get; set; }
    public string ChatPrefix { get; set; } = "{green}[Zaraysk VIP]{default}";
    public string AdminPermission { get; set; } = "@css/root";
    public int MaxMoney { get; set; } = 65000;
    public int BonusStartRound { get; set; } = 2;
    public float DamageHudSeconds { get; set; } = 3f;
    public List<int> FovChoices { get; set; } = [90, 95, 100, 105, 110, 115, 120];
    public List<int> BhopSpeedChoices { get; set; } = [250, 275, 300, 325, 350, 400, 450, 500];
    public List<string> ColorChoices { get; set; } = ["white", "red", "orange", "yellow", "lime", "green", "cyan", "blue", "purple", "pink"];
    public Dictionary<string, string> AuraEffects { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["off"] = "",
        ["circle"] = "particles/ui/ui_player_ping.vpcf"
    };
    public string GrenadeTrailParticle { get; set; } = "particles/ui/ui_player_ping.vpcf";
}

public sealed class VipGroup
{
    public List<string> CustomSkinIds { get; set; } = [];
    public string DisplayName { get; set; } = "VIP";
    public string ScoreboardTag { get; set; } = "[VIP]";
    public int Health { get; set; } = 100;
    public int Armor { get; set; } = 100;
    public bool Helmet { get; set; } = true;
    public int RoundMoney { get; set; }
    public float Speed { get; set; } = 1f;
    public float Gravity { get; set; } = 1f;
    public bool AntiFlash { get; set; }
    public bool BunnyHop { get; set; }
    public int BhopMaxSpeedLimit { get; set; } = 500;
    public bool DefuseKit { get; set; }
    public bool NoFallDamage { get; set; }
    public bool DamageHud { get; set; }
    public int HealthshotsEveryRounds { get; set; }
    public int HealthshotCount { get; set; }
    public bool RgbModel { get; set; }
    public bool RgbSmoke { get; set; }
    public bool KillScreen { get; set; }
    public bool CustomKillFeed { get; set; }
    public bool Fov { get; set; }
    public bool Auras { get; set; }
    public bool GrenadeTrails { get; set; }
}
