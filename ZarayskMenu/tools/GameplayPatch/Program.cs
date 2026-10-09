using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;

if (args.Length != 3) throw new ArgumentException("menu-patched WarcraftPlugin.dll, ZarayskWarcraftFixes.dll, output WarcraftPlugin.dll");
if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) != "F7801EAC2B3A5776097293A336103B331178C9EF3AF5BDED5D2108DCB30E9345")
    throw new InvalidDataException("Only the verified Zaraysk menu-patched Warcraft 4.2.0 is supported.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");
resolver.AddSearchDirectory(Path.Combine(nuget, "counterstrikesharp.api/1.0.375/lib/net10.0"));
using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var fixes = ModuleDefinition.ReadModule(args[1]);
var safeDamage = fixes.GetType("ZarayskWarcraftFixes.Damage").Methods.Single(m => m.Name == "Invoke");
if (safeDamage.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName.Contains("VirtualFunctions")))
    throw new InvalidDataException("Safe damage helper still calls a native damage virtual function.");
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[] { t }.Concat(Types(t.NestedTypes)));
var all = Types(module.Types).ToArray();
var before = all.SelectMany(t => t.Methods).Where(m => m.HasBody).ToDictionary(m => m.FullName, m => string.Join("\n", m.Body.Instructions));
var changed = new HashSet<string>();
MethodDefinition Method(string type, string name) => all.Single(t => t.FullName == type).Methods.Single(m => m.Name == name);
MethodReference Fix(string type, string name) => module.ImportReference(fixes.GetType("ZarayskWarcraftFixes." + type).Methods.Single(m => m.Name == name));
void Replace(MethodDefinition method, params Instruction[] instructions)
{
    changed.Add(method.FullName);
    method.Body = new MethodBody(method);
    foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
}
var damage = Method("WarcraftPlugin.Helpers.Warcraft", "TakeDamage");
// Known 4.2.0 body shape only: replace the entire obsolete attacker struct write.
var oldCtor = damage.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r && r.DeclaringType.Name == "CAttackerInfo");
var end = oldCtor;
while (!(end.Operand is MethodReference r && r.Name == "StructureToPtr")) end = end.Next ?? throw new InvalidDataException("No attacker write");
var ptrLoad = oldCtor.Next;
if (ptrLoad.OpCode != OpCodes.Ldloca_S || ptrLoad.Operand is not VariableDefinition pointerLocal || pointerLocal.VariableType.FullName != "System.IntPtr") throw new InvalidDataException("Unexpected damage pointer local");
// The preceding controller load stays on the stack; retain branch targets by nopping.
for (var current = oldCtor; current != end; current = current.Next) { current.OpCode = OpCodes.Nop; current.Operand = null; }
oldCtor.OpCode = OpCodes.Ldloc;
oldCtor.Operand = pointerLocal;
end.OpCode = OpCodes.Call;
end.Operand = Fix("Damage", "WriteAttacker");
changed.Add(damage.FullName);
var resolve = Method("WarcraftPlugin.Helpers.Warcraft", "ResolveTakeDamageInvoker");
Replace(resolve, Instruction.Create(OpCodes.Call, Fix("Damage", "Resolve")), Instruction.Create(OpCodes.Ret));

var dashType = all.Single(t => t.FullName == "WarcraftPlugin.Classes.Ranger/DashPassiveEffect");
var tick = dashType.Methods.Single(m => m.Name == "OnTick");
var owner = tick.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(r => r.Name == "get_Owner");
Replace(tick, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, owner),
    Instruction.Create(OpCodes.Call, Fix("Dash", "Tick")), Instruction.Create(OpCodes.Ret));
var ctor = dashType.Methods.Single(m => m.IsConstructor);
ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == .1f).Operand = .001f;
// At 999999f subtracting a 1/64 second tick rounds back to the same float.
// EffectManager therefore never advances this effect or calls OnTick.
ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 999999f).Operand = 86400f;
changed.Add(ctor.FullName);

// Missing heavy-player assets must not replace a valid standard/agent model.
var model = Method("WarcraftPlugin.Classes.Barbarian", "get_DefaultModel");
foreach (var i in model.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr)) i.Operand = "";
changed.Add(model.FullName);

