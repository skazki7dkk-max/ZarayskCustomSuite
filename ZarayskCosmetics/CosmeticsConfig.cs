using CounterStrikeSharp.API.Core;

namespace ZarayskCosmetics;

public sealed class CosmeticsConfig : BasePluginConfig
{
    public bool LocalTestMode { get; set; }
    public CosmeticsDatabase Database { get; set; } = new();
    public string AdminPermission { get; set; } = "@css/root";
    public Dictionary<string, CosmeticDefinition> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["example_t"] = new() { DisplayName = "Пример модели T", Team = 2, WorkshopId = 0, ModelPath = "characters/models/tm_phoenix/tm_phoenix.vmdl" },
        ["example_ct"] = new() { DisplayName = "Пример модели CT", Team = 3, WorkshopId = 0, ModelPath = "characters/models/ctm_sas/ctm_sas.vmdl" }
    };
}

public sealed class CosmeticsDatabase
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

public sealed class CosmeticDefinition
{
    public string DisplayName { get; set; } = "Модель";
    public ulong WorkshopId { get; set; }
    public string Type { get; set; } = "player";
    public string ModelPath { get; set; } = "";
    public string ParticlePath { get; set; } = "";
    public string WeaponClass { get; set; } = "";
    public int Team { get; set; }
    public float OffsetForward { get; set; }
    public float OffsetRight { get; set; }
    public float OffsetUp { get; set; }
    public bool? PetFlying { get; set; }
    public float PetFollowSmoothing { get; set; } = 0.18f;
    public string PetIdleAnimation { get; set; } = "idle";

    public string Slot => Type.Equals("weapon", StringComparison.OrdinalIgnoreCase)
        ? "weapon:" + WeaponClass.ToLowerInvariant()
        : Type.ToLowerInvariant();
}

public sealed record CosmeticGrant(ulong SteamId, string CosmeticId, int Quantity, DateTime? ExpiresAt, bool Equipped);
