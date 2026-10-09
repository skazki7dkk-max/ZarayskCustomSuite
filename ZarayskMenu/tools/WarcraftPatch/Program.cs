using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;

if (args.Length != 3) throw new ArgumentException("input WarcraftPlugin.dll, ZarayskMenu.Api.dll, output WarcraftPlugin.dll");
var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
if (hash != "2F722B5A7B21DD82DBC8672A3E3803D33F5EBCDDBC0B7CD987A08F3A71538BC1")
    throw new InvalidDataException("Patch is only for the supplied Warcraft 4.2.0 binary");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var api = ModuleDefinition.ReadModule(args[1]);
var service = api.GetType("ZarayskMenu.Api.MenuService");
var manager = module.GetType("WarcraftPlugin.Menu.MenuManager");
void Redirect(string methodName, string targetName, int arguments)
{
    var method = manager.Methods.Single(m => m.Name == methodName);
    if (method.Parameters.Count != arguments) throw new InvalidDataException("Unexpected signature");
    method.Body = new MethodBody(method);
    var il = method.Body.GetILProcessor();
    for (int i = 0; i < arguments; i++) il.Append(il.Create(OpCodes.Ldarg, method.Parameters[i]));
    il.Append(il.Create(OpCodes.Call, module.ImportReference(service.Methods.Single(m => m.Name == targetName))));
    il.Append(il.Create(OpCodes.Ret));
}
Redirect("OpenMainMenu", "OpenExternal", 3);
Redirect("CloseMenu", "CloseExternal", 1);
var unique = module.GetType("WarcraftPlugin.WarcraftPlugin").Methods.Single(m => m.Name == "AddUniqueCommand");
var processor = unique.Body.GetILProcessor();
var first = unique.Body.Instructions[0];
// Remove both unprefixed and prefixed main-menu collisions, including localized aliases.
var equality = module.ImportReference(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
foreach (var alias in new[] { "menu", "css_menu" })
{
    var continueAt = processor.Create(OpCodes.Nop);
    processor.InsertBefore(first, processor.Create(OpCodes.Ldarg_1));
    processor.InsertBefore(first, processor.Create(OpCodes.Ldstr, alias));
    processor.InsertBefore(first, processor.Create(OpCodes.Call, equality));
    processor.InsertBefore(first, processor.Create(OpCodes.Brfalse, continueAt));
    processor.InsertBefore(first, processor.Create(OpCodes.Ret));
    processor.InsertBefore(first, continueAt);
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
module.Write(args[2]);
using var verify = ModuleDefinition.ReadModule(args[2]);
foreach (var name in new[] { "OpenMainMenu", "CloseMenu" })
{
    var body = verify.GetType("WarcraftPlugin.Menu.MenuManager").Methods.Single(m => m.Name == name).Body;
    if (body.Instructions.Count(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).DeclaringType.FullName == "ZarayskMenu.Api.MenuService") != 1)
        throw new InvalidDataException("Bridge verification failed");
}
Console.WriteLine("Verified: both menu methods use ZarayskMenu.Api; Warcraft no longer registers menu/css_menu. Gameplay IL unchanged.");
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(Types(t.NestedTypes)));
string Instructions(MethodDefinition method) => string.Join("\n", method.Body.Instructions.Select(i => i.OpCode + " " + i.Operand));
using var original = ModuleDefinition.ReadModule(args[0]);
var originalMethods = Types(original.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToDictionary(m => m.FullName);
var changed = new List<string>();
foreach (var method in Types(verify.Types).SelectMany(t => t.Methods).Where(m => m.HasBody))
    if (Instructions(method) != Instructions(originalMethods[method.FullName])) changed.Add(method.FullName);
if (changed.Count != 3 || changed.Any(name => !name.Contains("MenuManager::OpenMainMenu(") && !name.Contains("MenuManager::CloseMenu(") && !name.Contains("WarcraftPlugin::AddUniqueCommand(")))
    throw new InvalidDataException("Unexpected IL changes: " + string.Join(", ", changed));
Console.WriteLine($"Verified {originalMethods.Count - 3} other method bodies unchanged; exactly 3 integration methods patched.");
