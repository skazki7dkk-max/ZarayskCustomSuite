using System.Text.Json;

namespace Zaraysk.Storage;

// Explicit offline storage; never silently replaces a failed production database.
internal sealed class LocalJsonStore<T>(string path)
{
    private readonly object gate = new();
    public TResult Use<TResult>(Func<List<T>, TResult> action, bool write = false)
    {
        lock (gate)
        {
            var rows = File.Exists(path) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty local inventory file") : [];
            var result = action(rows);
            if (write)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path + ".new", JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(path + ".new", path, overwrite: true);
            }
            return result;
        }
    }
}
