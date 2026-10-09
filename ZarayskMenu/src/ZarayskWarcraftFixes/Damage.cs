using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Memory;

namespace ZarayskWarcraftFixes;

public static class Damage
{
    // Matches CounterStrikeSharp 1.0.375 native ListenerTests.DealDamageFunc.
    [StructLayout(LayoutKind.Sequential)]
    public struct AttackerInfo
    {
        public bool NeedInit, IsPawn, IsWorld;
        public uint AttackerPawn;
        public int AttackerPlayerSlot, TeamChecked, Team;
    }

    public static void WriteAttacker(CCSPlayerController attacker, IntPtr info)
    {
        // This engine structure is not exposed through schema. Fail closed when
        // surrounding schema changes instead of writing an unchecked native offset.
        if (Schema.GetSchemaOffset("CTakeDamageInfo", "m_bStoppedBullet") != 132 ||
            Schema.GetSchemaOffset("CTakeDamageInfo", "m_bInTakeDamageFlow") != 272 ||
            Schema.GetClassSize("CTakeDamageInfo") < 273)
            throw new NotSupportedException("Warcraft damage: unsupported engine layout; send css_version and server logs.");
        Marshal.StructureToPtr(new AttackerInfo {
            NeedInit = true, IsPawn = true, AttackerPawn = attacker.Pawn.Raw,
            AttackerPlayerSlot = attacker.Slot
        }, info + 0x88, false);
    }

    public static Action<CBasePlayerPawn, CTakeDamageInfo> Resolve() => Invoke;

    public static void Invoke(CBasePlayerPawn pawn, CTakeDamageInfo info)
    {
        ApplyPawn(pawn, info.Damage);
    }

    public static void ApplyPawn(CBasePlayerPawn pawn, float amount)
    {
        if (!pawn.IsValid || pawn.Health <= 0 || !float.IsFinite(amount) || amount <= 0) return;
        int damage = Math.Max(1, (int)MathF.Ceiling(amount));
        if (damage >= pawn.Health)
        {
            // The old engine TakeDamage signature has repeatedly ended the server
            // process. CommitSuicide is a supported CSS API and runs normal death flow.
            pawn.CommitSuicide(explode: false, force: true);
            return;
        }
        pawn.Health -= damage;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }
}
