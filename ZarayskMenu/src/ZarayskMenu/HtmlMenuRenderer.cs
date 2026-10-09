using System.Net;
using System.Text;
using ZarayskMenu.Api;

namespace ZarayskMenu;

internal static class HtmlMenuRenderer
{
    internal static string Render(string title, string subtitle, IReadOnlyList<MenuItem> items, int cursor, int page, int pageSize)
    {
        var pages = Math.Max(1, (items.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var output = new StringBuilder();
        output.Append("<font color='#b477ff'>").Append(Escape(title)).Append("</font><br>");
        output.Append("<font color='#53b9ef'>").Append(Escape(subtitle)).Append(" · ")
            .Append(page + 1).Append('/').Append(pages).Append("</font><br>");
        var start = page * pageSize;
        for (var index = start; index < Math.Min(start + pageSize, items.Count); index++)
        {
            var item = items[index];
            var text = item.Text.Length > 52 ? item.Text[..51] + "…" : item.Text;
            var color = !item.Enabled ? "#888888" : index == cursor ? "#ffffff" : "#7eff34";
            output.Append("<font color='").Append(color).Append("'>")
                .Append(index == cursor ? "➤ " : "　 ").Append(index - start + 1).Append(". ")
                .Append(Escape(text)).Append("</font><br>");
        }
        if (items.Count == 0) output.Append("<font color='#aaaaaa'>Нет доступных пунктов</font><br>");
        output.Append("<font color='#53b9ef'>[W] ↑　[S] ↓　[E] Выбрать　[R] Назад</font><br>")
            .Append("<font color='#b477ff'>[F] Удерживать — выход　[9] След. страница</font>");
        return output.ToString();
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
