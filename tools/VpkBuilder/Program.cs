using ValvePak;

if (args.Length == 4 && args[0] == "--extract")
{
    using var extractPackage = new Package();
    extractPackage.Read(Path.GetFullPath(args[1]));
    var entry = extractPackage.FindEntry(args[2]) ?? throw new FileNotFoundException("VPK entry not found", args[2]);
    extractPackage.ReadEntry(entry, out var data, validateCrc: true);
    File.WriteAllBytes(Path.GetFullPath(args[3]), data);
    Console.WriteLine($"Extracted {entry.GetFullPath()} ({data.Length} bytes).");
    return 0;
}

if (args.Length == 3 && args[0] == "--find")
{
    using var searchPackage = new Package();
    searchPackage.Read(Path.GetFullPath(args[1]));
    foreach (var entry in searchPackage.Entries!.SelectMany(group => group.Value)
                 .Where(entry => entry.GetFullPath().Contains(args[2], StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine(entry.GetFullPath());
    return 0;
}

if (args.Length == 2 && args[0] == "--verify")
{
    using var verifyPackage = new Package();
    verifyPackage.Read(Path.GetFullPath(args[1]));
    var entries = verifyPackage.Entries!.SelectMany(group => group.Value).ToArray();
    foreach (var entry in entries) verifyPackage.ReadEntry(entry, out _, validateCrc: true);
    Console.WriteLine($"Verified {entries.Length} VPK entries with CRC checks.");
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: VpkBuilder <compiled-root> <output-dir-vpk> | --verify <dir-vpk> | --find <dir-vpk> <text> | --extract <dir-vpk> <entry> <output>");
    return 1;
}

var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Where(file => !Path.GetFileName(file).Equals("zaraysk_custom_assets.json", StringComparison.OrdinalIgnoreCase))
    .ToArray();
if (files.Length == 0)
{
    Console.Error.WriteLine($"No compiled resources found in {root}");
    return 2;
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var package = new Package();
foreach (var file in files)
{
    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
    package.AddFile(relative, File.ReadAllBytes(file), multiChunk: true);
}

package.Write(output);
Console.WriteLine($"Created {output} with {files.Length} files.");
return 0;
