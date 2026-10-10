using System.Text.Json;
using System.Xml.Linq;
using ZarayskMenu;
using ZarayskMenu.Api;
var root = Path.GetFullPath(args[0]);
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Accepted: " + name); }
var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "config/menu.json")), Settings.Json)!;
Check(config.RenderMode == "panorama", "user Panorama renderer remains the default");
var rendererItems = Enumerable.Range(0, 8).Select(i => new MenuItem { Id = "row_" + i, Text = "ROW" + i }).ToArray();
var secondPage = HtmlMenuRenderer.Render("Title", "Menu", rendererItems, 7, 1, 7);
Check(secondPage.Contains("ROW7") && !secondPage.Contains("ROW0") && secondPage.Contains("2/2"), "HTML pagination matches selectable rows");
var escapedPage = HtmlMenuRenderer.Render("<script>", "&", [new MenuItem { Text = "<font color='red'>unsafe</font>" }], 0, 0, 7);
Check(!escapedPage.Contains("<script>") && !escapedPage.Contains("<font color='red'>") && escapedPage.Contains("&lt;"), "HTML labels cannot inject markup");
Check(HtmlMenuRenderer.Render("Title", "Menu", [], 0, 0, 7).Contains("Нет доступных пунктов"), "empty HTML menu remains visible");
config.Validate(); Check(config.Title == "Зарайск | Меню", "default config/title");
var mainItems = config.Menus.Single(menu => menu.Id == "main").Items;
var controlItems = config.Menus.Single(menu => menu.Id == "help").Items;
Check(mainItems.Single(item => item.Id == "help").Submenu == "help" && controlItems.Count == 2 && controlItems.All(item => !item.CloseOnSelect) && controlItems.Select(item => item.PlayerCommand).Order().SequenceEqual(new[] { "css_zmenu_mode keyboard", "css_zmenu_mode mouse" }), "control submenu offers explicit keyboard and mouse choices without closing the session");
Check(mainItems.Single(item => item.Id == "vip").PlayerCommand == "css_vip", "VIP routes to plugin instead of missing submenu");
Check(mainItems.All(item => item.Id is not ("knives" or "rules")), "main menu omits knives and rules");
Check(config.RequiredPlugins["main.warcraft"] == "WarcraftPlugin", "Warcraft item requires WarcraftPlugin");
var adminItem = mainItems.Single(item => item.Id == "admin");
Check(adminItem.Text == "Админ-панель" && adminItem.Permission == "@zaraysk/menu" && adminItem.PlayerCommand == "css_admin", "admin panel is renamed and permission protected");
Check(config.PriorityEnabled && config.SuppressOtherHudInput, "priority defaults also apply to old JSON");
Check(config.KeyboardNavigationEnabled && !config.CaptureMouse && config.ExitHoldSeconds == 2.0, "W/S/E and hold-F controls enabled by default");
Check(ExternalMenus.CloseMethods().Count() == 1, "optional CS2MenuManager API discovery");
int errors = 0;
ExternalMenus.Close(null!, _ => errors++);
Check(CS2MenuManager.API.Class.MenuManager.Calls == 1 && errors == 0, "external close dispatch");
CS2MenuManager.API.Class.MenuManager.Throw = true;
ExternalMenus.Close(null!, _ => errors++);
Check(errors == 1, "external failure contained");
MenuDefinition Command(string text) => new() { Items = [new() { ServerCommand = text }] };
foreach(var cmd in new[] {"echo ok;quit", "echo ok\nquit", "echo ok\n", "echo {name}", "echo \"bad\"", "echo $(bad)"})
    Reject(() => Settings.ValidateMenu(Command(cmd), false), "reject command injection " + cmd.Replace('\n',' '));
