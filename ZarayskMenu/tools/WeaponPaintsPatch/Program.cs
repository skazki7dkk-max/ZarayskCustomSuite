using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
if (args.Length != 3) throw new ArgumentException("original WeaponPaints.dll, ZarayskMenu.Api.dll, output DLL");
if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) != "D3335137DA7C5DF8C1D851AF84F42A3B8A08D3FD2C8A5AD7624C16B150878DC7")
    throw new InvalidDataException("Only the supplied WeaponPaints build is supported");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var api = ModuleDefinition.ReadModule(args[1]);
var scope = new AssemblyNameReference(api.Assembly.Name.Name, api.Assembly.Name.Version);
module.AssemblyReferences.Add(scope);
var types = module.GetTypeReferences().Where(t=>t.Scope.Name=="CS2MenuManager").ToArray();
if (types.Length != 6) throw new InvalidDataException("Unexpected menu ABI");
foreach(var type in types)
{
    type.Namespace = type.Namespace.Replace("CS2MenuManager.API", "ZarayskMenu.Api.Compatibility");
    type.Scope = scope;
    if (api.GetType(type.FullName) == null) throw new InvalidDataException("Missing adapter type " + type);
}
var members = module.GetMemberReferences().OfType<MethodReference>().Where(m => m.DeclaringType.Scope.Name == scope.Name).ToArray();
foreach (var member in members)
{
    var target = api.GetType(member.DeclaringType.FullName);
    if (!target.Methods.Any(m=>m.FullName == member.FullName)) throw new InvalidDataException("Missing adapter member " + member);
}
IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> input) => input.SelectMany(t => new[] {t}.Concat(AllTypes(t.NestedTypes)));
var methods = AllTypes(module.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
var styles = methods.Where(m=>m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr && (string)i.Operand=="Pick your preferred menu style")).ToArray();
if (styles.Length != 1 || styles[0].ReturnType.FullName != "System.Void") throw new InvalidDataException("Unexpected style registration");
styles[0].Body = new MethodBody(styles[0]);
styles[0].Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
foreach (var reference in module.AssemblyReferences.Where(r=>r.Name=="CS2MenuManager").ToArray()) module.AssemblyReferences.Remove(reference);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
module.Write(args[2]);
using var verified = ModuleDefinition.ReadModule(args[2]);
if (verified.AssemblyReferences.Any(r=>r.Name=="CS2MenuManager") || verified.GetTypeReferences().Any(t=>t.Scope.Name=="CS2MenuManager"))
    throw new InvalidDataException("Stale CS2MenuManager reference");
using var original = ModuleDefinition.ReadModule(args[0]);
string IL(MethodDefinition m) => string.Join("\n", m.Body.Instructions.Select(i=>i.OpCode+" "+i.Operand)).Replace("CS2MenuManager.API", "ZarayskMenu.Api.Compatibility");
var oldMethods = AllTypes(original.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToDictionary(m=>m.FullName.Replace("CS2MenuManager.API", "ZarayskMenu.Api.Compatibility"));
int unchanged = 0;
foreach(var method in AllTypes(verified.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody))
{
    if (method.FullName==styles[0].FullName) continue;
    if (IL(method)!=IL(oldMethods[method.FullName])) throw new InvalidDataException("Unexpected gameplay edit: " + method.FullName);
    unchanged++;
}
Console.WriteLine($"Verified {types.Length} adapter types, {members.Length} member signatures, {unchanged} method bodies unchanged after type relocation. Only menu-style command registration disabled. No CS2MenuManager reference remains.");
