using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using ZarayskMenu.Api;
using ZarayskMenu.Api.Compatibility.Enum;
using ZarayskBaseMenu = ZarayskMenu.Api.Compatibility.Class.BaseMenu;

namespace ZarayskVipSystem;

[MinimumApiVersion(376)]
public sealed class ZarayskVipSystemPlugin : BasePlugin, IPluginConfig<VipConfig>
{
    public override string ModuleName => "Zaraysk VIP System";
    public override string ModuleVersion => "2.3.1";
    public override string ModuleAuthor => "Zaraysk";
    public override string ModuleDescription => "Standalone VIP system for CounterStrikeSharp and ZarayskMenu";

    public VipConfig Config { get; set; } = new();
    private VipStore? store;
    private volatile bool databaseReady;
    private readonly ConcurrentDictionary<ulong, VipMember> members = new();
    private readonly HashSet<ulong> roundMoneyGranted = [];
    private readonly HashSet<ulong> roundHealthshotGranted = [];
    private readonly Dictionary<ulong, CParticleSystem> auraEntities = [];
    private readonly Dictionary<uint, (CBaseCSGrenadeProjectile Grenade, CParticleSystem Particle)> grenadeTrails = [];
    private readonly Dictionary<uint, (CSmokeGrenadeProjectile Smoke, ulong SteamId)> coloredSmokes = [];
    private readonly HashSet<ulong> bhopAirborne = [];
    private readonly Dictionary<ulong, DamageHudState> damageHud = [];
    private readonly Dictionary<ulong, Action<CCSPlayerController>> menuBack = [];
    private readonly HashSet<int> localTestSlots = [];
    private readonly HashSet<string> localTestCaptures = [];
    private int roundNumber;
    private float rgbHue;
    private float nextSmokeUpdate;
    private float nextModelColorUpdate;

    private sealed class DamageHudState
    {
        public int Total;
        public uint VictimIndex;
        public float LastHit;
        public int LastDamage;
    }

    public void OnConfigParsed(VipConfig config)
    {
        ValidateConfig(config);
        Config = config;
        store = new VipStore(config, Path.Combine(ModuleDirectory, "vip-local.json"));
    }

