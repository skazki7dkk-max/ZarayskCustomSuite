using CounterStrikeSharp.API.Core;

namespace ZarayskMenu.Api;

// Optional bridge: ownership is checked by cosmetics, current VIP entitlement by VIP.
public static class CosmeticAccess
{
    public static Func<CCSPlayerController, IReadOnlyCollection<string>>? VipCatalog { get; set; }
    public static Action<CCSPlayerController>? OpenVipInventory { get; set; }
    public static Action<CCSPlayerController>? OpenVipMenu { get; set; }
}
