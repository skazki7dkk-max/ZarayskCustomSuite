using CounterStrikeSharp.API.Core;
using ZarayskMenu.Api;
namespace ZarayskVipDemo;
public sealed class Plugin : BasePlugin
{
    public override string ModuleName => "ZarayskVipDemo";
    public override string ModuleVersion => "0.1.0";
    IDisposable? registration;
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        registration?.Dispose();
        var api = MenuService.Current ?? throw new InvalidOperationException("Load ZarayskMenu first");
        registration = api.Register(new MenuDefinition
        {
            Id = "vip", Title = "VIP — пример API",
            Items = [new() { Id = "status", Text = "Проверить доступ", Permission = "@css/reservation" },
                     new() { Id = "help", Text = "О VIP-модуле" }]
        }, (player, item) => player.PrintToChat(item == "status" ? "VIP: доступ разрешён. Здесь будет действие VIP-плагина." : "Демонстрация API. Выдача привилегий ещё не реализована."));
    }
    public override void Unload(bool hotReload) => registration?.Dispose();
}
