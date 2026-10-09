namespace ZarayskWarcraftFixes;

using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

public static class Necromancer
{
    public static void OnSpawn(object race)
    {
        // GiveNamedItem here re-enters the native CanAcquire hook while CS2 is
        // still creating the spawn inventory. Normal purchase/pickup remains.
    }

    public static void RaiseDead(object race)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (race.GetType().GetProperty("Player", flags)?.GetValue(race) is not CCSPlayerController { IsValid: true } owner ||
            !owner.PawnIsAlive) return;
        var pluginType = race.GetType().Assembly.GetType("WarcraftPlugin.WarcraftPlugin");
        if (pluginType?.GetProperty("Instance", flags)?.GetValue(null) is not BasePlugin plugin) return;

        int pulses = 0;
        bool stopped = false;
        Timer? timer = null;
        void Stop()
        {
            if (stopped) return;
            stopped = true;
            timer?.Kill();
        }
        owner.PrintToChat(" [Некромант] Армия мёртвых окружила вас тёмной аурой на 10 секунд.");
        timer = plugin.AddTimer(1f, () =>
        {
            if (++pulses > 10 || !owner.IsValid || !owner.PawnIsAlive ||
                owner.PlayerPawn.Value is not { IsValid: true } source)
            {
                Stop();
                return;
            }
            var sourceOrigin = source.AbsOrigin;
            if (sourceOrigin == null) { Stop(); return; }
            foreach (var target in Utilities.GetPlayers())
            {
                if (!target.IsValid || !target.PawnIsAlive || target.Handle == owner.Handle || target.Team == owner.Team ||
                    target.PlayerPawn.Value is not { IsValid: true } victim || victim.AbsOrigin is not { } victimOrigin) continue;
                float x = victimOrigin.X - sourceOrigin.X;
                float y = victimOrigin.Y - sourceOrigin.Y;
                float z = victimOrigin.Z - sourceOrigin.Z;
                if (x * x + y * y + z * z <= 350f * 350f)
                    Damage.ApplyPawn(victim, 8f);
            }
            if (pulses >= 10) Stop();
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }
}
