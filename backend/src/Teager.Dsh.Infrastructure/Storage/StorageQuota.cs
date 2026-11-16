using System.Text.Json.Nodes;
using Teager.Dsh.Infrastructure.Common;

namespace Teager.Dsh.Infrastructure.Storage;

public static class StorageQuota
{
    public static long OrphanBytes(StoragePaths paths, string id)
    {
        var file = Path.Combine(paths.Data, "quota-locks", id + ".json");
        if (!File.Exists(file)) return 0;
        var records = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        return records?.Select(r => r.Value).Where(r => r?["unlinked"]?.GetValue<bool>() == true).GroupBy(r => r!["inode"]!.GetValue<string>()).Sum(g => g.Max(r => r!["size"]!.GetValue<long>())) ?? 0;
    }
    public static void DeleteVersions(StoragePaths paths, string id)
    {
        var parent = Path.GetFullPath(Path.Combine(paths.Data, "versions"));
        var directory = Path.GetFullPath(Path.Combine(parent, id));
        if (Path.GetDirectoryName(directory) != parent) throw ApiException.BadRequest("非法版本目录");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    public static int CountEntries(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var count = 0; var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory)) foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            count++;
            if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget == null) pending.Push(entry.FullName);
        }
        return count;
    }
}
