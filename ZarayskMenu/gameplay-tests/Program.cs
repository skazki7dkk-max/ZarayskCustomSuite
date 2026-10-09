using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ZarayskWarcraftFixes;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
float oldDuration = 999999f, newDuration = 86400f, tickInterval = 1f / 64f;
Check(oldDuration - tickInterval == oldDuration, "reproduce old effect timer precision failure");
Check(newDuration - (newDuration - tickInterval) >= tickInterval, "new duration advances effect scheduler each tick");
var dash = new DashGate();
Check(!dash.Step(true, false, true), "idle");
Check(!dash.Step(true, true, true), "ground jump cannot dash");
Check(dash.Step(false, true, true), "first airborne tick dashes");
Check(!dash.Step(false, true, true), "one dash per flight");
Check(!dash.Step(true, false, true), "landing resets");
Check(!dash.Step(false, false, true), "walking off does not dash");
Check(dash.Step(false, true, true), "airborne jump activates dash");
var cooldown = new DashGate();
Check(!cooldown.Step(true, false, true), "cooldown setup");
Check(!cooldown.Step(false, true, false), "cooldown blocks");
Check(cooldown.Step(false, true, true), "dash fires when cooldown becomes ready");
Check(Marshal.SizeOf<Damage.AttackerInfo>() == 28, "native test struct size");
Check(Marshal.OffsetOf<Damage.AttackerInfo>(nameof(Damage.AttackerInfo.AttackerPawn)).ToInt32() == 12, "native test pawn offset");

// Resolve the original plugin's managed dependencies, without executing plugin Load.
if (args.Length != 2) throw new ArgumentException("patched DLL, original dependency folder");
AssemblyLoadContext.Default.Resolving += (_, name) => {
    var path = Path.Combine(Path.GetFullPath(args[1]), name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var targets = new Dictionary<string, string[]> {
    ["WarcraftPlugin.Helpers.Warcraft"] = ["TakeDamage", "ResolveTakeDamageInvoker"],
    ["WarcraftPlugin.Classes.Ranger+DashPassiveEffect"] = ["OnTick"],
    ["WarcraftPlugin.Classes.Barbarian"] = ["get_DefaultModel"],
    ["WarcraftPlugin.Classes.Necromancer"] = ["OnPlayerSpawned", "RaiseDead"],
    ["WarcraftPlugin.Classes.Tinker"] = ["Ultimate", "get_PreloadResources"],
    ["WarcraftPlugin.Classes.Shapeshifter"] = ["Ultimate"],
    ["WarcraftPlugin.Classes.DwarfEngineer"] = ["PlayerSpawn"],
    ["WarcraftPlugin.Classes.BuildEffect"] = ["OnStart", "<SpawnProp>b__12_0"],
    ["WarcraftPlugin.Classes.Ranger"] = ["OnPlayerSpawn"],
    ["WarcraftPlugin.Summons.Drone"] = ["Activate", "<Activate>b__22_0", "<Activate>b__22_1", "<Activate>b__22_2"],
    ["WarcraftPlugin.Events.EventSystem"] = ["OnWeaponCanAcquire"]
    ,["WarcraftPlugin.Core.ClassManager"] = ["TrackCustomClassesChanges"]
    ,["WarcraftPlugin.WarcraftPlugin"] = ["get_ModuleVersion"]
};
foreach (var (typeName, methods) in targets)
foreach (var name in methods)
{
    var method = assembly.GetType(typeName, true)!.GetMethod(name, flags)!;
    Console.WriteLine($"JIT {typeName}.{name}");
    RuntimeHelpers.PrepareMethod(method.MethodHandle);
    checks++;
}
var race = assembly.GetType("WarcraftPlugin.Classes.Ranger", true)!;
RuntimeHelpers.PrepareMethod(assembly.GetType("WarcraftPlugin.Classes.Ranger+DashPassiveEffect", true)!.GetConstructors(flags).Single().MethodHandle);
checks++;
RuntimeHelpers.PrepareMethod(typeof(Dash).GetMethod("Tick")!.MethodHandle);
RuntimeHelpers.PrepareMethod(typeof(Damage).GetMethod("Invoke")!.MethodHandle);
RuntimeHelpers.PrepareMethod(typeof(Necromancer).GetMethod("OnSpawn")!.MethodHandle);
RuntimeHelpers.PrepareMethod(typeof(Necromancer).GetMethod("RaiseDead")!.MethodHandle);
RuntimeHelpers.PrepareMethod(typeof(Tinker).GetMethod("Ultimate")!.MethodHandle);
RuntimeHelpers.PrepareMethod(typeof(Shapeshifter).GetMethod("Ultimate")!.MethodHandle);
RuntimeHelpers.PrepareMethod(typeof(Damage).GetMethod("ApplyPawn")!.MethodHandle);
checks += 7;
Check(race.GetMethod("StartCooldown", flags)!.GetParameters().Length == 2, "cooldown reflection signature");
Check(race.GetMethod("IsAbilityReady", flags)!.ReturnType == typeof(bool), "ready reflection signature");
var extension = assembly.GetType("WarcraftPlugin.WarcraftPlayerExtensions", true)!;
Check(extension.GetMethod("GetWarcraftPlayer") != null, "profile accessor");
var plugin = assembly.GetType("WarcraftPlugin.WarcraftPlugin", true)!;
var uninitialized = RuntimeHelpers.GetUninitializedObject(plugin);
Check((string)plugin.GetProperty("ModuleVersion")!.GetValue(uninitialized)! == "4.2.0-zaraysk-0.3.1", "diagnostic module version");
Console.WriteLine($"PASS: {checks} gameplay checks, including JIT of patched methods. Native gameplay requires CS2 server.");
