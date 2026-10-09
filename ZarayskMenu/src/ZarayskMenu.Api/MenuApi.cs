using CounterStrikeSharp.API.Core;

namespace ZarayskMenu.Api;

// Put this assembly in counterstrikesharp/shared/ZarayskMenu.Api, never in each plugin directory.
public static class MenuService
{
    public static IMenuService? Current { get; set; }

    // Binary bridge used by the supplied Warcraft 4.2.0 integration patch.
    public static void OpenExternal(CCSPlayerController player, object menu, int selectedIndex)
    {
        if (Current is IExternalMenuService external) external.OpenExternal(player, menu, selectedIndex);
        else if (player is { IsValid: true }) player.PrintToChat("Для меню требуется загруженный ZarayskMenu.");
    }
    public static void CloseExternal(CCSPlayerController player) => Current?.Close(player);
}

public interface IExternalMenuService
{
    void OpenExternal(CCSPlayerController player, object menu, int selectedIndex);
}

public interface IMenuService
{
    // All methods must run on the CS2 server thread. Dispose before the provider unloads.
    IDisposable Register(MenuDefinition menu, Action<CCSPlayerController, string> selected);
    bool Open(CCSPlayerController player, string menuId = "main");
    void Close(CCSPlayerController player);
}

public sealed class MenuDefinition
{
    public string Id { get; set; } = "main";
    public string Title { get; set; } = "Главное меню";
    public string Permission { get; set; } = "";
    public List<MenuItem> Items { get; set; } = [];
}

public sealed class MenuItem
{
    public string Id { get; set; } = "item";
    public string Text { get; set; } = "Пункт";
    public string Permission { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool CloseOnSelect { get; set; } = true;
    // Exactly one target: submenu, fixed server command, local message, or API callback.
    public string Submenu { get; set; } = "";
    public string ServerCommand { get; set; } = "";
    public string PlayerCommand { get; set; } = "";
    public string Message { get; set; } = "";
}
