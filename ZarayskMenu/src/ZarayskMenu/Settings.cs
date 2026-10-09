using System.Text.Json;
using System.Text.RegularExpressions;
using ZarayskMenu.Api;

namespace ZarayskMenu;

public sealed class Settings
{
    public string RenderMode { get; set; } = "panorama";
    public string Title { get; set; } = "Зарайск | Меню";
    public string Theme { get; set; } = "gold";
    public string Placement { get; set; } = "standard";
    public int PageSize { get; set; } = 7;
    public int TimeoutSeconds { get; set; } = 45;
    public int ClickCooldownMs { get; set; } = 180;
    public bool CaptureMouse { get; set; } = false;
    public bool KeyboardNavigationEnabled { get; set; } = true;
    public double ExitHoldSeconds { get; set; } = 2.0;
    public bool RebuildOnConnect { get; set; } = true;
    public bool PriorityEnabled { get; set; } = true;
    public bool SuppressOtherHudInput { get; set; } = true;
    public string OpenSound { get; set; } = "sounds/ui/panorama/itemtile_click_02.vsnd";
    public string MoveSound { get; set; } = "sounds/ui/panorama/itemtile_plastic_rollover_15.vsnd";
    public string SelectSound { get; set; } = "sounds/ui/panorama/itemtile_click_02.vsnd";
    public string BackSound { get; set; } = "sounds/ui/panorama/inventory_item_close_01.vsnd";
    public string ExitSound { get; set; } = "sounds/ui/panorama/mainmenu_press_quit_02.vsnd";
    public float SoundVolume { get; set; } = 0.45f;
    public Dictionary<string, string> RequiredPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MenuDefinition> Menus { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static bool Identifier(string id) => Regex.IsMatch(id, @"\A[a-zA-Z][a-zA-Z0-9_]{0,47}\z");
    public static void ValidateMenu(MenuDefinition menu, bool callback)
    {
        if (!Identifier(menu.Id) || menu.Title.Length > 100 || menu.Items.Count > (callback ? 20000 : 500))
            throw new InvalidDataException("Invalid menu id/title/item count");
        var ids = new HashSet<string>();
        foreach (var i in menu.Items)
        {
            if (!Identifier(i.Id) || !ids.Add(i.Id) || i.Text.Length > 150)
                throw new InvalidDataException($"Invalid/duplicate item in {menu.Id}");
            var targets = (i.Submenu.Length > 0 ? 1 : 0) + (i.ServerCommand.Length > 0 ? 1 : 0) + (i.PlayerCommand.Length > 0 ? 1 : 0) + (i.Message.Length > 0 ? 1 : 0);
            if (targets > 1 || (targets == 0 && !callback && i.Enabled))
                throw new InvalidDataException($"Item {i.Id} needs exactly one action");
            if (i.Submenu.Length > 0 && !Identifier(i.Submenu)) throw new InvalidDataException("Invalid submenu");
            // Deliberately no semicolons, quotes, slashes or arbitrary string interpolation.
            var command = i.ServerCommand.Replace("{slot}", "0").Replace("{userid}", "0").Replace("{steamid}", "0");
            if (command.Length > 256 || (command.Length > 0 && !Regex.IsMatch(command, @"\A[a-zA-Z_][a-zA-Z0-9_]*( [a-zA-Z0-9_-]+)*\z")))
                throw new InvalidDataException($"Unsafe command in {i.Id}");
            if (i.PlayerCommand.Length > 256 || (i.PlayerCommand.Length > 0 && !Regex.IsMatch(i.PlayerCommand, @"\Acss_[a-zA-Z0-9_]+( [a-zA-Z0-9_-]+)*\z")))
                throw new InvalidDataException($"Unsafe player command in {i.Id}");
        }
    }
    public void Validate()
    {
        if (RenderMode is not ("html" or "panorama")) throw new InvalidDataException("RenderMode must be html or panorama");
        if (Title.Length is 0 or > 100 || Theme is not ("gold" or "ice" or "mono") || Placement is not ("standard" or "compact"))
            throw new InvalidDataException("Invalid title/theme/placement");
        if (PageSize is < 1 or > 7 || TimeoutSeconds is < 5 or > 600 || ClickCooldownMs is < 100 or > 2000
            || ExitHoldSeconds is < 0.5 or > 10.0 || SoundVolume is < 0 or > 1
            || new[] { OpenSound, MoveSound, SelectSound, BackSound, ExitSound }.Any(sound => sound.Length > 160 || sound.Contains('\n') || sound.Contains('\r')
                || sound.Length > 0 && !Regex.IsMatch(sound, @"\A(?:sounds/[a-zA-Z0-9_./-]+\.vsnd|[a-zA-Z][a-zA-Z0-9_.-]*)\z")))
            throw new InvalidDataException("Invalid page size, timeout or cooldown");
        foreach (var requirement in RequiredPlugins)
        {
            var parts = requirement.Key.Split('.');
            if (parts.Length != 2 || !Identifier(parts[0]) || !Identifier(parts[1])
                || !Regex.IsMatch(requirement.Value, @"\A[a-zA-Z][a-zA-Z0-9_.]{0,127}\z"))
                throw new InvalidDataException("Invalid required plugin mapping");
        }
        var ids = new HashSet<string>();
        foreach (var m in Menus) { ValidateMenu(m, false); if (!ids.Add(m.Id)) throw new InvalidDataException("Duplicate menu"); }
        if (!ids.Contains("main")) throw new InvalidDataException("main menu required");
    }
}