Settings.ValidateMenu(Command("native_action {slot} {userid} {steamid}"), false); Check(true, "numeric bridge tokens");
Reject(() => Settings.ValidateMenu(new() { Items = [new() { Id="a", Message="a" }, new() { Id="a", Message="b" }] }, false), "duplicate item ids");
Reject(() => Settings.ValidateMenu(new() { Items = [new() { Message="a", Submenu="main" }] }, false), "ambiguous action");
Reject(() => Settings.ValidateMenu(new() { Items = [new()] }, false), "missing config action");
Settings.ValidateMenu(new() { Items = [new()] }, true); Check(true, "API callback action");
config.PageSize = 8; Reject(config.Validate, "reserve keys 8/9/0"); config.PageSize = 7;
config.Menus.Add(new() { Id="main" }); Reject(config.Validate, "duplicate menus");
var xml = XDocument.Load(Path.Combine(root,"panorama/layout/custom_game/zaraysk_menu.xml"));
var ids = xml.Descendants().Attributes("id").Select(a => a.Value).ToArray();
Check(ids.Length == ids.Distinct().Count(), "unique Panorama panel ids");
Check(Enumerable.Range(1,7).All(n=>ids.Contains("key_"+n)) && !ids.Contains("key_9") && xml.Descendants().Where(e => (string?)e.Attribute("id") is "key_8" or "key_0").All(e => ((string?)e.Attribute("class") ?? "").Split(' ').Contains("control-hint")), "back and exit stay in existing key hints, not additional rows");
Check(new[]{"hint_up","hint_down","hint_select","hint_back","hint_exit","control_mode"}.All(ids.Contains), "keyboard, back and current control mode hints exist");
Check(xml.Root!.Element("Panel")!.Attribute("id") == null, "root has no forbidden id");
Check(xml.Descendants().All(e=>new[]{"root","styles","include","Panel","Label","Button","Image"}.Contains(e.Name.LocalName)), "allowed panel types only");
Check(File.ReadAllText(Path.Combine(root, "panorama/styles/custom_game/zaraysk_menu.css")).Contains("@keyframes 'zaraysk_rgb'"), "runtime CSS keyframe name is quoted");
Check(!System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(Path.Combine(root, "panorama/styles/custom_game/zaraysk_menu.css")), @"(?m)^[^{}]*[+~][^{}]*\{"), "Panorama selectors avoid unsupported sibling combinators that hide the entire HUD");
Check(xml.Descendants().Where(e => (string?)e.Attribute("id") is "hint_down" or "key_8").All(e => ((string?)e.Attribute("class") ?? "").Split(' ').Contains("second-column")), "second control column keeps its offset with an explicit supported class");
Check(xml.Descendants().Where(e => e.Name.LocalName is "Panel" or "Label" or "Button" or "Image").Attributes().All(a => new[]{"id","class","hittest","text","src","texturewidth","textureheight"}.Contains(a.Name.LocalName)), "Custom HUD attribute whitelist prevents client rejection");
Check(File.ReadAllText(Path.Combine(root, "src/ZarayskMenu/Plugin.cs")).Contains("const string Layout = \"panorama/layout/custom_game/zaraysk_menu.vxml_c\""), "dynamic HUD references compiled layout resource");
Check(xml.Descendants("Image").All(e=>e.Attribute("src")?.Value=="file://{images}/custom_game/zaraysk_brand.png"), "fixed title and logo use original bundled branding");
Console.WriteLine($"{passed} checks passed");
var guide = JsonSerializer.Deserialize<ZarayskWarcraftMenu.GuideConfig>(File.ReadAllText(Path.Combine(root,"config/warcraft.json")), ZarayskWarcraftMenu.GuideConfig.Json)!;
guide.Validate(); Check(guide.Skills.Count == 59, "56 skills and 3 introduction pages");
Check(guide.Skills.Select(x => x.Group).Distinct().Count() == 15, "14 class groups and introduction");
Check(guide.Skills.All(x => x.Lines.All(line => line.Length <= 29)), "guide lines fit narrow menu");
Check(guide.Skills.Where(x=>x.Group!="Начало игры").GroupBy(x=>x.Group).All(x=>x.Count()==4), "four abilities per class");
foreach (var command in new[] {"shop;quit", "shop\n", "css_menu", "menu", "war", "helpwar", "say \"hello\""})
    Reject(() => new ZarayskWarcraftMenu.GuideConfig { Actions = [new() { Title="bad", Command=command }] }.Validate(), "reject bridge command " + command.Replace('\n',' '));
