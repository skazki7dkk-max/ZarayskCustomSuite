using System.Text.Json;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using ZarayskMenu.Api;

namespace ZarayskWarcraftMenu;

[MinimumApiVersion(375)]
public sealed partial class Plugin : BasePlugin
{
    public override string ModuleName => "ZarayskWarcraftMenu";
    public override string ModuleVersion => "0.3.1";
    public override string ModuleAuthor => "Zaraysk";
    public override string ModuleDescription => "Warcraft menu adapter; requires ZarayskMenu";
    GuideConfig config = new();
    IMenuService? service;
    readonly List<IDisposable> registrations = [];
    bool warned;

    public override void Load(bool hotReload)
    {
        config = JsonSerializer.Deserialize<GuideConfig>(File.ReadAllText(Path.Combine(ModuleDirectory, "warcraft.json")), GuideConfig.Json)
            ?? throw new InvalidDataException("Empty warcraft.json");
        config.Validate();
        AddCommand("css_war", "Warcraft menu", (p, c) => Open(p, "warcraft"));
        AddCommand("css_helpwar", "Warcraft skill instructions", (p, c) => Open(p, "warcraft_help"));
        AddCommand("css_warxp", "Admin: award yourself Warcraft XP; server: amount SteamID64", GiveExperience);
        // Reconnect after late core loading or hot reload; never retain a stale core.
        AddTimer(1.0f, Sync, TimerFlags.REPEAT);
    }
    public override void OnAllPluginsLoaded(bool hotReload) => Sync();
    void Open(CCSPlayerController? player, string id)
    {
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false }) return;
        Sync();
        if (service == null || !service.Open(player, id))
            player.PrintToChat("Warcraft | Меню недоступно. Администратору: проверьте ZarayskMenu и журнал сервера.");
    }
    void Sync()
    {
        var next = MenuService.Current;
        if (ReferenceEquals(next, service)) return;
        Detach();
        if (next == null) return;
        try
        {
            var help = new MenuDefinition { Id = "warcraft_help", Title = "Warcraft | Как играть" };
            help.Items.Add(new() { Id = "navigation", Text = "Управление меню", Message = "Warcraft | Мышь — выбор; 8 — назад, 9 — далее, 0 — выход (цифры с CFG). !menu — главное; !war — Warcraft.", CloseOnSelect = false });
            if (config.Skills.Count == 0)
                help.Items.Add(new() { Id = "missing", Text = "Описания скиллов ещё не подключены", Message = "Warcraft | В UI-архиве нет логики навыков. Нужна настройка warcraft.json по серверному RPG-плагину.", CloseOnSelect = false });
            var groups = new Dictionary<string, MenuDefinition>();
            for (int i = 0; i < config.Skills.Count; i++)
            {
                var skill = config.Skills[i];
                if (!groups.TryGetValue(skill.Group, out var group))
                {
                    group = new MenuDefinition { Id = "warcraft_group_" + groups.Count, Title = skill.Group };
                    groups.Add(skill.Group, group);
                    help.Items.Add(new() { Id = "group_" + groups.Count, Text = skill.Group, Submenu = group.Id });
                }
                var id = "warcraft_skill_" + i;
                var page = new MenuDefinition { Id = id, Title = skill.Name,
                    Items = skill.Lines.Select((line, n) => new MenuItem { Id = "line_" + n, Text = line, Message = line, CloseOnSelect = false }).ToList() };
                registrations.Add(next.Register(page, (_, _) => { }));
                group.Items.Add(new() { Id = "skill_" + i, Text = skill.Name, Submenu = id });
            }
            foreach (var group in groups.Values) registrations.Add(next.Register(group, (_, _) => { }));
            registrations.Add(next.Register(help, (_, _) => { }));
            var main = new MenuDefinition { Id = "warcraft", Title = "Warcraft | RPG",
                Items = [new() { Id = "guide", Text = "Как использовать навыки", Submenu = "warcraft_help" }] };
            for (int i = 0; i < config.Actions.Count; i++)
                main.Items.Add(new() { Id = "action_" + i, Text = config.Actions[i].Title, Permission = config.Actions[i].Permission });
            if (config.Actions.Count == 0)
                main.Items.Add(new() { Id = "setup", Text = "Игровой модуль не подключён", Message = "Warcraft | Подключите команды серверного RPG-плагина в warcraft.json.", CloseOnSelect = false });
            registrations.Add(next.Register(main, (player, item) =>
            {
                if (!item.StartsWith("action_") || !int.TryParse(item[7..], out var index) || index < 0 || index >= config.Actions.Count) return;
                var action = config.Actions[index];
                if (action.Permission.Length > 0 && !AdminManager.PlayerHasPermissions(player, action.Permission)) return;
                // Core closes its session before callback so priority cannot dismiss the target menu.
                player.ExecuteClientCommandFromServer(action.Command);
            }));
            service = next; warned = false;
        }
        catch (Exception ex)
        {
            Detach();
            if (!warned) Logger.LogError(ex, "Warcraft menu registration failed");
            warned = true;
        }
    }
    void Detach()
    {
        for (int i = registrations.Count - 1; i >= 0; i--) registrations[i].Dispose();
        registrations.Clear(); service = null;
    }
    public override void Unload(bool hotReload) => Detach();
}
