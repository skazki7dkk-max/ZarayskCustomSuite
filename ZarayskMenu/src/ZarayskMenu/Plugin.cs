using System.Diagnostics;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZarayskMenu.Api;

namespace ZarayskMenu;

[MinimumApiVersion(375)]
public sealed partial class Plugin : BasePlugin, IMenuService, IExternalMenuService
{
    public override string ModuleName => "ZarayskMenu";
    public override string ModuleVersion => "0.7.3";
    public override string ModuleAuthor => "Zaraysk";
    public override string ModuleDescription => "Panorama menu and C#/Metamod command integration";
    const string Layout = "panorama/layout/custom_game/zaraysk_menu.vxml_c";
    CCSCustomHudLayout? hud;
    Settings settings = new();
    bool ready, active;
    bool externalWarningLogged;
    readonly Dictionary<string, Entry> menus = [];
    readonly Dictionary<int, Session> sessions = [];
    readonly Dictionary<int, bool> mousePreferences = [];
    sealed record Entry(MenuDefinition Menu, Action<CCSPlayerController, string>? Callback);
    sealed class Session(string menu)
    {
        public string Menu = menu;
        public int Page;
        public List<(string, int)> Back = [];
        public List<MenuItem> Displayed = [];
        public int Cursor;
        public double LastClick;
        public double Expires;
        public double ExitHoldStarted = -1;
        public double LastMouseToggle;
        public bool MouseMode;
        public Action<CCSPlayerController>? OnBack;
    }
    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    string ConfigPath => Path.Combine(ModuleDirectory, "menu.json");
    bool HtmlMode => settings.RenderMode == "html";
    static bool Human(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };
    static bool Allowed(CCSPlayerController p, string permission) => permission.Length == 0 || AdminManager.PlayerHasPermissions(p, permission);
    bool PluginAvailable(string menuId, string itemId)
    {
        if (!settings.RequiredPlugins.TryGetValue(menuId + "." + itemId, out var requiredPlugin)) return true;
        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => string.Equals(assembly.GetName().Name, requiredPlugin, StringComparison.OrdinalIgnoreCase));
    }

    public override void Load(bool hotReload)
    {
        Reload(); active = true; MenuService.Current = this;
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            if (!HtmlMode) manifest.AddResource(Layout);
        });
        if (!HtmlMode) RegisterListener<Listeners.OnCustomHudClicked>((p, entity, id) =>
        {
            if (hud is not { IsValid: true } || entity.Handle != hud.Handle || !Human(p)) return;
            if (id == "mouse_toggle") { ToggleMouse(p); return; }
            if (id.StartsWith("key_") && int.TryParse(id[4..], out var key)) Select(p, key);
        });
        RegisterListener<Listeners.OnPlayerButtonsChanged>((p, pressed, released) => HandleButtons(p, pressed, released));
        AddCommandListener("player_ping", (p, info) =>
        {
            if (!Human(p) || !sessions.ContainsKey(p!.Slot)) return HookResult.Continue;
            ToggleMouse(p);
            return HookResult.Handled;
        }, HookMode.Pre);
        RegisterListener<Listeners.OnMapEnd>(() => { ready = false; sessions.Clear(); ClearExternal(); hud = null; });
        RegisterListener<Listeners.OnClientDisconnect>(slot => { sessions.Remove(slot); mousePreferences.Remove(slot); ReleaseExternal(slot); });
        RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            sessions.Remove(slot); ReleaseExternal(slot);
            var p = Utilities.GetPlayerFromSlot(slot); if (Human(p)) Hide(p!);
        });
        RegisterEventHandler<EventRoundStart>((e, info) => { ready = true; EnsureHud(); CloseAll(); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerDeath>((e, info) => { if (Human(e.Userid)) Close(e.Userid!); return HookResult.Continue; });
        RegisterEventHandler<EventPlayerConnectFull>((e, info) =>
        {
            if (settings.RebuildOnConnect) AddTimer(1.5f, Rebuild, TimerFlags.STOP_ON_MAPCHANGE);
            return HookResult.Continue;
        });
        AddCommand("css_menu", "Open main menu", (p, c) => { if (Human(p)) Toggle(p!); else c.ReplyToCommand("Команда доступна в игре: !menu"); });
        AddCommand("css_zmenu", "Open Zaraysk menu", (p, c) => { if (Human(p)) Toggle(p!); });
        AddCommand("css_zmenu_open", "Open without toggling", (p, c) =>
        {
            if (!Human(p)) { c.ReplyToCommand("Run css_zmenu_open in the GAME client console, not RCON."); return; }
            if (Open(p!)) c.ReplyToCommand($"Зарайск | Меню открыто в режиме {settings.RenderMode}. Управление: W/S, E, R, удержание F.");
            else c.ReplyToCommand("ZarayskMenu: opening failed; use css_zmenu_status and check server logs.");
        });
        AddCommand("css_zmenu_status", "Menu diagnostics (server state only)", (p, c) =>
        {
            c.ReplyToCommand($"ZarayskMenu {ModuleVersion}: active={active}, worldReady={ready}, entity={(hud is { IsValid: true } ? hud.Index.ToString() : "none")}, priority={settings.PriorityEnabled}, sessions={sessions.Count}");
            c.ReplyToCommand(HtmlMode ? "Renderer: HTML. Workshop and client files are not required." : $"Renderer: Panorama. Client resource required: {Layout}.");
            c.ReplyToCommand($"CS2MenuManager compatible CloseActiveMenu APIs found: {ExternalMenus.CloseMethods().Count()}, lastError={externalWarningLogged}");
            if (!ready) return;
            if (Human(p)) c.ReplyToCommand($"steam64={p!.SteamID}, slot={p.Slot}, session={sessions.ContainsKey(p.Slot)}, ownCapture={(hud is { IsValid: true } && hud.IsInputCaptureEnabled(p))}, CSSMenu={MenuManager.GetActiveMenu(p) != null}");
            foreach (var other in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout"))
                if (other.IsValid) c.ReplyToCommand($"HUD {other.Index}: {other.StrLayout}" + (Human(p) ? $", capture={other.IsInputCaptureEnabled(p!)}" : ""));
        });
        AddCommand("css_zmenu_close", "Release menu cursor", (p, c) => { if (Human(p)) Close(p!); });
        AddCommand("css_zmenu_test", "Test stock HTML HUD without menu or Workshop", (p, c) =>
        {
            if (!Human(p)) { c.ReplyToCommand("Команда доступна в консоли игрока."); return; }
            p!.PrintToCenterHtml("<font color='#7eff34'>ZARAYSK: HTML HUD</font>");
            c.ReplyToCommand($"ZarayskMenu {ModuleVersion} loaded; HTML HUD test sent; mode={settings.RenderMode}.");
        });
        AddCommand("css_zmenu_mouse", "Toggle ZarayskMenu mouse control", (p, c) => { if (Human(p) && sessions.ContainsKey(p!.Slot)) ToggleMouse(p); });
        for (int i = 0; i <= 9; i++)
        {
            int key = i;
            AddCommand($"css_zm{key}", "Select menu row", (p, c) => { if (Human(p)) Select(p!, key); });
        }
        AddCommand("zm_reload", "SERVER ONLY: reload menu.json", (p, c) =>
        {
            if (p != null) { c.ReplyToCommand("Server console only"); return; }
            try { Reload(); CloseAll(); c.ReplyToCommand("ZarayskMenu configuration loaded"); }
            catch (Exception ex) { c.ReplyToCommand("Kept previous configuration: " + ex.Message); }
        });
        AddCommand("zm_open", "SERVER ONLY: zm_open slot steamid menu", (p, c) =>
        {
            if (p != null || c.ArgCount != 4 || !int.TryParse(c.GetArg(1), out var slot) || !ulong.TryParse(c.GetArg(2), out var steam)) return;
            var target = Utilities.GetPlayerFromSlot(slot);
            if (Human(target) && target!.SteamID == steam) Open(target, c.GetArg(3));
        });
        AddCommand("zm_players", "SERVER ONLY: menu player diagnostics", (p, c) =>
        {
            if (p != null) return;
            foreach (var player in Utilities.GetPlayers().Where(Human))
                c.ReplyToCommand($"slot={player.Slot}, steam64={player.SteamID}, alive={player.PawnIsAlive}");
        });
        AddTimer(1, () =>
        {
            foreach (var slot in sessions.Keys.ToArray())
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!Human(p)) { sessions.Remove(slot); ReleaseExternal(slot); continue; }
                if (Now >= sessions[slot].Expires) Close(p!);
            }
        }, TimerFlags.REPEAT);
        AddTimer(0.1f, CheckExitHolds, TimerFlags.REPEAT);
        AddTimer(0.15f, () =>
        {
            if (!active || !HtmlMode) return;
            foreach (var slot in sessions.Keys.ToArray())
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (!Human(player)) continue;
                if (Now >= sessions[slot].Expires) { Close(player!); continue; }
                Draw(player!, false);
            }
        }, TimerFlags.REPEAT);
        AddTimer(0.25f, () =>
        {
            if (!ready || !settings.PriorityEnabled || sessions.Count == 0) return;
            foreach (var slot in sessions.Keys.ToArray())
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (!Human(p)) continue;
                if (Now >= sessions[slot].Expires) { Close(p!); continue; }
                ApplyPriority(p!);
            }
        }, TimerFlags.REPEAT);
        if (hotReload) { ready = true; EnsureHud(); }
    }
    void Toggle(CCSPlayerController player)
    {
        if (sessions.TryGetValue(player.Slot, out var session) && session.Menu == "main") CloseWithSound(player);
        else Open(player);
    }
    void Reload()
    {
        var next = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), Settings.Json) ?? throw new InvalidDataException("Empty config");
        next.Validate();
        var dynamic = menus.Where(x => x.Value.Callback != null).ToArray();
        if (next.Menus.Any(m => dynamic.Any(d => d.Key == m.Id))) throw new InvalidDataException("Config collides with API provider");
        menus.Clear(); foreach (var m in next.Menus) menus.Add(m.Id, new(m, null));
        foreach (var d in dynamic) menus.Add(d.Key, d.Value);
        settings = next;
        Logger.LogInformation("ZarayskMenu renderer={Renderer}; configured menus={MenuCount}", settings.RenderMode, menus.Count);
    }
    void EnsureHud()
    {
        if (HtmlMode) return;
        if (!ready || hud is { IsValid: true }) return;
        foreach (var old in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout").ToArray())
            if (old.IsValid && old.StrLayout == Layout) old.Remove();
        hud = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout") ?? throw new InvalidOperationException("custom_hud_layout unavailable");
        hud.StrLayout = Layout; hud.DispatchSpawn();
    }
    void Rebuild()
    {
        if (!ready || !active) return;
        foreach (var p in Utilities.GetPlayers().Where(Human)) Hide(p);
        if (hud is { IsValid: true }) hud.Remove(); hud = null; EnsureHud();
        foreach (var slot in sessions.Keys.ToArray()) { var p = Utilities.GetPlayerFromSlot(slot); if (Human(p)) Draw(p!); }
    }
    public bool Open(CCSPlayerController p, string menuId = "main")
    {
        if (!active || !Human(p) || !menus.TryGetValue(menuId, out var menu) || !Allowed(p, menu.Menu.Permission)) return false;
        try
        {
            Close(p);
            ready = true; EnsureHud();
            var session = new Session(menuId) { MouseMode = mousePreferences.GetValueOrDefault(p.Slot) }; sessions[p.Slot] = session;
            Draw(p);
            PlayMenuSound(p, settings.OpenSound);
            // The client may not have constructed the replicated panel on the first frame.
            // Bound retries never reopen a closed/replaced session or extend its timeout.
            foreach (var delay in new[] { 0.15f, 0.5f, 1.5f })
                AddTimer(delay, () =>
                {
                    if (!active || !Human(p) || !sessions.TryGetValue(p.Slot, out var current) || !ReferenceEquals(current, session)) return;
                    if (Now >= current.Expires) { Close(p); return; }
                    Draw(p, false);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            return true;
        }
        catch (Exception ex)
        {
            Close(p); Logger.LogError(ex, "ZarayskMenu: failed to open for slot {Slot}", p.Slot);
            p.PrintToChat("Зарайск | Ошибка открытия. Проверьте журнал сервера и css_zmenu_status.");
            return false;
        }
    }
    void ApplyPriority(CCSPlayerController p)
    {
        if (!settings.PriorityEnabled) return;
        MenuManager.CloseActiveMenu(p);
        ExternalMenus.Close(p, ex =>
        {
            if (externalWarningLogged) return;
            externalWarningLogged = true;
            Logger.LogWarning(ex, "CS2MenuManager CloseActiveMenu failed; this fork may need a dedicated adapter");
        });
        if (HtmlMode || hud is not { IsValid: true }) return;
        if (MouseCapture(p) && settings.SuppressOtherHudInput)
            foreach (var other in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout"))
                if (other.IsValid && other.Handle != hud.Handle && other.IsInputCaptureEnabled(p))
                    other.SetInputCaptureEnabled(p, false);
        // Do not delete other plugins' entities or assume their root panel ids.
        var capture = MouseCapture(p);
        if (hud.IsInputCaptureEnabled(p) != capture) hud.SetInputCaptureEnabled(p, capture);
    }
    bool MouseCapture(CCSPlayerController p) => sessions.TryGetValue(p.Slot, out var s) && s.MouseMode
        || settings.CaptureMouse && !settings.KeyboardNavigationEnabled;
    public void Close(CCSPlayerController p) { sessions.Remove(p.Slot); ReleaseExternal(p.Slot); Hide(p); }
    void CloseWithSound(CCSPlayerController p) { PlayMenuSound(p, settings.ExitSound); Close(p); }
    void Hide(CCSPlayerController p)
    {
        if (HtmlMode) { if (p.IsValid) p.PrintToCenterHtml(" "); return; }
        if (hud is not { IsValid: true } || !p.IsValid) return;
        hud.SetHasClassForPlayer(p, "window", "shown", false);
        hud.SetInputCaptureEnabled(p, false);
    }
    void CloseAll() { foreach (var p in Utilities.GetPlayers().Where(Human)) Hide(p); sessions.Clear(); ClearExternal(); }
    void Text(CCSPlayerController p, string id, string text) => hud!.SetDialogVariableStringForPlayer(p, id, "text", text);
    void Class(CCSPlayerController p, string id, string name, bool value) => hud!.SetHasClassForPlayer(p, id, name, value);
    List<MenuItem> Visible(CCSPlayerController p, Entry entry) => entry.Menu.Items
        .Where(i => Allowed(p, i.Permission) && PluginAvailable(entry.Menu.Id, i.Id)).ToList();
    void HandleButtons(CCSPlayerController p, PlayerButtons pressed, PlayerButtons released)
    {
        if (!settings.KeyboardNavigationEnabled || !Human(p) || !sessions.TryGetValue(p.Slot, out var s)) return;
        if ((released & PlayerButtons.Inspect) != 0) { s.ExitHoldStarted = -1; KeyState(p, "hint_exit", false); }
        if ((pressed & PlayerButtons.Inspect) != 0) { s.ExitHoldStarted = Now; KeyState(p, "hint_exit", true); }
        if ((pressed & PlayerButtons.Forward) != 0) { Pulse(p, "hint_up"); MoveCursor(p, -1); }
        else if ((pressed & PlayerButtons.Back) != 0) { Pulse(p, "hint_down"); MoveCursor(p, 1); }
        if ((pressed & PlayerButtons.Use) != 0) { Pulse(p, "hint_select"); ActivateCursor(p); }
        if ((pressed & PlayerButtons.Reload) != 0)
        {
            Pulse(p, "hint_back");
            Select(p, 8);
        }
        if ((pressed & PlayerButtons.Attack3) != 0) ToggleMouse(p);
    }
    void ToggleMouse(CCSPlayerController p)
    {
        if (HtmlMode) { p.PrintToChat("Зарайск | HTML-меню: W/S — выбор, E — открыть, R — назад, удержание F — закрыть."); return; }
        if (!sessions.TryGetValue(p.Slot, out var s) || Now - s.LastMouseToggle < 0.3) return;
        s.LastMouseToggle = Now;
        s.MouseMode = !s.MouseMode;
        mousePreferences[p.Slot] = s.MouseMode;
        s.Expires = Now + settings.TimeoutSeconds;
        PlayMenuSound(p, s.MouseMode ? settings.SelectSound : settings.BackSound);
        Class(p, "hint_mouse", "enabled", s.MouseMode);
        Pulse(p, "hint_mouse");
        if (hud is { IsValid: true }) hud.SetInputCaptureEnabled(p, MouseCapture(p));
    }
    void KeyState(CCSPlayerController p, string id, bool pressed)
    {
        if (hud is { IsValid: true } && p.IsValid) Class(p, id, "pressed", pressed);
    }
    void Pulse(CCSPlayerController p, string id)
    {
        if (!sessions.TryGetValue(p.Slot, out var session)) return;
        KeyState(p, id, true);
        AddTimer(0.14f, () =>
        {
            if (Human(p) && sessions.TryGetValue(p.Slot, out var current) && ReferenceEquals(current, session))
                KeyState(p, id, false);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }
    void CheckExitHolds()
    {
        if (!settings.KeyboardNavigationEnabled || sessions.Count == 0) return;
        foreach (var slot in sessions.Keys.ToArray())
        {
            if (!sessions.TryGetValue(slot, out var s) || s.ExitHoldStarted < 0 || Now - s.ExitHoldStarted < settings.ExitHoldSeconds) continue;
            var p = Utilities.GetPlayerFromSlot(slot);
            if (Human(p)) CloseWithSound(p!); else sessions.Remove(slot);
        }
    }
    void MoveCursor(CCSPlayerController p, int delta)
    {
        if (!sessions.TryGetValue(p.Slot, out var s) || s.Displayed.Count == 0) return;
        var previous = s.Cursor;
        s.Cursor = Math.Clamp(s.Cursor + delta, 0, s.Displayed.Count - 1);
        if (s.Cursor != previous) PlayMenuSound(p, settings.MoveSound);
        s.Page = s.Cursor / settings.PageSize;
        Draw(p);
    }
    void ActivateCursor(CCSPlayerController p)
    {
        if (!sessions.TryGetValue(p.Slot, out var s) || s.Displayed.Count == 0) return;
        s.Page = Math.Clamp(s.Cursor / settings.PageSize, 0, Math.Max(0, (s.Displayed.Count - 1) / settings.PageSize));
        Select(p, s.Cursor % settings.PageSize + 1);
    }
    void Draw(CCSPlayerController p, bool refreshTimeout = true)
    {
        if (!sessions.TryGetValue(p.Slot, out var s)) return;
        if (!menus.TryGetValue(s.Menu, out var entry) || !Allowed(p, entry.Menu.Permission)) { Close(p); return; }
        var items = Visible(p, entry); var pages = Math.Max(1, (items.Count + settings.PageSize - 1) / settings.PageSize);
        s.Displayed = items;
        s.Page = Math.Clamp(s.Page, 0, pages - 1);
        var pageStart = s.Page * settings.PageSize;
        if (items.Count == 0) s.Cursor = 0;
        else if (s.Cursor < pageStart || s.Cursor >= pageStart + settings.PageSize || s.Cursor >= items.Count)
            s.Cursor = Math.Min(pageStart, items.Count - 1);
        if (refreshTimeout) s.Expires = Now + settings.TimeoutSeconds;
        if (HtmlMode)
        {
            ApplyPriority(p);
            p.PrintToCenterHtml(HtmlMenuRenderer.Render(settings.Title, entry.Menu.Title, items, s.Cursor, s.Page, settings.PageSize));
            return;
        }
        Text(p, "subtitle", entry.Menu.Title); Text(p, "page", $"{s.Page + 1} / {pages}");
        foreach (var theme in new[] { "gold", "ice", "mono" }) Class(p, "window", "theme_" + theme, settings.Theme == theme);
        Class(p, "window", "compact", settings.Placement == "compact");
        for (int n = 1; n <= 7; n++)
        {
            var index = s.Page * settings.PageSize + n - 1; var item = n <= settings.PageSize && index < items.Count ? items[index] : null;
            Text(p, $"text_{n}", item?.Text ?? ""); Class(p, $"key_{n}", "empty", item == null);
            Class(p, $"key_{n}", "selected", item != null && index == s.Cursor);
            Class(p, $"key_{n}", "disabled", item == null || !item.Enabled || (item.Submenu.Length > 0 && (!menus.TryGetValue(item.Submenu, out var child) || !Allowed(p, child.Menu.Permission))));
        }
        Class(p, "hint_mouse", "enabled", s.MouseMode);
        Class(p, "window", "shown", true); hud!.SetInputCaptureEnabled(p, MouseCapture(p));
        ApplyPriority(p);
    }
    void Select(CCSPlayerController p, int key)
    {
        if (!sessions.TryGetValue(p.Slot, out var s)) return;
        if (key == 0) { CloseWithSound(p); return; }
        if (Now >= s.Expires) { Close(p); return; }
        if (Now - s.LastClick < settings.ClickCooldownMs / 1000.0) return;
        s.LastClick = Now;
        if (!menus.TryGetValue(s.Menu, out var entry) || !Allowed(p, entry.Menu.Permission)) { Close(p); return; }
        var items = s.Displayed;
        if (key == 8)
        {
            var changed = s.Page > 0 || s.Back.Count > 0;
            if (s.Back.Count > 0) { (s.Menu, s.Page) = s.Back[^1]; s.Back.RemoveAt(s.Back.Count - 1); s.Cursor = s.Page * settings.PageSize; }
            else if (s.OnBack is { } back) { PlayMenuSound(p, settings.BackSound); back(p); return; }
            else if (s.Menu != "main") { PlayMenuSound(p, settings.BackSound); Open(p); return; }
            else if (s.Page > 0) { s.Page--; s.Cursor = s.Page * settings.PageSize; }
            if (changed) PlayMenuSound(p, settings.BackSound);
            Draw(p); return;
        }
        if (key == 9) { if ((s.Page + 1) * settings.PageSize < items.Count) { s.Page++; s.Cursor = s.Page * settings.PageSize; PlayMenuSound(p, settings.MoveSound); } Draw(p); return; }
        var index = s.Page * settings.PageSize + key - 1;
        if (key < 1 || key > settings.PageSize || index >= items.Count) return;
        s.Cursor = index;
        var item = items[index]; if (!item.Enabled || !Allowed(p, item.Permission)) { Draw(p); return; }
        PlayMenuSound(p, settings.SelectSound);
        if (item.Submenu.Length > 0)
        {
            if (!menus.TryGetValue(item.Submenu, out var child) || !Allowed(p, child.Menu.Permission) || s.Back.Count >= 16) return;
            s.Back.Add((s.Menu, s.Page)); s.Menu = item.Submenu; s.Page = 0; s.Cursor = 0; Draw(p); return;
        }
        if (item.CloseOnSelect) Close(p); else Draw(p);
        try
        {
            if (item.ServerCommand.Length > 0)
                Server.ExecuteCommand(item.ServerCommand.Replace("{slot}", p.Slot.ToString()).Replace("{userid}", (p.UserId ?? -1).ToString()).Replace("{steamid}", p.SteamID.ToString()));
            else if (item.PlayerCommand.Length > 0) p.ExecuteClientCommandFromServer(item.PlayerCommand);
            else if (item.Message.Length > 0) p.PrintToChat(item.Message);
            else entry.Callback?.Invoke(p, item.Id);
        }
        catch (Exception ex) { Logger.LogError(ex, "Menu action failed: {Item}", item.Id); }
    }
    void PlayMenuSound(CCSPlayerController p, string sound)
    {
        if (settings.SoundVolume <= 0 || string.IsNullOrWhiteSpace(sound)) return;
        try
        {
            // UI files are raw .vsnd resources rather than sound-event names.
            // playvol is client-local, so only the player using the menu hears it.
            if (sound.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase))
                p.ExecuteClientCommandFromServer($"playvol {sound} {settings.SoundVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            else if (p.PlayerPawn.Value is { IsValid: true } pawn)
                pawn.EmitSound(sound, p, settings.SoundVolume, 1f);
        }
        catch (Exception ex) { Logger.LogDebug(ex, "ZarayskMenu sound failed: {Sound}", sound); }
    }
    public IDisposable Register(MenuDefinition menu, Action<CCSPlayerController, string> selected)
    {
        if (!active) throw new InvalidOperationException("Menu core unloaded");
        ArgumentNullException.ThrowIfNull(selected);
        var snapshot = JsonSerializer.Deserialize<MenuDefinition>(JsonSerializer.Serialize(menu))!;
        Settings.ValidateMenu(snapshot, true);
        var entry = new Entry(snapshot, selected);
        if (!menus.TryAdd(snapshot.Id, entry)) throw new InvalidOperationException("Menu id already registered");
        return new Registration(() =>
        {
            if (menus.TryGetValue(snapshot.Id, out var current) && ReferenceEquals(current, entry))
            {
                menus.Remove(snapshot.Id);
                foreach (var slot in sessions.Where(x => x.Value.Menu == snapshot.Id).Select(x => x.Key).ToArray())
                { var p = Utilities.GetPlayerFromSlot(slot); if (Human(p)) Close(p!); else sessions.Remove(slot); }
            }
        });
    }
    sealed class Registration(Action dispose) : IDisposable
    { Action? action = dispose; public void Dispose() { var a = action; action = null; a?.Invoke(); } }
    public override void Unload(bool hotReload)
    {
        active = false; if (ReferenceEquals(MenuService.Current, this)) MenuService.Current = null;
        if (ready) CloseAll(); if (hud is { IsValid: true }) hud.Remove(); hud = null; menus.Clear();
    }
}
