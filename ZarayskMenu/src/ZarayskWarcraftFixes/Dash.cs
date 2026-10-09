using System.Reflection;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace ZarayskWarcraftFixes;

public sealed class DashGate
{
    private bool wasGrounded = true, available;
    public bool Step(bool grounded, bool jump, bool ready)
    {
        if (grounded) available = false;
        else if (wasGrounded) available = true;
        wasGrounded = grounded;
        // Trigger as soon as the jump is airborne. Requiring a release and a
        // second press was easy to miss because CS2 reports the first press
        // while the pawn is still flagged as on-ground.
        if (grounded || !available || !jump || !ready) return false;
        available = false;
        return true;
    }
}

public static class Dash
{
    private sealed class State
    {
        public readonly DashGate Gate = new();
        public readonly float SpawnTime = Server.CurrentTime;
    }
    private static readonly ConditionalWeakTable<object, State> States = new();
    private static MethodInfo? getProfile;
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static void Tick(object effect, CCSPlayerController owner)
    {
        if (!owner.IsValid || !owner.PawnIsAlive || owner.PlayerPawn.Value is not { IsValid: true } pawn) return;
        if (pawn.MovementServices is not CCSPlayer_MovementServices movement) return;
        getProfile ??= effect.GetType().Assembly.GetType("WarcraftPlugin.WarcraftPlayerExtensions")!
            .GetMethod("GetWarcraftPlayer", BindingFlags.Static | BindingFlags.Public)!;
        var profile = getProfile.Invoke(null, [owner]);
        if (profile == null) return;
        var race = profile.GetType().GetMethod("GetClass")!.Invoke(profile, null);
        if (race?.GetType().FullName != "WarcraftPlugin.Classes.Ranger") return;
        var state = States.GetValue(effect, _ => new State());
        ulong buttons = movement.Buttons.ButtonStates[0];
        bool ready = Server.CurrentTime > state.SpawnTime + .2f &&
            (int)profile.GetType().GetMethod("GetAbilityLevel")!.Invoke(profile, [0])! > 0 &&
            (bool)race.GetType().GetMethod("IsAbilityReady", Flags)!.Invoke(race, [0])!;
        if (!state.Gate.Step((pawn.Flags & 1) != 0, (buttons & 2) != 0, ready)) return;
        // Teleport with velocity tells the movement system about the impulse.
        var angle = pawn.EyeAngles;
        float yaw = angle.Y + ((buttons & 0x10) != 0 ? 180 : (buttons & 0x200) != 0 ? 90 : (buttons & 0x400) != 0 ? -90 : 0);
        double pitchRad = angle.X * Math.PI / 180, yawRad = yaw * Math.PI / 180;
        var velocity = new Vector((float)(Math.Cos(pitchRad) * Math.Cos(yawRad) * 800),
            (float)(Math.Cos(pitchRad) * Math.Sin(yawRad) * 800), (float)(Math.Max(.275, -Math.Sin(pitchRad)) * 800));
        pawn.Teleport(null, null, velocity);
        race.GetType().GetMethod("StartCooldown", Flags)!.Invoke(race, [0, null]);
        owner.PrintToChat(" [Warcraft] Рывок!");
    }
}