// Runtime custom-hero polling touches WarcraftPlugin.dll to force a hot reload.
// This has repeatedly unloaded the live plugin on the user's Linux host.
var customHeroPolling = Method("WarcraftPlugin.Core.ClassManager", "TrackCustomClassesChanges");
Replace(customHeroPolling, Instruction.Create(OpCodes.Ret));

// Make installed builds identifiable in server logs.
var moduleVersion = Method("WarcraftPlugin.WarcraftPlugin", "get_ModuleVersion");
var versionText = moduleVersion.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr);
versionText.Operand = "4.2.0-zaraysk-0.3.1";
changed.Add(moduleVersion.FullName);

// Warcraft's global spawn handler already calls OnPlayerSpawned. These two
// class hooks called it a second time, creating duplicate build/dash effects.
Replace(Method("WarcraftPlugin.Classes.DwarfEngineer", "PlayerSpawn"), Instruction.Create(OpCodes.Ret));
Replace(Method("WarcraftPlugin.Classes.Ranger", "OnPlayerSpawn"), Instruction.Create(OpCodes.Ret));

// Physics props must have their model before DispatchSpawn. Dwarf Engineer's
// blueprint and placed prop used the unsafe reverse order.
Instruction CloneInstruction(Instruction source) => source.Operand switch
{
    null => Instruction.Create(source.OpCode),
    VariableDefinition value => Instruction.Create(source.OpCode, value),
    FieldReference value => Instruction.Create(source.OpCode, value),
    MethodReference value => Instruction.Create(source.OpCode, value),
    _ => throw new InvalidDataException($"Unsupported cloned operand {source.Operand.GetType()}")
};
void ModelBeforeSpawn(MethodDefinition method)
{
    var spawn = method.Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "DispatchSpawn");
    var set = method.Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "SetModel");
    var modelLoad = new[] { set.Previous!.Previous!.Previous!.Previous!, set.Previous.Previous.Previous!, set.Previous.Previous!, set.Previous!, set };
    var processor = method.Body.GetILProcessor();
    foreach (var source in modelLoad) processor.InsertBefore(spawn, CloneInstruction(source));
    changed.Add(method.FullName);
}
ModelBeforeSpawn(Method("WarcraftPlugin.Classes.BuildEffect", "OnStart"));
ModelBeforeSpawn(Method("WarcraftPlugin.Classes.BuildEffect", "<SpawnProp>b__12_0"));

var tinkerUltimate = Method("WarcraftPlugin.Classes.Tinker", "Ultimate");
// Preserve every normal Tinker skill and hook. Only replace the entity-heavy
// Drone Swarm ultimate which caused the confirmed native crash.
Replace(tinkerUltimate, Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, Fix("Tinker", "Ultimate")), Instruction.Create(OpCodes.Ret));

var shapeshifterUltimate = Method("WarcraftPlugin.Classes.Shapeshifter", "Ultimate");
Replace(shapeshifterUltimate, Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, Fix("Shapeshifter", "Ultimate")), Instruction.Create(OpCodes.Ret));

var necroSpawn = Method("WarcraftPlugin.Classes.Necromancer", "OnPlayerSpawned");
Replace(necroSpawn, Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, Fix("Necromancer", "OnSpawn")), Instruction.Create(OpCodes.Ret));
var raiseDead = Method("WarcraftPlugin.Classes.Necromancer", "RaiseDead");
Replace(raiseDead, Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, Fix("Necromancer", "RaiseDead")), Instruction.Create(OpCodes.Ret));

// Skip native weapon-data lookup for classes that have no weapon restriction.
var acquire = Method("WarcraftPlugin.Events.EventSystem", "OnWeaponCanAcquire");
var nativeLookup = acquire.Body.Instructions.Single(i => i.Operand is FieldReference f && f.Name == "GetCSWeaponDataFromKeyFunc");
var lookupStart = nativeLookup.Previous;
if (lookupStart == null || lookupStart.OpCode != OpCodes.Ldloc_0) throw new InvalidDataException("Unexpected weapon lookup stack shape");
var whitelistGetter = acquire.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(r => r.Name == "get_WeaponWhitelist");
var countGetter = acquire.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().First(r =>
    r.Name == "get_Count" && r.DeclaringType.FullName.StartsWith("System.Collections.Generic.List`1<System.String>"));
