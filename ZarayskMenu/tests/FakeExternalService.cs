using CounterStrikeSharp.API.Core;
using ZarayskMenu.Api;
sealed class FakeExternalService : IMenuService, IExternalMenuService
{
    public object? Source;
    public int Selected;
    public IDisposable Register(MenuDefinition menu, Action<CCSPlayerController,string> action) => throw new NotSupportedException();
    public bool Open(CCSPlayerController player, string id="main") => true;
    public void Close(CCSPlayerController player) { }
    public void OpenExternal(CCSPlayerController player, object menu, int selectedIndex) { Source=menu; Selected=selectedIndex; }
}
