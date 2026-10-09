using System.Collections.Concurrent;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using ZarayskMenu.Api;
using ZarayskMenu.Api.Compatibility.Enum;
using ZarayskBaseMenu = ZarayskMenu.Api.Compatibility.Class.BaseMenu;

namespace ZarayskCosmetics;

[MinimumApiVersion(376)]
public sealed class ZarayskCosmeticsPlugin : BasePlugin, IPluginConfig<CosmeticsConfig>
{
    public override string ModuleName => "Zaraysk Cosmetics";
    public override string ModuleVersion => "1.3.1";
    public override string ModuleAuthor => "Zaraysk";
    public override string ModuleDescription => "Timed Workshop model inventory for ZarayskMenu";

    public CosmeticsConfig Config { get; set; } = new();
    private CosmeticsStore? store;
    private volatile bool ready;
    private readonly ConcurrentDictionary<ulong, List<CosmeticGrant>> inventory = new();
    private readonly Dictionary<(ulong SteamId, string Slot), CBaseEntity> spawned = [];
    private readonly Dictionary<(ulong SteamId, string Slot), Vector> petPositions = [];
    private sealed record AppliedWeapon(CBasePlayerWeapon Weapon, CBaseModelEntity? ViewModel, string CosmeticId, string OriginalWorldModel, string OriginalViewModel);
    private readonly Dictionary<ulong, AppliedWeapon> appliedWeapons = [];
    private readonly Dictionary<ulong, Dictionary<string, string>> vipEquipped = [];
    private readonly HashSet<int> localTestSlots = [];
    private readonly HashSet<string> localTestCaptures = [];

