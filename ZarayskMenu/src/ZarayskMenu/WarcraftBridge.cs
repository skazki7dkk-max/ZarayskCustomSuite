using System.Collections;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core;
using ZarayskMenu.Api;

namespace ZarayskMenu;

public sealed partial class Plugin
{
    readonly Dictionary<int, IDisposable> externalRegistrations = [];
    static object? Property(object value, string name) => value.GetType().GetProperty(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value);
    internal static string Plain(string? html, int limit)
    {
        var text = Regex.Replace(html ?? "", "<br\\s*/?>", " · ", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", ""));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= limit ? text : text[..(limit - 1)] + "…";
    }
    static IEnumerable<string> Wrap(string text)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > 29) { yield return line; line = ""; }
            line += (line.Length > 0 ? " " : "") + word;
        }
        if (line.Length > 0) yield return line;
    }
    public void OpenExternal(CCSPlayerController player, object source, int selectedIndex)
    {
        if (!active || !Human(player)) return;
        bool isWarcraft = source.GetType().FullName == "WarcraftPlugin.Menu.Menu";
        if (!isWarcraft && source is not ZarayskMenu.Api.Compatibility.Class.BaseMenu) throw new ArgumentException("Unsupported external menu");
        Close(player);
        var options = ((IEnumerable?)Property(source, "Options"))?.Cast<object>().ToArray() ?? [];
        var id = "external_live_" + player.Slot;
        var menu = new MenuDefinition { Id = id, Title = Plain(Property(source, "Title")?.ToString(), 100) };
        Action<CCSPlayerController, string> selected = (p, item) =>
        {
            if (p.Slot != player.Slot || p.Handle != player.Handle) return;
            if (item == "home") { Open(p); return; }
            if (!item.StartsWith("option_") || !int.TryParse(item[7..], out var index) || index < 0 || index >= options.Length) return;
            var option = options[index];
            if (option is ZarayskMenu.Api.Compatibility.Class.ItemOption disabled && disabled.Disabled != ZarayskMenu.Api.Compatibility.Enum.DisableOption.None) return;
            var description = Plain(Property(option, "SubOptionDisplay")?.ToString(), 600);
            if (description.Length > 0) p.PrintToChat(description);
            // Keep the current page for rejected choices. A valid callback replaces or closes it.
            (Property(option, "OnChoose") as Delegate)?.DynamicInvoke(p, option);
        };
        var registrations = new List<IDisposable>();
        var registration = new Registration(() => { foreach (var item in registrations) item.Dispose(); });
        try
        {
            for (int i = 0; i < options.Length; i++)
            {
                var option = options[i];
                var item = new MenuItem { Id = "option_" + i, Text = Plain(Property(option, "OptionDisplay")?.ToString(), 150), CloseOnSelect = false };
                if (option is ZarayskMenu.Api.Compatibility.Class.ItemOption compatible)
                    item.Enabled = compatible.Disabled == ZarayskMenu.Api.Compatibility.Enum.DisableOption.None;
                var description = Plain(Property(option, "SubOptionDisplay")?.ToString(), 600);
                var callback = Property(option, "OnChoose") as Delegate;
                if (description.Length > 0 && callback?.Method.DeclaringType?.FullName?.Contains(".ShopMenu") == true)
                {
                    // Read the complete price and description before buying in the narrow menu.
                    var detail = new MenuDefinition { Id = id + "_item_" + i, Title = "Описание предмета" };
                    detail.Items.Add(new() { Id = item.Id, Text = "Купить предмет", CloseOnSelect = false });
                    int n = 0;
                    foreach (var line in Wrap(item.Text + " · " + description))
                        detail.Items.Add(new() { Id = "info_" + n++, Text = line, Message = line, CloseOnSelect = false });
                    registrations.Add(Register(detail, selected));
                    item.Submenu = detail.Id;
                }
                menu.Items.Add(item);
            }
            if (isWarcraft)
            {
                menu.Items.Add(new() { Id = "guide", Text = "Помощь по навыкам", Submenu = "warcraft_help" });
                menu.Items.Add(new() { Id = "home", Text = "Главное меню сервера" });
            }
            registrations.Add(Register(menu, selected));
            if (!Open(player, id)) { registration.Dispose(); return; }
            externalRegistrations[player.Slot] = registration;
        }
        catch { registration.Dispose(); throw; }
        if (sessions.TryGetValue(player.Slot, out var session))
        {
            session.OnBack = source is ZarayskMenu.Api.Compatibility.Class.BaseMenu compatible
                ? compatible.BackAction : null;
            session.Page = Math.Max(0, selectedIndex) / settings.PageSize;
            Draw(player);
        }
    }
    void ReleaseExternal(int slot)
    {
        if (externalRegistrations.Remove(slot, out var registration)) registration.Dispose();
    }
    void ClearExternal()
    {
        foreach (var slot in externalRegistrations.Keys.ToArray()) ReleaseExternal(slot);
    }
}