var continueLookup = Instruction.Create(OpCodes.Nop);
var ail = acquire.Body.GetILProcessor();
ail.InsertBefore(lookupStart, Instruction.Create(OpCodes.Ldloc_3));
ail.InsertBefore(lookupStart, Instruction.Create(OpCodes.Callvirt, whitelistGetter));
ail.InsertBefore(lookupStart, Instruction.Create(OpCodes.Callvirt, countGetter));
ail.InsertBefore(lookupStart, Instruction.Create(OpCodes.Brtrue, continueLookup));
ail.InsertBefore(lookupStart, Instruction.Create(OpCodes.Ldc_I4_0));
ail.InsertBefore(lookupStart, Instruction.Create(OpCodes.Ret));
ail.InsertBefore(lookupStart, continueLookup);
changed.Add(acquire.FullName);

// Initialize models before DispatchSpawn: physics props need a model at spawn.
var activate = Method("WarcraftPlugin.Summons.Drone", "Activate");
var setModel = all.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Select(i => i.Operand).OfType<MethodReference>().First(r => r.Name == "SetModel" && r.DeclaringType.Name == "CBaseModelEntity");
var spawns = activate.Body.Instructions.Where(i => i.Operand is MethodReference r && r.Name == "DispatchSpawn").ToArray();
if (spawns.Length != 3) throw new InvalidDataException("Unexpected drone spawns");
var il = activate.Body.GetILProcessor();
for (int index = 0; index < spawns.Length; index++)
{
    var spawn = spawns[index];
    if (((MethodReference)spawn.Operand).Parameters.Count != 0) throw new InvalidDataException("Unexpected spawn signature");
    var anchor = spawn;
    il.InsertBefore(anchor, Instruction.Create(OpCodes.Dup));
    il.InsertBefore(anchor, Instruction.Create(OpCodes.Ldstr, index == 2 ? "models/tools/bullet_hit_marker.vmdl" : "models/props/de_dust/hr_dust/dust_soccerball/dust_soccer_ball001.vmdl"));
    il.InsertBefore(anchor, Instruction.Create(OpCodes.Callvirt, setModel));
}
changed.Add(activate.FullName);
// The delayed color callbacks no longer change the already spawned physics model.
foreach (var m in all.Where(t => t.FullName.StartsWith("WarcraftPlugin.Summons.Drone")).SelectMany(t => t.Methods).Where(m => m.HasBody))
{
    if (!m.Name.Contains("<Activate>")) continue;
    foreach (var call in m.Body.Instructions.Where(i => i.Operand is MethodReference r && r.Name == "SetModel").ToArray())
    {
        call.OpCode = OpCodes.Pop; call.Operand = null;
        m.Body.GetILProcessor().InsertAfter(call, Instruction.Create(OpCodes.Pop));
        changed.Add(m.FullName);
    }
}
// Ensure the turret model is present in the resource manifest.
var preload = Method("WarcraftPlugin.Classes.Tinker", "get_PreloadResources");
var ret = preload.Body.Instructions.Last();
var listAdd = module.ImportReference(typeof(List<string>).GetMethod("Add")!);
var pil = preload.Body.GetILProcessor();
pil.InsertBefore(ret, Instruction.Create(OpCodes.Dup));
pil.InsertBefore(ret, Instruction.Create(OpCodes.Ldstr, "models/tools/bullet_hit_marker.vmdl"));
pil.InsertBefore(ret, Instruction.Create(OpCodes.Callvirt, listAdd));
changed.Add(preload.FullName);

// Widen branches in edited methods so added instructions cannot overflow sbyte offsets.
foreach (var m in all.SelectMany(t => t.Methods).Where(m => changed.Contains(m.FullName)))
foreach (var i in m.Body.Instructions)
{
    if (i.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
    var field = typeof(OpCodes).GetFields().Single(f => f.FieldType == typeof(OpCode) &&
        ((OpCode)f.GetValue(null)!).Name == i.OpCode.Name[..^2]);
    i.OpCode = (OpCode)field.GetValue(null)!;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
module.Write(args[2]);
using var verification = ModuleDefinition.ReadModule(args[2]);
var after = Types(verification.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
foreach (var m in after)
    if (!changed.Contains(m.FullName) && before[m.FullName] != string.Join("\n", m.Body.Instructions))
        throw new InvalidDataException("Unexpected method change: " + m.FullName);
Console.WriteLine($"Verified {changed.Count} gameplay method edits; {after.Length - changed.Count} other bodies unchanged.");
foreach (var name in changed) Console.WriteLine(name);
Console.WriteLine("SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))));
