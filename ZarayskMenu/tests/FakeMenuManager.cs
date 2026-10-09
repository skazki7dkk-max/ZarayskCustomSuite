using CounterStrikeSharp.API.Core;
namespace CS2MenuManager.API.Class;
// Contract test double; never distributed as a server library.
public static class MenuManager
{
    public static int Calls;
    public static bool Throw;
    public static void CloseActiveMenu(CCSPlayerController player)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("Simulated external failure");
    }
}
