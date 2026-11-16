using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Infrastructure.Data;

namespace Teager.Dsh.Infrastructure.Storage;

/// <summary>Keep deleted files recoverable until their database deletion commits.</summary>
public sealed class FileTrash(StoragePaths paths, string ownerKind, string ownerId) : IDisposable
{
    private readonly string directory = Path.Combine(paths.Data, ".trash", Guid.NewGuid().ToString("N"));
    private readonly List<(string Original, string Staged)> moved = [];
    private bool committed;

    public void Stage(string original)
    {
        if (!File.Exists(original) && !Directory.Exists(original)) return;
        if (!Inside(original, paths.Data) && !Inside(original, paths.Logs)) throw new IOException("Invalid cleanup path");
        // Each staged item stays on the same volume as its original file.
        var staged = original + ".deleted-" + Path.GetFileName(directory);
        moved.Add((original, staged));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new Manifest(ownerKind, ownerId, moved.Select(m => new Entry(m.Original, m.Staged)).ToArray())));
        Move(original, staged);
    }

    public void Commit()
    {
        committed = true;
        var cleaned = true;
        foreach (var item in moved) cleaned &= Remove(item.Staged);
        if (cleaned) Remove(directory);
    }

    public void Dispose()
    {
        if (committed) return;
        foreach (var item in moved.AsEnumerable().Reverse())
            if ((File.Exists(item.Staged) || Directory.Exists(item.Staged)) && !File.Exists(item.Original) && !Directory.Exists(item.Original)) Move(item.Staged, item.Original);
        Remove(directory);
    }

    public static async Task RecoverAsync(StoragePaths paths, DshDbContext db)
    {
        var root = Path.Combine(paths.Data, ".trash");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(directory, "manifest.json");
            if (!File.Exists(file)) continue;
            var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(file)) ?? throw new IOException("Invalid cleanup journal");
            var exists = manifest.Kind switch
            {
                "workspace" => await db.Workspaces.AnyAsync(w => w.Id == manifest.Id),
                "shared" => await db.SharedSpaces.AnyAsync(s => s.Id == manifest.Id),
                "session" => await db.Sessions.AnyAsync(s => s.Id == manifest.Id),
                "workspace-context" => await db.Sessions.AnyAsync(s => s.WorkspaceId == manifest.Id),
                _ => throw new IOException("Unknown cleanup journal")
            };
            var cleaned = true;
            foreach (var entry in manifest.Entries.Reverse())
            {
                if ((!Inside(entry.Original, paths.Data) && !Inside(entry.Original, paths.Logs)) || entry.Staged != entry.Original + ".deleted-" + Path.GetFileName(directory)) throw new IOException("Unsafe cleanup journal");
                if (!exists) cleaned &= Remove(entry.Staged);
                else if ((File.Exists(entry.Staged) || Directory.Exists(entry.Staged)) && !File.Exists(entry.Original) && !Directory.Exists(entry.Original)) Move(entry.Staged, entry.Original);
            }
            if (cleaned) Remove(directory);
        }
    }

    private static bool Inside(string path, string root) => !Path.GetRelativePath(root, path).StartsWith("..", StringComparison.Ordinal) && Path.IsPathRooted(path);
    private static void Move(string source, string target) { if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target); }
    private static bool Remove(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } }
    private record Entry(string Original, string Staged);
    private record Manifest(string Kind, string Id, Entry[] Entries);
}