    public override void Load(bool hotReload)
    {
        CosmeticAccess.VipCatalog = VipCosmeticCatalog;
        CosmeticAccess.OpenVipMenu = OpenMenu;
        AddCommand("css_vip", "Open Zaraysk VIP menu", OnVipCommand);
        AddCommand("css_zvip_add", "css_zvip_add <steam64> <group> <days> [name]", OnAddCommand);
        AddCommand("css_zvip_remove", "css_zvip_remove <steam64>", OnRemoveCommand);
        AddCommand("css_zvip_reload", "Reload a player's VIP data", OnReloadCommand);
        AddCommand("css_zvip_status", "Show current VIP status", OnStatusCommand);
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnClientConnect>((slot, _, ip) =>
        {
            if (Config.Settings.LocalTestMode && Config.Database.Provider == "json"
                && (ip is "127.0.0.1" or "::1" or "loopback" || ip.StartsWith("127.0.0.1:") || ip.StartsWith("[::1]:"))) localTestSlots.Add(slot);
        });
        RegisterEventHandler<EventPlayerConnectFull>((e, _) =>
        {
            if (e.Userid is { IsValid: true } player)
            {
                LoadMember(player.SteamID);
                AddTimer(1f, () => { if (Human(player)) LoadMember(player.SteamID); });
                if (Config.Settings.LocalTestMode && Config.Database.Provider == "json")
                {
                    AddTimer(7f, () =>
                    {
                        if (!Human(player) || !TryGroup(player, out var group, out var member)
                            || !group.DamageHud || !Enabled(member, "damage_hud")) return;
                        RecordDamageHit(player, 27);
                        AddTimer(0.35f, () =>
                        {
                            if (!Human(player)) return;
                            RecordDamageHit(player, 13);
                            CaptureLocalTestScreenshot(player, "zaraysk_damage_hud", 0.2f);
                        });
                    });
                }
            }
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt, HookMode.Post);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate, HookMode.Post);
        RegisterEventHandler<EventSmokegrenadeExpired>(OnSmokeExpired, HookMode.Post);
        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePre);
        RegisterListener<Listeners.OnEntityCreated>(OnEntityCreated);
        RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecacheResources);
        RegisterListener<Listeners.OnMapStart>(_ => { roundNumber = 0; roundMoneyGranted.Clear(); roundHealthshotGranted.Clear(); ClearVisualEntities(); });
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);
        AddCommandListener("say", OnSay, HookMode.Pre);
        AddCommandListener("say_team", OnSayTeam, HookMode.Pre);
        _ = InitializeAsync(hotReload);
    }

    public override void Unload(bool hotReload)
    {
        if (CosmeticAccess.VipCatalog == VipCosmeticCatalog) CosmeticAccess.VipCatalog = null;
        if (CosmeticAccess.OpenVipMenu == OpenMenu) CosmeticAccess.OpenVipMenu = null;
        foreach (var player in HumanPlayers()) ResetPawn(player);
        ClearVisualEntities();
        members.Clear();
    }

    private async Task InitializeAsync(bool hotReload)
    {
        try
        {
            await RequireStore().InitializeAsync();
            databaseReady = true;
            Logger.LogInformation("Zaraysk VIP database is ready");
            Server.NextFrame(() => { foreach (var player in HumanPlayers()) LoadMember(player.SteamID); });
        }
        catch (Exception ex) { Logger.LogError(ex, "Zaraysk VIP database initialization failed"); }
    }

    private void OnClientAuthorized(int slot, SteamID steamId) => LoadMember(steamId.SteamId64);
    private void OnClientDisconnect(int slot)
    {
        localTestSlots.Remove(slot);
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player?.SteamID is > 0)
        {
            members.TryRemove(player.SteamID, out _);
            roundMoneyGranted.Remove(player.SteamID);
            roundHealthshotGranted.Remove(player.SteamID);
            bhopAirborne.Remove(player.SteamID);
            damageHud.Remove(player.SteamID);
            menuBack.Remove(player.SteamID);
            RemoveAura(player.SteamID);
        }
    }

    private void LoadMember(ulong steamId)
    {
        if (!databaseReady || steamId == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var member = await RequireStore().GetAsync(steamId);
                Server.NextFrame(() =>
                {
                    if (member is { IsActive: true } && Config.Groups.ContainsKey(member.GroupId))
                    {
                        members[steamId] = member;
                        Logger.LogInformation("Loaded VIP group {GroupId} for {SteamId}", member.GroupId, steamId);
                        var player = FindPlayer(steamId);
                        if (player is not null) ApplyIdentity(player, member);
                    }
                    else members.TryRemove(steamId, out _);
                });
            }
            catch (Exception ex) { Logger.LogError(ex, "Could not load VIP {SteamId}", steamId); }
        });
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (Human(player)) AddTimer(0.2f, () => ApplySpawnBenefits(player!));
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (!Human(attacker) || attacker == victim || !TryGroup(attacker!, out var group, out var member)) return HookResult.Continue;
        if (group.KillScreen && Enabled(member, "kill_screen")) ShowKillScreen(attacker!, member.Preferences.KillScreenColor);
        if (group.CustomKillFeed && Enabled(member, "kill_feed")) ShowKillFeed(attacker!, victim, @event);
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        if (!Human(attacker) || attacker == @event.Userid || !TryGroup(attacker!, out var group, out var member)
            || !group.DamageHud || !Enabled(member, "damage_hud")) return HookResult.Continue;
        RecordDamageHit(attacker!, Math.Max(0, @event.DmgHealth), @event.Userid?.Index ?? 0);
        return HookResult.Continue;
    }

    private void RecordDamageHit(CCSPlayerController attacker, int amount, uint victimIndex = 0)
    {
        if (amount <= 0) return;
        if (!damageHud.TryGetValue(attacker.SteamID, out var state)) damageHud[attacker.SteamID] = state = new DamageHudState();
        state.LastDamage = amount;
        state.Total = AccumulateDamage(state.Total, state.LastHit, Server.CurrentTime, Config.Settings.DamageHudSeconds, amount);
        state.VictimIndex = victimIndex;
        state.LastHit = Server.CurrentTime;
        DrawDamageHud(attacker, state.Total, state.LastDamage, Config.Settings.DamageHudSeconds);
    }

    public static int AccumulateDamage(int currentTotal, float previousHitTime, float now, float configuredSeconds, int amount)
    {
        var timeout = Math.Clamp(configuredSeconds, 2f, 4f);
        return now - previousHitTime > timeout ? Math.Max(0, amount) : currentTotal + Math.Max(0, amount);
    }

    private void CaptureLocalTestScreenshot(CCSPlayerController player, string name, float delay)
    {
        if (!Config.Settings.LocalTestMode || Config.Database.Provider != "json" || !localTestCaptures.Add(name)) return;
        AddTimer(delay, () =>
        {
            if (!Human(player)) return;
            player.ExecuteClientCommand("jpeg " + name);
            Logger.LogInformation("Requested local QA screenshot {Screenshot}", name);
        });
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        roundNumber = rules is null ? roundNumber + 1 : rules.WarmupPeriod ? 0 : rules.TotalRoundsPlayed + 1;
        roundMoneyGranted.Clear();
        roundHealthshotGranted.Clear();
        foreach (var player in HumanPlayers()) AddTimer(0.25f, () => ApplySpawnBenefits(player));
        return HookResult.Continue;
    }

    private HookResult OnPlayerTakeDamagePre(CCSPlayerPawn pawn, CTakeDamageInfo damage)
    {
        if ((damage.BitsDamageType & DamageTypes_t.DMG_FALL) == 0) return HookResult.Continue;
        var player = ControllerOf(pawn);
        return Human(player) && TryGroup(player!, out var group, out var member) && group.NoFallDamage && Enabled(member, "no_fall")
            ? HookResult.Handled : HookResult.Continue;
    }

    private void ApplySpawnBenefits(CCSPlayerController player)
    {
        if (!TryGroup(player, out var group, out var member)) return;
        var pawn = AlivePawn(player);
        if (pawn is null) return;
        ApplyIdentity(player, member);
        if (roundNumber >= Config.Settings.BonusStartRound && Enabled(member, "health")) { pawn.Health = group.Health; pawn.MaxHealth = Math.Max(100, group.Health); Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth"); }
        if (roundNumber >= Config.Settings.BonusStartRound && Enabled(member, "helmet") && group.Helmet) player.GiveNamedItem("item_assaultsuit");
        if (roundNumber >= Config.Settings.BonusStartRound && Enabled(member, "armor")) { pawn.ArmorValue = group.Armor; Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue"); }
        if (Enabled(member, "defuse") && group.DefuseKit && player.TeamNum == 3) player.GiveNamedItem("item_defuser");
        if (Enabled(member, "speed")) { pawn.VelocityModifier = group.Speed; Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier"); }
        if (Enabled(member, "gravity")) { pawn.GravityScale = group.Gravity; Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale"); }
        if (Enabled(member, "round_money") && roundMoneyGranted.Add(player.SteamID) && player.InGameMoneyServices is not null)
        {
            player.InGameMoneyServices.Account = Math.Min(Config.Settings.MaxMoney, player.InGameMoneyServices.Account + group.RoundMoney);
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
        if (group.HealthshotsEveryRounds > 0 && roundNumber > 0 && roundNumber % group.HealthshotsEveryRounds == 0
            && Enabled(member, "healthshot") && roundHealthshotGranted.Add(player.SteamID))
            for (var i = 0; i < Math.Clamp(group.HealthshotCount, 1, 5); i++) player.GiveNamedItem("weapon_healthshot");
        ApplyAura(player, member, group);
    }

    private void OnTick()
    {
        rgbHue = (rgbHue + 0.8f) % 360f;
        var updateModelColor = Server.CurrentTime >= nextModelColorUpdate;
        if (updateModelColor) nextModelColorUpdate = Server.CurrentTime + 0.08f;
        foreach (var player in HumanPlayers())
        {
            if (!TryGroup(player, out var group, out var member)) continue;
            var pawn = AlivePawn(player);
            if (pawn is null) continue;
            if (Enabled(member, "speed") && pawn.VelocityModifier != group.Speed)
            {
                pawn.VelocityModifier = group.Speed;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
            }
            if (Enabled(member, "gravity") && pawn.GravityScale != group.Gravity)
            {
                pawn.GravityScale = group.Gravity;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");
            }
            if (Enabled(member, "anti_flash") && group.AntiFlash && pawn.FlashDuration > 0)
            {
                pawn.FlashDuration = 0; pawn.FlashMaxAlpha = 0;
                Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashDuration");
                Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashMaxAlpha");
            }
            if (group.BunnyHop && Enabled(member, "bhop")) UpdateBhop(player, pawn, member, group);
            if (group.Fov && Enabled(member, "fov")) ApplyFov(player, member.Preferences.Fov);
            if (updateModelColor && group.RgbModel && Enabled(member, "rgb_model"))
            {
                var color = ColorFromChoice(member.Preferences.ModelColor, rgbHue);
                if (pawn.Render != color)
                {
                    pawn.Render = color;
                    Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
                }
            }
            if (auraEntities.TryGetValue(player.SteamID, out var aura) && aura.IsValid && pawn.AbsOrigin is not null)
                aura.Teleport(pawn.AbsOrigin, null, null);
        }
        if (Server.CurrentTime >= nextSmokeUpdate)
        {
            nextSmokeUpdate = Server.CurrentTime + 0.08f;
            UpdateColoredSmokes();
        }
        UpdateGrenadeTrails();
        foreach (var pair in damageHud.ToArray())
        {
            var player = FindPlayer(pair.Key);
            if (Server.CurrentTime - pair.Value.LastHit > Math.Clamp(Config.Settings.DamageHudSeconds, 2f, 4f) || player is null)
            { damageHud.Remove(pair.Key); continue; }
        }
    }

    private void UpdateBhop(CCSPlayerController player, CCSPlayerPawn pawn, VipMember member, VipGroup group)
    {
        var onGround = (pawn.Flags & 1) != 0;
        if (!onGround) bhopAirborne.Add(player.SteamID);
        if (onGround && bhopAirborne.Contains(player.SteamID) && (player.Buttons & PlayerButtons.Jump) != 0)
        {
            var velocity = LimitedHorizontalVelocity(pawn.AbsVelocity, Math.Min(member.Preferences.BhopMaxSpeed, group.BhopMaxSpeedLimit));
            pawn.Teleport(null, null, new Vector(velocity.X, velocity.Y, 285f));
            bhopAirborne.Remove(player.SteamID);
        }
        else if (onGround && (player.Buttons & PlayerButtons.Jump) == 0) bhopAirborne.Remove(player.SteamID);
        var limited = LimitedHorizontalVelocity(pawn.AbsVelocity, Math.Min(member.Preferences.BhopMaxSpeed, group.BhopMaxSpeedLimit));
        if (Math.Abs(limited.X - pawn.AbsVelocity.X) > 0.1f || Math.Abs(limited.Y - pawn.AbsVelocity.Y) > 0.1f)
            pawn.Teleport(null, null, limited);
    }

    private static Vector LimitedHorizontalVelocity(Vector velocity, int maxSpeed)
    {
        var limit = Math.Clamp(maxSpeed, 200, 1000);
        var horizontal = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        if (horizontal <= limit || horizontal < 0.01f) return new Vector(velocity.X, velocity.Y, velocity.Z);
        var factor = limit / horizontal;
        return new Vector(velocity.X * factor, velocity.Y * factor, velocity.Z);
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (MenuService.Current is null && (pressed & PlayerButtons.Reload) != 0 && menuBack.TryGetValue(player.SteamID, out var back))
        {
            menuBack.Remove(player.SteamID);
            back(player);
        }
    }

    private void OnVipCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Human(player)) { info.ReplyToCommand("Команда доступна только игроку."); return; }
        OpenMenu(player!);
    }

    private void OpenMenu(CCSPlayerController player)
    {
        if (!TryGroup(player, out var group, out var member)) { Reply(player, "У вас нет активной VIP."); return; }
        menuBack.Remove(player.SteamID);
        var title = $"VIP | {group.DisplayName}";
        var menu = new PlayerMenu(this, title);
        if (group.CustomSkinIds.Count > 0) menu.Add("Кастомные скины VIP", (p, _) =>
        {
            if (CosmeticAccess.OpenVipInventory is { } open) open(p);
            else Reply(p, "Плагин ZarayskCosmetics не загружен.");
        }, false);
        menu.Add($"Группа: {group.DisplayName}", (_, _) => { }, true);
        menu.Add($"Срок: {FormatExpiry(member.ExpiresAt)}", (_, _) => { }, true);
        AddFeature(menu, player, member, "health", $"Здоровье: {group.Health}", group.Health > 100);
        AddFeature(menu, player, member, "armor", $"Броня: {group.Armor}", group.Armor > 0);
        AddFeature(menu, player, member, "helmet", "Шлем", group.Helmet);
        AddFeature(menu, player, member, "round_money", $"Каждый раунд: +${group.RoundMoney}", group.RoundMoney > 0);
        AddFeature(menu, player, member, "defuse", "Набор сапёра", group.DefuseKit);
        AddFeature(menu, player, member, "bhop", "Автораспрыжка", group.BunnyHop);
        if (group.BunnyHop) menu.Add($"Макс. скорость bhop: {member.Preferences.BhopMaxSpeed}", (p, _) => OpenIntChoiceMenu(p, "VIP | Скорость bhop", Config.Settings.BhopSpeedChoices.Where(value => value <= group.BhopMaxSpeedLimit), () => member.Preferences.BhopMaxSpeed, value => member.Preferences.BhopMaxSpeed = value), false);
        AddFeature(menu, player, member, "no_fall", "Без урона от падения", group.NoFallDamage);
        AddFeature(menu, player, member, "damage_hud", "Показывать нанесённый урон", group.DamageHud);
        AddFeature(menu, player, member, "speed", $"Скорость: x{group.Speed:0.##}", group.Speed != 1f);
        AddFeature(menu, player, member, "gravity", $"Гравитация: x{group.Gravity:0.##}", group.Gravity != 1f);
        AddFeature(menu, player, member, "healthshot", $"Шприцы: {group.HealthshotCount} каждые {group.HealthshotsEveryRounds} р.", group.HealthshotsEveryRounds > 0);
        AddFeature(menu, player, member, "anti_flash", "Антифлеш", group.AntiFlash);
        AddFeature(menu, player, member, "rgb_model", "RGB-модель", group.RgbModel);
        AddFeature(menu, player, member, "rgb_smoke", "RGB-смок", group.RgbSmoke);
        AddFeature(menu, player, member, "kill_screen", "Подсветка экрана при убийстве", group.KillScreen);
        AddFeature(menu, player, member, "kill_feed", "Расширенный килл-фид", group.CustomKillFeed);
        AddFeature(menu, player, member, "grenade_trails", "Трейлы гранат", group.GrenadeTrails);
        if (group.Fov) menu.Add($"FOV: {member.Preferences.Fov}", (p, _) => OpenIntChoiceMenu(p, "VIP | FOV", Config.Settings.FovChoices, () => member.Preferences.Fov, value => { member.Preferences.Fov = value; ApplyFov(p, value); }), false);
        if (group.KillScreen) menu.Add($"Цвет убийства: {member.Preferences.KillScreenColor}", (p, _) => OpenStringChoiceMenu(p, "VIP | Цвет убийства", Config.Settings.ColorChoices, () => member.Preferences.KillScreenColor, value => member.Preferences.KillScreenColor = value), false);
        if (group.RgbSmoke) menu.Add($"Цвет смока: {member.Preferences.SmokeColor}", (p, _) => OpenStringChoiceMenu(p, "VIP | Цвет смока", Config.Settings.ColorChoices.Prepend("rainbow"), () => member.Preferences.SmokeColor, value => member.Preferences.SmokeColor = value), false);
        if (group.GrenadeTrails) menu.Add($"Цвет трейла: {member.Preferences.TrailColor}", (p, _) => OpenStringChoiceMenu(p, "VIP | Цвет трейла", Config.Settings.ColorChoices, () => member.Preferences.TrailColor, value => member.Preferences.TrailColor = value), false);
        menu.Add($"Цвет VIP-тега: {member.Preferences.ChatTagColor}", (p, _) => OpenStringChoiceMenu(p, "VIP | Цвет тега", Config.Settings.ColorChoices, () => member.Preferences.ChatTagColor, value => member.Preferences.ChatTagColor = value), false);
        menu.Add($"Цвет ника: {member.Preferences.ChatNameColor}", (p, _) => OpenStringChoiceMenu(p, "VIP | Цвет ника", Config.Settings.ColorChoices, () => member.Preferences.ChatNameColor, value => member.Preferences.ChatNameColor = value), false);
        menu.Add($"Цвет сообщения: {member.Preferences.ChatMessageColor}", (p, _) => OpenStringChoiceMenu(p, "VIP | Цвет сообщения", Config.Settings.ColorChoices, () => member.Preferences.ChatMessageColor, value => member.Preferences.ChatMessageColor = value), false);
        if (group.Auras) menu.Add($"Аура: {member.Preferences.Aura}", (p, _) => OpenStringChoiceMenu(p, "VIP | Аура", Config.Settings.AuraEffects.Keys, () => member.Preferences.Aura, value => { member.Preferences.Aura = value; RemoveAura(p.SteamID); ApplyAura(p, member, group); }), false);
        menu.Open(player);
    }

    private IReadOnlyCollection<string> VipCosmeticCatalog(CCSPlayerController player) =>
        TryGroup(player, out var group, out _) ? group.CustomSkinIds : Array.Empty<string>();

    private void AddFeature(PlayerMenu menu, CCSPlayerController player, VipMember member, string id, string label, bool available)
    {
        if (!available) return;
        var enabled = Enabled(member, id);
        menu.Add($"{(enabled ? "[ON]" : "[OFF]")} {label}", (p, _) => ToggleFeature(p, id), false);
    }

    private void ToggleFeature(CCSPlayerController player, string feature)
    {
        if (!members.TryGetValue(player.SteamID, out var member)) return;
        if (!member.DisabledFeatures.Add(feature)) member.DisabledFeatures.Remove(feature);
        SavePreferences(member);
        if (!Enabled(member, feature)) ResetFeature(player, feature);
        else if (feature is "health" or "armor" or "helmet" or "speed" or "gravity") ApplySpawnBenefits(player);
        OpenMenu(player);
    }

    private void OpenIntChoiceMenu(CCSPlayerController player, string title, IEnumerable<int> choices, Func<int> current, Action<int> select)
    {
        if (!members.TryGetValue(player.SteamID, out var member)) return;
        var values = choices.Distinct().ToList();
        var menu = new PlayerMenu(this, title);
        foreach (var value in values)
        {
            var captured = value;
            menu.Add($"{(captured == current() ? "[ВЫБРАНО] " : "")}{value.ToString(CultureInfo.InvariantCulture)}", (p, _) =>
            {
                select(captured); SavePreferences(member); OpenIntChoiceMenu(p, title, values, current, select);
            }, false);
        }
        AddBack(menu, player);
        menu.Open(player);
    }

    private void OpenStringChoiceMenu(CCSPlayerController player, string title, IEnumerable<string> choices, Func<string> current, Action<string> select)
    {
        if (!members.TryGetValue(player.SteamID, out var member)) return;
        var values = choices.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var menu = new PlayerMenu(this, title);
        foreach (var value in values)
        {
            var captured = value;
            menu.Add($"{(captured.Equals(current(), StringComparison.OrdinalIgnoreCase) ? "[ВЫБРАНО] " : "")}{captured}", (p, _) =>
            {
                select(captured); SavePreferences(member); OpenStringChoiceMenu(p, title, values, current, select);
            }, false);
        }
        AddBack(menu, player);
        menu.Open(player);
    }

    private void AddBack(PlayerMenu menu, CCSPlayerController player)
    {
        menuBack[player.SteamID] = OpenMenu;
    }

    private void SavePreferences(VipMember member)
    {
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<VipPreferences>(System.Text.Json.JsonSerializer.Serialize(member.Preferences)) ?? new VipPreferences();
        _ = Task.Run(async () =>
        {
            try { await RequireStore().SavePreferencesAsync(member.SteamId, snapshot); }
            catch (Exception ex) { Logger.LogError(ex, "Could not save VIP preferences for {SteamId}", member.SteamId); }
        });
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info) => FormatChat(player, info, false);
    private HookResult OnSayTeam(CCSPlayerController? player, CommandInfo info) => FormatChat(player, info, true);
    private HookResult FormatChat(CCSPlayerController? player, CommandInfo info, bool teamOnly)
    {
        if (!Human(player) || !TryGroup(player!, out var group, out var member)) return HookResult.Continue;
        var message = info.GetArg(1).Trim('"').Trim();
        if (message.Length == 0 || message[0] is '!' or '/') return HookResult.Continue;
        var formatted = $" {ChatColor(member.Preferences.ChatTagColor)}{group.ScoreboardTag} {ChatColor(member.Preferences.ChatNameColor)}{player!.PlayerName}{ChatColors.Default}: {ChatColor(member.Preferences.ChatMessageColor)}{message}";
        if (teamOnly)
            foreach (var receiver in HumanPlayers().Where(receiver => receiver.TeamNum == player.TeamNum)) receiver.PrintToChat(formatted);
        else Server.PrintToChatAll(formatted);
        return HookResult.Handled;
    }

    private void ApplyIdentity(CCSPlayerController player, VipMember member)
    {
        if (!Config.Groups.TryGetValue(member.GroupId, out var group)) return;
        player.Clan = group.ScoreboardTag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
        if (group.Fov && Enabled(member, "fov")) ApplyFov(player, member.Preferences.Fov);
    }

    private static void ApplyFov(CCSPlayerController player, int fov)
    {
        var value = (uint)Math.Clamp(fov, 60, 140);
        if (player.DesiredFOV != value) player.DesiredFOV = value;
        var pawn = AlivePawn(player);
        if (pawn is null) return;
        if (Math.Abs(pawn.ViewmodelFOV - value) > 0.01f)
        {
            pawn.ViewmodelFOV = value;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flViewmodelFOV");
        }
    }

    private void ShowKillScreen(CCSPlayerController player, string colorName)
    {
        var pawn = AlivePawn(player);
        if (pawn is not null)
        {
            pawn.HealthShotBoostExpirationTime = Server.CurrentTime + 1.5f;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flHealthShotBoostExpirationTime");
        }
        var fade = Utilities.CreateEntityByName<CEnvFade>("env_fade");
        if (fade is null) return;
        var color = ColorFromChoice(colorName, rgbHue);
        fade.FadeColor = Color.FromArgb(55, color.R, color.G, color.B);
        fade.Duration = 0.3f;
        fade.HoldDuration = 1.1f;
        fade.Spawnflags = 1;
        fade.DispatchSpawn();
        fade.AcceptInput("Fade", player, player);
        AddTimer(1.6f, () => { if (fade.IsValid) fade.Remove(); });
    }

    private static void ShowKillFeed(CCSPlayerController attacker, CCSPlayerController? victim, EventPlayerDeath death)
    {
        var flags = new List<string>();
        if (death.Headshot) flags.Add("HEADSHOT");
        if (death.Noscope) flags.Add("NO SCOPE");
        if (death.Penetrated > 0) flags.Add($"WALL x{death.Penetrated}");
        if (death.Thrusmoke) flags.Add("SMOKE");
        if (death.Attackerblind) flags.Add("BLIND");
        if (death.Attackerinair) flags.Add("AIR");
        if (death.Assistedflash) flags.Add("FLASH ASSIST");
        if (death.Dominated > 0) flags.Add("DOMINATION");
        if (death.Revenge > 0) flags.Add("REVENGE");
        if (death.Wipe > 0) flags.Add("SQUAD WIPE");
        var extras = flags.Count == 0 ? "KILL" : string.Join(" • ", flags);
        attacker.PrintToChat($" {ChatColors.Gold}{attacker.PlayerName} {ChatColors.White}{death.Weapon} {ChatColors.LightBlue}{victim?.PlayerName ?? "enemy"} {ChatColors.Red}[{extras}]");
    }

    private static void DrawDamageHud(CCSPlayerController attacker, int damage, int lastDamage, float configuredSeconds)
    {
        var color = damage >= 100 ? "#ff4040" : damage >= 50 ? "#ffb020" : "#ffffff";
        // One draw per hit avoids the visible blink caused by replacing CenterHtml every 150 ms.
        var duration = (int)Math.Ceiling(Math.Clamp(configuredSeconds, 2f, 4f));
        attacker.PrintToCenterHtml($"<font color='{color}' class='fontSize-s'><b>{damage}</b></font> <font color='#aeb7c8' class='fontSize-s'>урона · +{lastDamage}</font>", duration);
    }

    private void OnEntityCreated(CEntityInstance entity)
    {
        if (entity is CSmokeGrenadeProjectile smoke)
            foreach (var delay in new[] { 0.05f, 0.2f, 0.6f, 1.2f }) AddTimer(delay, () => ApplySmokeColor(smoke));
        if (entity is CBaseCSGrenadeProjectile grenade) Server.NextFrame(() => AttachGrenadeTrail(grenade));
    }

    private void OnPrecacheResources(ResourceManifest manifest)
    {
        foreach (var effect in Config.Settings.AuraEffects.Values.Append(Config.Settings.GrenadeTrailParticle)
                     .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            manifest.AddResource(effect);
    }

    private void ApplySmokeColor(CSmokeGrenadeProjectile smoke)
    {
        if (!smoke.IsValid) return;
        var pawn = smoke.Thrower.Value ?? smoke.OriginalThrower.Value;
        var player = ControllerOf(pawn);
        if (!Human(player) || !TryGroup(player!, out var group, out var member) || !group.RgbSmoke || !Enabled(member, "rgb_smoke")) return;
        TrackSmoke(smoke, player!, member);
    }

    private HookResult OnSmokeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!Human(player) || !TryGroup(player!, out var group, out var member) || !group.RgbSmoke || !Enabled(member, "rgb_smoke"))
            return HookResult.Continue;
        var smoke = Utilities.FindAllEntitiesByDesignerName<CSmokeGrenadeProjectile>("smokegrenade_projectile")
            .Where(entity => entity.IsValid)
            .OrderBy(entity => entity.Index == (uint)Math.Max(0, @event.Entityid) ? 0 : DistanceSquared(entity.AbsOrigin, @event.X, @event.Y, @event.Z))
            .FirstOrDefault();
        if (smoke is null) return HookResult.Continue;
        TrackSmoke(smoke, player!, member);
        foreach (var delay in new[] { 0.05f, 0.2f, 0.5f }) AddTimer(delay, () =>
        {
            if (smoke.IsValid && members.TryGetValue(player!.SteamID, out var current)) SetSmokeColor(smoke, current.Preferences.SmokeColor);
        });
        return HookResult.Continue;
    }

    private HookResult OnSmokeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        coloredSmokes.Remove((uint)Math.Max(0, @event.Entityid));
        return HookResult.Continue;
    }

    private void TrackSmoke(CSmokeGrenadeProjectile smoke, CCSPlayerController player, VipMember member)
    {
        coloredSmokes[smoke.Index] = (smoke, player.SteamID);
        SetSmokeColor(smoke, member.Preferences.SmokeColor);
    }

    private static float DistanceSquared(Vector? origin, float x, float y, float z)
    {
        if (origin is null) return float.MaxValue;
        var dx = origin.X - x; var dy = origin.Y - y; var dz = origin.Z - z;
        return dx * dx + dy * dy + dz * dz;
    }

    private void UpdateColoredSmokes()
    {
        foreach (var key in coloredSmokes.Keys.ToArray())
        {
            var (smoke, steamId) = coloredSmokes[key];
            if (!smoke.IsValid || !members.TryGetValue(steamId, out var member)) { coloredSmokes.Remove(key); continue; }
            SetSmokeColor(smoke, member.Preferences.SmokeColor);
        }
    }

    private void SetSmokeColor(CSmokeGrenadeProjectile smoke, string choice)
    {
        var color = ColorFromChoice(choice, rgbHue);
        smoke.SmokeColor.X = color.R; smoke.SmokeColor.Y = color.G; smoke.SmokeColor.Z = color.B;
        Utilities.SetStateChanged(smoke, "CSmokeGrenadeProjectile", "m_vSmokeColor");
    }

    private void AttachGrenadeTrail(CBaseCSGrenadeProjectile grenade)
    {
        if (!grenade.IsValid || grenade is CSmokeGrenadeProjectile) return;
        var pawn = grenade.Thrower.Value;
        var player = ControllerOf(pawn);
        if (!Human(player) || !TryGroup(player!, out var group, out var member) || !group.GrenadeTrails || !Enabled(member, "grenade_trails")) return;
        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle is null || string.IsNullOrWhiteSpace(Config.Settings.GrenadeTrailParticle)) return;
        particle.EffectName = Config.Settings.GrenadeTrailParticle;
        particle.StartActive = true;
        particle.Tint = ColorFromChoice(member.Preferences.TrailColor, rgbHue);
        particle.DispatchSpawn();
        particle.AcceptInput("Start");
        grenadeTrails[grenade.Index] = (grenade, particle);
    }

    private void UpdateGrenadeTrails()
    {
        foreach (var key in grenadeTrails.Keys.ToArray())
        {
            var (grenade, particle) = grenadeTrails[key];
            if (!grenade.IsValid || !particle.IsValid || grenade.AbsOrigin is null)
            {
                if (particle.IsValid) particle.Remove();
                grenadeTrails.Remove(key);
                continue;
            }
            particle.Teleport(grenade.AbsOrigin, null, null);
        }
    }

    private void ApplyAura(CCSPlayerController player, VipMember member, VipGroup group)
    {
        if (!group.Auras || !Enabled(member, "aura") || member.Preferences.Aura.Equals("off", StringComparison.OrdinalIgnoreCase)
            || auraEntities.ContainsKey(player.SteamID) || !Config.Settings.AuraEffects.TryGetValue(member.Preferences.Aura, out var effect)
            || string.IsNullOrWhiteSpace(effect)) return;
        var pawn = AlivePawn(player);
        if (pawn?.AbsOrigin is null) return;
        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle is null) return;
        particle.EffectName = effect;
        particle.StartActive = true;
        particle.DispatchSpawn();
        particle.Teleport(pawn.AbsOrigin, null, null);
        particle.AcceptInput("Start");
        auraEntities[player.SteamID] = particle;
    }

    private void RemoveAura(ulong steamId)
    {
        if (auraEntities.Remove(steamId, out var entity) && entity.IsValid) entity.Remove();
    }

    private void ClearVisualEntities()
    {
        foreach (var entity in auraEntities.Values) if (entity.IsValid) entity.Remove();
        foreach (var (_, particle) in grenadeTrails.Values) if (particle.IsValid) particle.Remove();
        auraEntities.Clear(); grenadeTrails.Clear(); coloredSmokes.Clear(); damageHud.Clear();
    }

    private void OnAddCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!CanManage(caller)) { info.ReplyToCommand("Нет доступа."); return; }
        if (info.ArgCount < 4 || !ulong.TryParse(info.GetArg(1), out var steam) || !int.TryParse(info.GetArg(3), out var days) || days < 0)
        { info.ReplyToCommand("Использование: css_zvip_add <steam64> <group> <days, 0=навсегда> [name]"); return; }
        var group = info.GetArg(2);
        if (!Config.Groups.ContainsKey(group)) { info.ReplyToCommand("Неизвестная группа. Доступно: " + string.Join(", ", Config.Groups.Keys)); return; }
        var name = info.ArgCount >= 5 ? string.Join(' ', Enumerable.Range(4, info.ArgCount - 4).Select(info.GetArg)) : steam.ToString(CultureInfo.InvariantCulture);
        _ = Task.Run(async () =>
        {
            try { await RequireStore().UpsertAsync(steam, name, group, days); Server.NextFrame(() => { LoadMember(steam); info.ReplyToCommand($"VIP {group} выдана {steam}."); }); }
            catch (Exception ex) { Logger.LogError(ex, "Could not add VIP"); Server.NextFrame(() => info.ReplyToCommand("Ошибка базы данных.")); }
        });
    }

    private void OnRemoveCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!CanManage(caller)) { info.ReplyToCommand("Нет доступа."); return; }
        if (info.ArgCount != 2 || !ulong.TryParse(info.GetArg(1), out var steam)) { info.ReplyToCommand("Использование: css_zvip_remove <steam64>"); return; }
        _ = Task.Run(async () =>
        {
            try { var removed = await RequireStore().RemoveAsync(steam); Server.NextFrame(() => { members.TryRemove(steam, out _); info.ReplyToCommand(removed ? "VIP удалена." : "VIP не найдена."); }); }
            catch (Exception ex) { Logger.LogError(ex, "Could not remove VIP"); Server.NextFrame(() => info.ReplyToCommand("Ошибка базы данных.")); }
        });
    }

    private void OnReloadCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!CanManage(caller)) { info.ReplyToCommand("Нет доступа."); return; }
        if (info.ArgCount == 2 && ulong.TryParse(info.GetArg(1), out var steam)) LoadMember(steam);
        else foreach (var player in HumanPlayers()) LoadMember(player.SteamID);
        info.ReplyToCommand("VIP-данные обновляются.");
    }

    private void OnStatusCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Human(player)) { info.ReplyToCommand("Команда доступна только игроку."); return; }
        if (!TryGroup(player!, out var group, out var member)) { Reply(player!, "У вас нет активной VIP."); return; }
        Reply(player!, $"Группа: {group.DisplayName}. Срок: {FormatExpiry(member.ExpiresAt)}.");
    }

    private bool TryGroup(CCSPlayerController player, out VipGroup group, out VipMember member)
    {
        group = null!; member = null!;
        if (!members.TryGetValue(player.SteamID, out var foundMember) || !foundMember.IsActive
            || !Config.Groups.TryGetValue(foundMember.GroupId, out var foundGroup)) return false;
        member = foundMember;
        group = foundGroup;
        return true;
    }

    private static bool Enabled(VipMember member, string feature) => !member.DisabledFeatures.Contains(feature);
    private bool CanManage(CCSPlayerController? player) => player is null || AdminManager.PlayerHasPermissions(player, Config.Settings.AdminPermission);
    private VipStore RequireStore() => store ?? throw new InvalidOperationException("VIP config is not loaded.");
    private static bool Human(CCSPlayerController? player) => player is { IsValid: true, IsBot: false, IsHLTV: false };
    private static CCSPlayerPawn? AlivePawn(CCSPlayerController? player) => player is { PawnIsAlive: true } ? player.PlayerPawn.Value : null;
    private static CCSPlayerController? ControllerOf(CCSPlayerPawn? pawn)
    {
        var controller = pawn?.Controller.Value;
        return controller is null ? null : new CCSPlayerController(controller.Handle);
    }
    private static IEnumerable<CCSPlayerController> HumanPlayers() => Utilities.GetPlayers().Where(Human);
    private static CCSPlayerController? FindPlayer(ulong steamId) => HumanPlayers().FirstOrDefault(player => player.SteamID == steamId);
    private void Reply(CCSPlayerController player, string message) => player.PrintToChat($" {Colorize(Config.Settings.ChatPrefix)} {Colorize(message)}");
    private static string Colorize(string text)
    {
        (string Tag, char Color)[] colors =
        [
            ("{default}", ChatColors.Default), ("{white}", ChatColors.White), ("{green}", ChatColors.Green),
            ("{lime}", ChatColors.Lime), ("{red}", ChatColors.Red), ("{purple}", ChatColors.Purple),
            ("{grey}", ChatColors.Grey), ("{yellow}", ChatColors.Yellow), ("{gold}", ChatColors.Gold),
            ("{blue}", ChatColors.Blue), ("{lightblue}", ChatColors.LightBlue), ("{orange}", ChatColors.Orange)
        ];
        foreach (var (tag, color) in colors) text = text.Replace(tag, color.ToString(), StringComparison.OrdinalIgnoreCase);
        return text;
    }
    private static string FormatExpiry(DateTime? expiresAt) => expiresAt is null ? "навсегда" : expiresAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static char ChatColor(string name) => name.ToLowerInvariant() switch
    {
        "red" => ChatColors.Red, "orange" => ChatColors.Orange, "yellow" => ChatColors.Yellow,
        "gold" => ChatColors.Gold, "lime" => ChatColors.Lime, "green" => ChatColors.Green,
        "cyan" or "lightblue" => ChatColors.LightBlue, "blue" => ChatColors.Blue,
        "purple" => ChatColors.Purple, "pink" => ChatColors.Magenta, "grey" => ChatColors.Grey,
        _ => ChatColors.White
    };

    private static Color ColorFromChoice(string name, float hue) => name.ToLowerInvariant() switch
    {
        "rainbow" => Hsv(hue, 1f, 1f), "red" => Color.FromArgb(255, 255, 45, 45),
        "orange" => Color.FromArgb(255, 255, 135, 30), "yellow" => Color.FromArgb(255, 255, 230, 35),
        "gold" => Color.FromArgb(255, 255, 190, 45), "lime" => Color.FromArgb(255, 100, 255, 40),
        "green" => Color.FromArgb(255, 30, 210, 90), "cyan" => Color.FromArgb(255, 35, 225, 255),
        "blue" => Color.FromArgb(255, 55, 105, 255), "purple" => Color.FromArgb(255, 155, 75, 255),
        "pink" => Color.FromArgb(255, 255, 75, 190), _ => Color.White
    };

    private static Color Hsv(float hue, float saturation, float value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60f % 2 - 1));
        var m = value - chroma;
        (float r, float g, float b) = hue switch
        {
            < 60 => (chroma, x, 0f), < 120 => (x, chroma, 0f), < 180 => (0f, chroma, x),
            < 240 => (0f, x, chroma), < 300 => (x, 0f, chroma), _ => (chroma, 0f, x)
        };
        return Color.FromArgb(255, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    private static void ValidateConfig(VipConfig config)
    {
        if (config.Groups.Count == 0) throw new InvalidOperationException("At least one VIP group is required.");
        foreach (var (id, group) in config.Groups)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) throw new InvalidOperationException("Invalid VIP group id.");
            group.Health = Math.Clamp(group.Health, 1, 500);
            group.Armor = Math.Clamp(group.Armor, 0, 500);
            group.Speed = Math.Clamp(group.Speed, 0.5f, 2f);
            group.Gravity = Math.Clamp(group.Gravity, 0.1f, 2f);
            group.RoundMoney = Math.Clamp(group.RoundMoney, 0, config.Settings.MaxMoney);
            group.HealthshotCount = Math.Clamp(group.HealthshotCount, 0, 5);
        }
    }

    private static void ResetPawn(CCSPlayerController player)
    {
        var pawn = AlivePawn(player); if (pawn is null) return;
        pawn.GravityScale = 1f; pawn.VelocityModifier = 1f;
        pawn.Render = Color.White;
        player.DesiredFOV = 90;
        player.Clan = "";
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale");
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private static void ResetFeature(CCSPlayerController player, string feature)
    {
        var pawn = AlivePawn(player); if (pawn is null) return;
        if (feature == "speed") { pawn.VelocityModifier = 1f; Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier"); }
        if (feature == "gravity") { pawn.GravityScale = 1f; Utilities.SetStateChanged(pawn, "CBaseEntity", "m_flGravityScale"); }
        if (feature == "health") { pawn.MaxHealth = 100; pawn.Health = Math.Min(100, pawn.Health); Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth"); }
    }

    private sealed class PlayerMenu(ZarayskVipSystemPlugin plugin, string title)
    {
        private readonly List<(string Text, Action<CCSPlayerController, object?> Callback, bool Disabled)> items = [];
        public void Add(string text, Action<CCSPlayerController, object?> callback, bool disabled) => items.Add((text, callback, disabled));
        public void Open(CCSPlayerController player)
        {
            if (MenuService.Current is not null)
            {
                var menu = new ZarayskBaseMenu(title);
                menu.BackAction = p =>
                {
                    if (plugin.menuBack.Remove(p.SteamID, out var back)) back(p);
                    else MenuService.Current?.Open(p);
                };
                foreach (var item in items)
                {
                    var captured = item;
                    menu.AddItem(captured.Text, (p, _) => captured.Callback(p, null), captured.Disabled ? DisableOption.Disabled : DisableOption.None);
                }
                menu.Display(player, 0);
                return;
            }
            var fallback = new CenterHtmlMenu(title, plugin);
            foreach (var item in items)
            {
                var captured = item;
                fallback.AddMenuOption(captured.Text, (p, _) => captured.Callback(p, null), captured.Disabled);
            }
            fallback.Open(player);
        }
    }
}