Check(guide.Skills.Any(x => x.Group == "Разбойник" && string.Join(" ", x.Lines).Contains("X не требуется")), "rogue ultimate is automatic");
Check(guide.Skills.Any(x => x.Group == "Маг" && x.Name == "Магический рывок"), "mage guide follows binary, not stale translation");
Console.WriteLine($"TOTAL {passed} checks passed");
var externalService = new FakeExternalService();
MenuService.Current = externalService;
var adapter = new ZarayskMenu.Api.Compatibility.Menu.PlayerMenu("skins", null!);
string selectedText = "";
var option = adapter.AddItem("AK-47 | Original name", (_, item) => selectedText=item.Text, ZarayskMenu.Api.Compatibility.Enum.DisableOption.None);
adapter.Display(null!, 14);
Check(ReferenceEquals(externalService.Source, adapter) && externalService.Selected==14, "WeaponPaints display routes through core");
option.OnChoose!(null!, option);
Check(selectedText=="AK-47 | Original name", "WeaponPaints callbacks preserve exact original item text");
MenuService.Current=null;
foreach (var cmd in new[] {"css_skins;quit", "css_skins\n", "quit", "css_skins \"x\""})
    Reject(()=>Settings.ValidateMenu(new() {Items=[new() {PlayerCommand=cmd}]},false), "reject unsafe player command");
Settings.ValidateMenu(new() {Items=[new() {PlayerCommand="css_skins"}]},false);
Check(true,"player command accepted without converting to RCON");
Reject(()=>Settings.ValidateMenu(new() {Items=[new() {PlayerCommand="css_skins",Message="ambiguous"}]},false),"reject ambiguous player action");
Settings.ValidateMenu(new() {Items=Enumerable.Range(0,501).Select(i=>new MenuItem {Id="skin_"+i,Text="skin"}).ToList()},true);
Check(true,"large WeaponPaints catalogs accepted");
Console.WriteLine($"FINAL TOTAL {passed} checks passed");
var weaponPath = Path.Combine(root,"server/game/csgo/addons/counterstrikesharp/plugins/WeaponPaints/WeaponPaints.dll");
if (File.Exists(weaponPath))
{
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var path=Path.Combine(Path.GetDirectoryName(weaponPath)!,name.Name+".dll");
        return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
    };
    var assembly=System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(weaponPath);
    Check(!assembly.GetReferencedAssemblies().Any(a=>a.Name=="CS2MenuManager"),"patched WeaponPaints has no CS2MenuManager assembly dependency");
    var method=assembly.GetType("WeaponPaints.Utility")!.GetMethod("CreateMenu",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
    Check(method.ReturnType==typeof(ZarayskMenu.Api.Compatibility.Interface.IMenu),"real WeaponPaints menu factory resolves shared API types");
    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
    Check(true,"patched WeaponPaints menu factory passes CLR JIT preparation (no server callbacks invoked)");
}
Console.WriteLine($"VERIFIED TOTAL {passed} checks passed");
var updated = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root,"config/menu.json")),Settings.Json)!;
var skinItems=updated.Menus.Single(m=>m.Id=="skins").Items;
Check(skinItems.Single(i=>i.Id=="skins").PlayerCommand=="css_skins", "custom cosmetics opens Zaraysk inventory");
Check(skinItems.Where(i=>i.Id is not ("skins" or "knives" or "pets")).Count()==5 && skinItems.Where(i=>i.Id is not ("skins" or "knives" or "pets")).All(i=>i.Message=="Скины вы можете настроить на нашем сайте cszaraysk.net/skins" && i.PlayerCommand=="" && !i.CloseOnSelect),"five website cosmetic sections keep menu open");
Check(skinItems.Single(i=>i.Id=="knives").PlayerCommand=="css_knife" && skinItems.Single(i=>i.Id=="pets").PlayerCommand=="css_pet","knife and pet menus retained");
foreach(var input in new[]{"0","-1","1000001","2147483648","100;quit","1 2","NaN","+10","10\n"})
    Check(!ZarayskWarcraftMenu.Plugin.ValidExperience(input,out _),"reject XP input " + input.Replace('\n',' '));
foreach(var input in new[]{"1","5000","1000000"})
    Check(ZarayskWarcraftMenu.Plugin.ValidExperience(input,out int xp) && xp==int.Parse(input),"accept bounded XP amount");
Console.WriteLine($"RELEASE TOTAL {passed} checks passed");
