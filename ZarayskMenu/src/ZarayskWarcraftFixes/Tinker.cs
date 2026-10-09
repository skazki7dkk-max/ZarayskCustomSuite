namespace ZarayskWarcraftFixes;

using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

public static class Tinker
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Ultimate(object race)
    {
        if (FindProperty(race.GetType(), "Player")?.GetValue(race) is not CCSPlayerController { IsValid: true } player ||
            !player.PawnIsAlive || player.PlayerPawn.Value is not { IsValid: true } pawn)
            return;

        var ready = FindMethod(race.GetType(), "IsAbilityReady");
        if (ready?.Invoke(race, [3]) is bool isReady && !isReady)
            return;

        FindMethod(race.GetType(), "StartCooldown")?.Invoke(race, [3, null]);

        // Safe replacement for the entity-based drone swarm. It gives an
        // immediate combat repair without spawning or parenting native props.
        pawn.Health = Math.Min(pawn.MaxHealth, pawn.Health + 35);
        pawn.ArmorValue = Math.Min(100, pawn.ArmorValue + 50);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        player.PrintToChat(" [Tinker] Аварийный ремонт: +35 здоровья и +50 брони.");
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetProperty(name, Flags | BindingFlags.DeclaredOnly) is { } property)
                return property;
        return null;
    }

    private static MethodInfo? FindMethod(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetMethod(name, Flags | BindingFlags.DeclaredOnly) is { } method)
                return method;
        return null;
    }
}
