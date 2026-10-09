using CounterStrikeSharp.API.Core;

// Narrow ABI adapter for the supplied WeaponPaints build. Its assembly references
// are patched to these types; no replacement CS2MenuManager.dll is installed.
namespace ZarayskMenu.Api.Compatibility.Enum
{
    public enum DisableOption { None = 0, Disabled = 1, Hidden = 2 }
}
namespace ZarayskMenu.Api.Compatibility.Interface
{
    public interface IMenu
    {
        Class.ItemOption AddItem(string text, Action<CCSPlayerController, Class.ItemOption> action, Enum.DisableOption disabled);
        void Display(CCSPlayerController player, int selectedIndex);
    }
}
namespace ZarayskMenu.Api.Compatibility.Class
{
    public sealed class ItemOption
    {
        public string Text { get; init; } = "";
        public string OptionDisplay => Text;
        public string SubOptionDisplay => "";
        public Enum.DisableOption Disabled { get; init; }
        public Action<CCSPlayerController, ItemOption>? OnChoose { get; init; }
    }
    public class BaseMenu : Interface.IMenu
    {
        public string Title { get; }
        public Action<CCSPlayerController>? BackAction { get; set; }
        public List<ItemOption> Options { get; } = [];
        public BaseMenu(string title) => Title = title;
        public ItemOption AddItem(string text, Action<CCSPlayerController, ItemOption> action, Enum.DisableOption disabled)
        {
            var item = new ItemOption { Text = text, OnChoose = action, Disabled = disabled };
            Options.Add(item); return item;
        }
        public void Display(CCSPlayerController player, int selectedIndex) => MenuService.OpenExternal(player, this, selectedIndex);
    }
    public static class MenuTypeManager
    {
        public static BaseMenu MenuTypeMenuByType(Type type, CCSPlayerController player, BasePlugin plugin, Interface.IMenu? parent)
        {
            var menu = new BaseMenu("Скины | ZarayskMenu");
            menu.AddItem("Открыть главное меню", (p, _) => MenuService.Current?.Open(p), Enum.DisableOption.None);
            return menu;
        }
    }
}
namespace ZarayskMenu.Api.Compatibility.Menu
{
    public sealed class PlayerMenu : Class.BaseMenu
    {
        public PlayerMenu(string title, BasePlugin plugin) : base(title) { }
    }
}
