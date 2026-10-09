namespace ZarayskWarcraftFixes;

using System.Drawing;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

public static class Shapeshifter
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Ultimate(object race)
    {
        if (FindProperty(race.GetType(), "Player")?.GetValue(race) is not CCSPlayerController { IsValid: true } player ||
            !player.PawnIsAlive || player.PlayerPawn.Value is not { IsValid: true } pawn)
            return;
        if (FindMethod(race.GetType(), "IsAbilityReady")?.Invoke(race, [3]) is bool ready && !ready)
            return;

        FindMethod(race.GetType(), "StartCooldown")?.Invoke(race, [3, null]);
        pawn.Render = Color.FromArgb(55, 255, 255, 255);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        player.PrintToChat(" [Оборотень] Мимикрия включена на 10 секунд.");

        var pluginType = race.GetType().Assembly.GetType("WarcraftPlugin.WarcraftPlugin");
        if (pluginType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is not BasePlugin plugin)
            return;
        plugin.AddTimer(10f, () =>
        {
            if (!player.IsValid || player.PlayerPawn.Value is not { IsValid: true } current || current.Handle != pawn.Handle)
                return;
            FindMethod(race.GetType(), "SetDefaultAppearance")?.Invoke(race, null);
            player.PrintToChat(" [Оборотень] Мимикрия закончилась.");
        });
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetProperty(name, Flags | BindingFlags.DeclaredOnly) is { } property) return property;
        return null;
    }

    private static MethodInfo? FindMethod(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetMethod(name, Flags | BindingFlags.DeclaredOnly) is { } method) return method;
        return null;
    }
}