    private List<CosmeticGrant> EffectiveItems(CCSPlayerController player, bool vipOnly = false, bool includeVip = true)
    {
        var allowed = includeVip ? CosmeticAccess.VipCatalog?.Invoke(player) ?? Array.Empty<string>() : Array.Empty<string>();
        var owned = inventory.TryGetValue(player.SteamID, out var found) ? found : [];
        var result = vipOnly ? new List<CosmeticGrant>() : owned.Where(item => item.Quantity > 0 && (item.ExpiresAt is null || item.ExpiresAt > DateTime.UtcNow)).ToList();
        foreach (var id in allowed.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Config.Models.TryGetValue(id, out var definition)) continue;
            var equipped = vipEquipped.TryGetValue(player.SteamID, out var selected) && selected.GetValueOrDefault(definition.Slot) == id;
            if (equipped) result.RemoveAll(item => item.Equipped && Config.Models.TryGetValue(item.CosmeticId, out var other) && other.Slot == definition.Slot);
            var index = result.FindIndex(item => item.CosmeticId == id);
            if (index < 0) result.Add(new(player.SteamID, id, 1, null, equipped));
            else if (equipped) result[index] = result[index] with { Equipped = true };
        }
        return result;
    }

    public void OnConfigParsed(CosmeticsConfig config)
    {
        if (config.Models.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
            || string.IsNullOrWhiteSpace(pair.Value.DisplayName)
            || pair.Value.Type is not ("player" or "hat" or "backpack" or "pet" or "weapon" or "aura")
            || pair.Value.Type == "aura" && string.IsNullOrWhiteSpace(pair.Value.ParticlePath)
            || pair.Value.Type != "aura" && string.IsNullOrWhiteSpace(pair.Value.ModelPath)
            || pair.Value.Type == "weapon" && string.IsNullOrWhiteSpace(pair.Value.WeaponClass)))
            throw new InvalidOperationException("Invalid cosmetic definition. Supported types: player, hat, backpack, pet, weapon, aura.");
        Config = config; store = new CosmeticsStore(config, Path.Combine(ModuleDirectory, "cosmetics-local.json"));
    }

    public override void Load(bool hotReload)
    {
        CosmeticAccess.OpenVipInventory = OpenVipInventory;
        AddCommand("css_skins", "Open custom model inventory", OnMenuCommand);
        AddCommand("css_zskin_status", "Show own inventory diagnostics", (player, info) =>
        {
            if (!Human(player)) { info.ReplyToCommand($"ready={ready}, catalog={Config.Models.Count}"); return; }
            var count = inventory.TryGetValue(player!.SteamID, out var found) ? found.Count : 0;
            info.ReplyToCommand($"steam64={player.SteamID}, ready={ready}, owned={count}, catalog={Config.Models.Count}, panorama={MenuService.Current is not null}");
        });
        AddCommand("css_cw", "Open owned custom weapons without VIP", (player, info) =>
        {
            if (Human(player)) OpenMenu(player!, "weapon");
            else info.ReplyToCommand("Команда доступна в игре: !cw");
        });
        AddCommand("css_zskin_give", "css_zskin_give <steam64> <model> <quantity> <days>", OnGiveCommand);
        AddCommand("css_zskin_take", "css_zskin_take <steam64> <model> <quantity>", OnTakeCommand);
        AddCommand("css_zskin_reload", "Reload model inventory", OnReloadCommand);
        RegisterListener<Listeners.OnClientAuthorized>(OnAuthorized);
        RegisterListener<Listeners.OnClientConnect>((slot, _, ip) =>
        {
            if (Config.LocalTestMode && Config.Database.Provider == "json"
                && (ip is "127.0.0.1" or "::1" or "loopback" || ip.StartsWith("127.0.0.1:") || ip.StartsWith("[::1]:"))) localTestSlots.Add(slot);
        });
        RegisterEventHandler<EventPlayerConnectFull>((e, _) =>
        {
            if (e.Userid is { IsValid: true } player)
            {
                LoadInventory(player.SteamID);
                AddTimer(1f, () =>
                {
                    if (!Human(player)) return;
                    LoadInventory(player.SteamID);
                    if (!Config.LocalTestMode || Config.Database.Provider != "json") return;
                    if (player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) player.SwitchTeam(CsTeam.CounterTerrorist);
                    if (!player.PawnIsAlive) player.Respawn();
                    AddTimer(0.5f, () => { if (Human(player)) ApplyEquipped(player); });
                    AddTimer(4f, () =>
                    {
                        if (!Human(player)) return;
                        OpenMenu(player, "weapon");
                        CaptureLocalTestScreenshot(player, "zaraysk_cw_menu", 1f);
                    });
                });
            }
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot => localTestSlots.Remove(slot));
        RegisterListener<Listeners.OnClientDisconnect>(slot => { var player = Utilities.GetPlayerFromSlot(slot); if (player?.SteamID > 0) { Cleanup(player.SteamID); inventory.TryRemove(player.SteamID, out _); } });
        RegisterListener<Listeners.OnMapEnd>(() => CleanupAll());
        RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecache);
        RegisterEventHandler<EventPlayerSpawn>(OnSpawn, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>((e, info) => { if (e.Userid?.SteamID > 0) Cleanup(e.Userid.SteamID); return HookResult.Continue; }, HookMode.Post);
        AddTimer(0.05f, UpdateCosmetics, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        _ = InitializeAsync(hotReload);
    }

    public override void Unload(bool hotReload)
    {
        if (CosmeticAccess.OpenVipInventory == OpenVipInventory) CosmeticAccess.OpenVipInventory = null;
        CleanupAll();
    }

    private void OpenVipInventory(CCSPlayerController player) => OpenMenu(player, "vip");

    private void OnPrecache(ResourceManifest manifest)
    {
        foreach (var resource in Config.Models.Values.SelectMany(value => new[] { value.ModelPath, value.ParticlePath })
                     .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            manifest.AddResource(resource);
    }

    private async Task InitializeAsync(bool hotReload)
    {
        try
        {
            await RequireStore().InitializeAsync(); ready = true;
            Server.NextFrame(() => { foreach (var player in Players()) LoadInventory(player.SteamID); });
        }
        catch (Exception ex) { Logger.LogError(ex, "Cosmetics database initialization failed"); }
    }

    private void OnAuthorized(int slot, SteamID steamId) => LoadInventory(steamId.SteamId64);
    private void LoadInventory(ulong steamId)
    {
        if (!ready || steamId == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var items = await RequireStore().GetAsync(steamId);
                Server.NextFrame(() =>
                {
                    inventory[steamId] = items;
                    Logger.LogInformation("Loaded {Count} active cosmetics for {SteamId}", items.Count, steamId);
                    var player = Players().FirstOrDefault(candidate => candidate.SteamID == steamId);
                    if (player is { PawnIsAlive: true }) ApplyEquipped(player);
                });
            }
            catch (Exception ex) { Logger.LogError(ex, "Could not load cosmetics for {SteamId}", steamId); }
        });
    }

    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (Human(player)) AddTimer(0.25f, () => ApplyEquipped(player!));
        return HookResult.Continue;
    }

    private void ApplyEquipped(CCSPlayerController player)
    {
        Cleanup(player.SteamID);
        var items = EffectiveItems(player);
        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid) return;
        foreach (var grant in items.Where(item => item.Equipped && (item.ExpiresAt is null || item.ExpiresAt > DateTime.UtcNow)))
        {
            if (!Config.Models.TryGetValue(grant.CosmeticId, out var definition)
                || definition.Team is 2 or 3 && definition.Team != player.TeamNum) continue;
            switch (definition.Type)
            {
                case "player": pawn.SetModel(definition.ModelPath); break;
                case "weapon": break;
                case "aura": SpawnAura(player, definition); break;
                default: SpawnProp(player, definition); break;
            }
        }
        ApplyWeapon(player);
    }

    private void SpawnProp(CCSPlayerController player, CosmeticDefinition definition)
    {
        var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (prop is null) return;
        prop.CreateNonSolid = true;
        if (definition.Type == "pet")
        {
            prop.UseAnimGraph = true;
            prop.AnimatedEveryTick = true;
            prop.IdleAnim = string.IsNullOrWhiteSpace(definition.PetIdleAnimation) ? "idle" : definition.PetIdleAnimation;
        }
        prop.SetModel(definition.ModelPath);
        prop.DispatchSpawn();
        prop.AcceptInput("DisableCollision");
        spawned[(player.SteamID, definition.Slot)] = prop;
        if (definition.Type == "pet")
        {
            var origin = player.PlayerPawn.Value?.AbsOrigin;
            if (origin is not null) petPositions[(player.SteamID, definition.Slot)] = new Vector(origin.X, origin.Y, origin.Z);
            Logger.LogInformation("Spawned pet {ModelPath} for {SteamId}; animation={Animation}, flying={Flying}",
                definition.ModelPath, player.SteamID, prop.IdleAnim, IsFlyingPet(definition));
            CaptureLocalTestScreenshot(player, "zaraysk_pet", 1.5f);
        }
        else Logger.LogInformation("Spawned {Type} {ModelPath} for {SteamId}", definition.Type, definition.ModelPath, player.SteamID);
    }

    private void SpawnAura(CCSPlayerController player, CosmeticDefinition definition)
    {
        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle is null) return;
        particle.EffectName = definition.ParticlePath;
        particle.StartActive = true;
        particle.DispatchSpawn();
        particle.AcceptInput("Start");
        spawned[(player.SteamID, definition.Slot)] = particle;
        Logger.LogInformation("Spawned aura {ParticlePath} for {SteamId}", definition.ParticlePath, player.SteamID);
        CaptureLocalTestScreenshot(player, "zaraysk_aura", 2f);
    }

    private void UpdateCosmetics()
    {
        foreach (var player in Players())
        {
            var pawn = player.PlayerPawn.Value;
            if (!player.PawnIsAlive || pawn is not { IsValid: true }) { Cleanup(player.SteamID); continue; }
            var items = EffectiveItems(player);
            var activeSlots = items.Where(item => item.Equipped && Config.Models.ContainsKey(item.CosmeticId)).Select(item => Config.Models[item.CosmeticId].Slot).ToHashSet();
            foreach (var key in spawned.Keys.Where(key => key.SteamId == player.SteamID && !activeSlots.Contains(key.Slot)).ToArray())
            {
                petPositions.Remove(key);
                if (spawned.Remove(key, out var expired) && expired.IsValid) expired.Remove();
            }
            foreach (var grant in items.Where(item => item.Equipped && item.Quantity > 0 && (item.ExpiresAt is null || item.ExpiresAt > DateTime.UtcNow) && Config.Models.ContainsKey(item.CosmeticId)))
            {
                var definition = Config.Models[grant.CosmeticId];
                if (definition.Type is "player" or "weapon") continue;
                if (definition.Team is 2 or 3 && definition.Team != player.TeamNum) continue;
                if (!spawned.TryGetValue((player.SteamID, definition.Slot), out var entity) || !entity.IsValid)
                {
                    if (definition.Type == "aura") SpawnAura(player, definition);
                    else SpawnProp(player, definition);
                    continue;
                }
                var origin = pawn.AbsOrigin; var rotation = pawn.AbsRotation;
                if (origin is null || rotation is null) continue;
                var target = CosmeticPosition(origin.X, origin.Y, origin.Z, rotation.Y, definition, Server.CurrentTime);
                if (definition.Type == "pet")
                {
                    var key = (player.SteamID, definition.Slot);
                    var current = petPositions.GetValueOrDefault(key, target);
                    var smoothing = Math.Clamp(definition.PetFollowSmoothing, 0.05f, 1f);
                    var next = new Vector(
                        current.X + (target.X - current.X) * smoothing,
                        current.Y + (target.Y - current.Y) * smoothing,
                        current.Z + (target.Z - current.Z) * smoothing);
                    petPositions[key] = next;
                    var dx = target.X - current.X; var dy = target.Y - current.Y;
                    var yaw = MathF.Abs(dx) + MathF.Abs(dy) > 0.05f ? MathF.Atan2(dy, dx) * 180f / MathF.PI : rotation.Y;
                    entity.Teleport(next, new QAngle(0, yaw, 0), null);
                }
                else entity.Teleport(target, new QAngle(0, rotation.Y, 0), null);
            }
            ApplyWeapon(player);
        }
    }

    private void ApplyWeapon(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        var weapon = pawn?.WeaponServices?.ActiveWeapon.Value;
        if (weapon is not { IsValid: true }) return;
        var items = EffectiveItems(player);
        var grant = items.FirstOrDefault(item => item.Equipped && item.Quantity > 0 && (item.ExpiresAt is null || item.ExpiresAt > DateTime.UtcNow) && Config.Models.TryGetValue(item.CosmeticId, out var value)
            && value.Type == "weapon" && (value.Team == 0 || value.Team == player.TeamNum) && WeaponMatches(value.WeaponClass, weapon.DesignerName));
        if (grant is null || !Config.Models.TryGetValue(grant.CosmeticId, out var definition)) { RestoreWeapon(player.SteamID); return; }
        if (appliedWeapons.TryGetValue(player.SteamID, out var applied) && applied.Weapon.IsValid && applied.Weapon.Handle == weapon.Handle && applied.CosmeticId == grant.CosmeticId)
        {
            // CS2 recreates/resets the first-person entity on weapon switches. Reassert it
            // without rebuilding the tracked world-model state.
            var currentViewModel = TryGetViewModel(player);
            var currentViewModelName = currentViewModel?.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
            if (currentViewModel is { IsValid: true } && currentViewModelName != definition.ModelPath)
                currentViewModel.SetModel(definition.ModelPath);
            return;
        }
        RestoreWeapon(player.SteamID);
        var original = weapon.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName ?? "";
        var viewModel = TryGetViewModel(player);
        var originalViewModel = viewModel?.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName ?? "";
        weapon.SetModel(definition.ModelPath);
        if (viewModel is { IsValid: true }) viewModel.SetModel(definition.ModelPath);
        appliedWeapons[player.SteamID] = new(weapon, viewModel, grant.CosmeticId, original, originalViewModel);
        Logger.LogInformation("Applied custom weapon {CosmeticId} ({ModelPath}) to {SteamId}; entity={Entity}; worldOriginal={OriginalModel}; viewOriginal={OriginalViewModel}",
            grant.CosmeticId, definition.ModelPath, player.SteamID, weapon.Index, original, originalViewModel);
        CaptureLocalTestScreenshot(player, "zaraysk_custom_weapon", 1f);
    }

    public static bool WeaponMatches(string configured, string actual) =>
        string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase) || configured == "weapon_knife"
        && (actual.StartsWith("weapon_knife", StringComparison.OrdinalIgnoreCase) || actual.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase));

    public static bool IsFlyingPet(CosmeticDefinition definition) => definition.PetFlying
        ?? definition.ModelPath.Contains("flying", StringComparison.OrdinalIgnoreCase)
        || definition.ModelPath.Contains("_fly", StringComparison.OrdinalIgnoreCase);

    public static Vector CosmeticPosition(float originX, float originY, float originZ, float yaw, CosmeticDefinition definition, float time)
    {
        var radians = yaw * MathF.PI / 180f;
        var forward = definition.Type == "pet" && definition.OffsetForward == 0 ? -55f : definition.OffsetForward;
        var right = definition.Type == "pet" && definition.OffsetRight == 0 ? 28f : definition.OffsetRight;
        var up = definition.Type == "pet" && definition.OffsetUp == 0 ? (IsFlyingPet(definition) ? 48f : 8f) : definition.OffsetUp;
        if (definition.Type == "pet" && IsFlyingPet(definition)) up += 40f + MathF.Sin(time * 2.4f) * 4f;
        return new Vector(
            originX + MathF.Cos(radians) * forward - MathF.Sin(radians) * right,
            originY + MathF.Sin(radians) * forward + MathF.Cos(radians) * right,
            originZ + up);
    }

    private static CBaseModelEntity? TryGetViewModel(CCSPlayerController player)
    {
        try
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn is not { IsValid: true }) return null;
            var services = Marshal.ReadIntPtr(pawn.Handle + Schema.GetSchemaOffset("CBasePlayerPawn", "m_pViewModelServices"));
            if (services == IntPtr.Zero) return null;
            var field = services + Schema.GetSchemaOffset("CCSPlayer_ViewModelServices", "m_hViewModel");
            return new CHandle<CBaseModelEntity>(field).Value;
        }
        catch { return null; }
    }

    private void RestoreWeapon(ulong steamId)
    {
        if (!appliedWeapons.Remove(steamId, out var applied)) return;
        if (applied.Weapon.IsValid && !string.IsNullOrWhiteSpace(applied.OriginalWorldModel))
            applied.Weapon.SetModel(applied.OriginalWorldModel);
        if (applied.ViewModel is { IsValid: true } && !string.IsNullOrWhiteSpace(applied.OriginalViewModel))
            applied.ViewModel.SetModel(applied.OriginalViewModel);
    }

    private void Cleanup(ulong steamId)
    {
        foreach (var key in spawned.Keys.Where(key => key.SteamId == steamId).ToArray())
        {
            petPositions.Remove(key);
            if (spawned.Remove(key, out var entity) && entity.IsValid) entity.Remove();
        }
        RestoreWeapon(steamId);
    }

    private void CleanupAll()
    {
        foreach (var entity in spawned.Values) if (entity.IsValid) entity.Remove();
        spawned.Clear();
        petPositions.Clear();
        foreach (var steamId in appliedWeapons.Keys.ToArray()) RestoreWeapon(steamId);
    }

    private void OnMenuCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!Human(player)) { info.ReplyToCommand("Команда доступна только игроку."); return; }
        OpenMenu(player!);
    }

    private void OpenMenu(CCSPlayerController player, string? type = null)
    {
        var menu = new CosmeticMenu(this, type == "weapon" ? "Кастомное оружие | !cw" : type == "vip" ? "VIP | Кастомные скины" : "Косметика | Мой инвентарь");
        var items = EffectiveItems(player, type == "vip", type != "weapon");
        menu.Add(type == "weapon" ? "Снять кастомное оружие" : "Снять всю косметику", p => Equip(p, null, type), false);
        foreach (var grant in items.Where(item => item.Quantity > 0 && (item.ExpiresAt is null || item.ExpiresAt > DateTime.UtcNow)))
        {
            if (!Config.Models.TryGetValue(grant.CosmeticId, out var definition)) continue;
            if (type is not (null or "vip") && definition.Type != type) continue;
            var marker = grant.Equipped ? "[НАДЕТО] " : "";
            var expiry = grant.ExpiresAt is null ? "навсегда" : grant.ExpiresAt.Value.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            menu.Add($"{marker}[{TypeName(definition.Type)}] {definition.DisplayName} x{grant.Quantity} ({expiry})", p => Equip(p, grant.CosmeticId, type), definition.Team is 2 or 3 && definition.Team != player.TeamNum);
        }
        menu.Open(player);
    }

    private void CaptureLocalTestScreenshot(CCSPlayerController player, string name, float delay)
    {
        if (!Config.LocalTestMode || Config.Database.Provider != "json" || !localTestCaptures.Add(name)) return;
        AddTimer(delay, () =>
        {
            if (!Human(player)) return;
            player.ExecuteClientCommand("jpeg " + name);
            Logger.LogInformation("Requested local QA screenshot {Screenshot}", name);
        });
    }

    private void Equip(CCSPlayerController player, string? id, string? type = null)
    {
        var vipIds = CosmeticAccess.VipCatalog?.Invoke(player) ?? Array.Empty<string>();
        if (id is null && type == "vip") { vipEquipped.Remove(player.SteamID); ApplyEquipped(player); OpenMenu(player, type); return; }
        if (type == "vip" && id is not null && vipIds.Contains(id) && Config.Models.TryGetValue(id, out var vipDefinition))
        {
            if (!vipEquipped.TryGetValue(player.SteamID, out var selected)) vipEquipped[player.SteamID] = selected = new();
            selected[vipDefinition.Slot] = id;
            ApplyEquipped(player); OpenMenu(player, type); return;
        }
        if (id is not null && Config.Models.TryGetValue(id, out var ownedDefinition) && vipEquipped.TryGetValue(player.SteamID, out var vipSelection)) vipSelection.Remove(ownedDefinition.Slot);
        if (id is null && vipEquipped.TryGetValue(player.SteamID, out var previous))
            foreach (var slot in previous.Keys.Where(slot => type is null || slot.StartsWith(type + ":") || slot == type).ToArray()) previous.Remove(slot);
        _ = Task.Run(async () =>
        {
            try
            {
                var slotIds = id is null ? Config.Models.Where(pair => type is null || pair.Value.Type == type).Select(pair => pair.Key).ToArray() : Config.Models
                    .Where(pair => pair.Value.Slot == Config.Models[id].Slot).Select(pair => pair.Key).ToArray();
                await RequireStore().EquipAsync(player.SteamID, id, slotIds);
                var items = await RequireStore().GetAsync(player.SteamID);
                Server.NextFrame(() => { if (!Human(player)) return; inventory[player.SteamID] = items; ApplyEquipped(player); OpenMenu(player, type); });
            }
            catch (Exception ex) { Logger.LogError(ex, "Could not equip cosmetic"); }
        });
    }

    private static string TypeName(string type) => type switch
    {
        "player" => "персонаж", "hat" => "голова", "backpack" => "спина", "pet" => "питомец",
        "weapon" => "оружие", "aura" => "аура", _ => type
    };

    private void OnGiveCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Manage(caller)) { info.ReplyToCommand("Нет доступа."); return; }
        if (info.ArgCount != 5 || !ulong.TryParse(info.GetArg(1), out var steam) || !int.TryParse(info.GetArg(3), out var quantity) || !int.TryParse(info.GetArg(4), out var days) || quantity < 1 || days < 0)
        { info.ReplyToCommand("Использование: css_zskin_give <steam64> <model> <quantity> <days, 0=навсегда>"); return; }
        var id = info.GetArg(2); if (!Config.Models.ContainsKey(id)) { info.ReplyToCommand("Неизвестная модель: " + string.Join(", ", Config.Models.Keys)); return; }
        _ = Task.Run(async () => { try { await RequireStore().GrantAsync(steam, id, quantity, days); Server.NextFrame(() => { LoadInventory(steam); info.ReplyToCommand("Модель выдана."); }); } catch (Exception ex) { Logger.LogError(ex, "Could not grant cosmetic"); } });
    }

    private void OnTakeCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Manage(caller)) { info.ReplyToCommand("Нет доступа."); return; }
        if (info.ArgCount != 4 || !ulong.TryParse(info.GetArg(1), out var steam) || !int.TryParse(info.GetArg(3), out var quantity) || quantity < 1)
        { info.ReplyToCommand("Использование: css_zskin_take <steam64> <model> <quantity>"); return; }
        var id = info.GetArg(2);
        _ = Task.Run(async () => { try { await RequireStore().RemoveAsync(steam, id, quantity); Server.NextFrame(() => { LoadInventory(steam); info.ReplyToCommand("Количество модели уменьшено."); }); } catch (Exception ex) { Logger.LogError(ex, "Could not remove cosmetic"); } });
    }

    private void OnReloadCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!Manage(caller)) { info.ReplyToCommand("Нет доступа."); return; }
        foreach (var player in Players()) LoadInventory(player.SteamID);
        info.ReplyToCommand("Инвентари обновляются.");
    }

    private CosmeticsStore RequireStore() => store ?? throw new InvalidOperationException("Config not loaded.");
    private bool Manage(CCSPlayerController? player) => player is null || AdminManager.PlayerHasPermissions(player, Config.AdminPermission);
    private static bool Human(CCSPlayerController? player) => player is { IsValid: true, IsBot: false, IsHLTV: false };
    private static IEnumerable<CCSPlayerController> Players() => Utilities.GetPlayers().Where(Human);

    private sealed class CosmeticMenu(ZarayskCosmeticsPlugin plugin, string title)
    {
        private readonly List<(string Text, Action<CCSPlayerController> Callback, bool Disabled)> items = [];
        public void Add(string text, Action<CCSPlayerController> callback, bool disabled) => items.Add((text, callback, disabled));
        public void Open(CCSPlayerController player)
        {
            if (MenuService.Current is not null)
            {
                var menu = new ZarayskBaseMenu(title);
                menu.BackAction = p =>
                {
                    if (title.StartsWith("VIP |", StringComparison.Ordinal) && CosmeticAccess.OpenVipMenu is { } back) back(p);
                    else MenuService.Current?.Open(p, "skins");
                };
                foreach (var item in items) { var captured = item; menu.AddItem(captured.Text, (p, _) => captured.Callback(p), captured.Disabled ? DisableOption.Disabled : DisableOption.None); }
                menu.Display(player, 0); return;
            }
            var fallback = new CenterHtmlMenu(title, plugin);
            foreach (var item in items) { var captured = item; fallback.AddMenuOption(captured.Text, (p, _) => captured.Callback(p), captured.Disabled); }
            fallback.Open(player);
        }
    }
}
