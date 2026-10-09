using System.Reflection;
using CounterStrikeSharp.API.Core;

namespace ZarayskMenu;

// Optional integration: do not ship/replace the server's CS2MenuManager DLL.
// Match the public API signature; forks with another API remain unsupported and are reported.
internal static class ExternalMenus
{
    internal static IEnumerable<MethodInfo> CloseMethods()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("CS2MenuManager.API.Class.MenuManager", false);
            var method = type?.GetMethod("CloseActiveMenu", BindingFlags.Public | BindingFlags.Static,
                null, [typeof(CCSPlayerController)], null);
            if (method != null && method.ReturnType == typeof(void)) yield return method;
        }
    }
    internal static void Close(CCSPlayerController player, Action<Exception> error)
    {
        foreach (var method in CloseMethods())
            try { method.Invoke(null, [player]); }
            catch (Exception ex) { error(ex); }
    }
}
