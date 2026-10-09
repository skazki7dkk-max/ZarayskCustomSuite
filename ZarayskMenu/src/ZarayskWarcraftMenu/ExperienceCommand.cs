using System.Globalization;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace ZarayskWarcraftMenu;

public sealed partial class Plugin
{
    public static bool ValidExperience(string input, out int amount) =>
        int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount is >= 1 and <= 1000000;

    void GiveExperience(CCSPlayerController? caller, CommandInfo command)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (caller != null && (!caller.IsValid || caller.IsBot || caller.IsHLTV)) return;
        if (caller != null && !AdminManager.PlayerHasPermissions(caller, "@css/root") && !AdminManager.PlayerHasPermissions(caller, "@css/addxp"))
        { command.ReplyToCommand("Warcraft | Нет доступа. Требуется @css/addxp или @css/root."); return; }
        bool server = caller == null;
        if (command.ArgCount != (server ? 3 : 2) || !ValidExperience(command.GetArg(1), out int amount))
        {
            command.ReplyToCommand(server ? "Использование: css_warxp <1..1000000> <SteamID64 игрока онлайн>" : "Выдать опыт себе: css_warxp <1..1000000>");
            return;
        }
        var target = caller;
        if (server)
        {
            if (!ulong.TryParse(command.GetArg(2), NumberStyles.None, CultureInfo.InvariantCulture, out var steamId))
            { command.ReplyToCommand("Warcraft | Неверный SteamID64."); return; }
            target = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID == steamId);
        }
        if (target == null) { command.ReplyToCommand("Warcraft | Игрок должен быть подключён к серверу."); return; }
        try
        {
            var active = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "WarcraftPlugin")
                .Select(a => a.GetType("WarcraftPlugin.WarcraftPlugin"))
                .Where(t => t != null)
                .Select(t => (Type: t!, Instance: t!.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)))
                .LastOrDefault(p => p.Instance != null);
            var type = active.Type;
            var plugin = active.Instance;
            var xp = plugin == null ? null : type!.GetField("XpSystem", members)?.GetValue(plugin);
            var profile = plugin == null ? null : type!.GetMethod("GetWcPlayer", members)?.Invoke(plugin, [target]);
            if (xp == null || profile == null) { command.ReplyToCommand("Warcraft | Плагин или профиль игрока ещё не загружен."); return; }
            var levelMethod = profile.GetType().GetMethod("GetLevel", members) ?? throw new MissingMethodException("Warcraft.GetLevel");
            int before = (int)levelMethod.Invoke(profile, null)!;
            if (before >= 16) { command.ReplyToCommand("Warcraft | У текущего класса уже максимальный уровень 16."); return; }
            var stored = (int)(profile.GetType().GetField("currentXp", members)?.GetValue(profile) ?? throw new MissingFieldException("Warcraft.currentXp"));
            if (stored > int.MaxValue - amount) { command.ReplyToCommand("Warcraft | Значение опыта слишком велико."); return; }
            var add = xp.GetType().GetMethod("AddXp", members, null, [typeof(CCSPlayerController), typeof(int)], null)
                ?? throw new MissingMethodException("Warcraft.AddXp");
            // The original RPG method performs level-ups and marks progress for saving.
            add.Invoke(xp, [target, amount]);
            int after = (int)levelMethod.Invoke(profile, null)!;
            command.ReplyToCommand($"Warcraft | Выдано {amount} XP: {target.PlayerName}. Уровень {before} → {after} (максимум 16).");
            if (server) target.PrintToChat($"Warcraft | Администратор выдал {amount} XP. Уровень: {after}.");
            Logger.LogInformation("Warcraft XP grant: actor={Actor}, target={SteamId}, amount={Amount}, level={Before}->{After}", caller?.SteamID.ToString() ?? "server", target.SteamID, amount, before, after);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Warcraft experience command failed");
            command.ReplyToCommand("Warcraft | Не удалось завершить выдачу опыта. Проверьте уровень и журнал сервера перед повтором.");
        }
    }
}
