using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Options;

namespace Teager.Dsh.Infrastructure.Storage;

/// <summary>文件历史版本。</summary>
/// <param name="Version">版本号（时间戳，如 20260929201530123）。</param>
/// <param name="Time">版本生成时间。</param>
/// <param name="Size">大小（字节）。</param>
public record FileVersion(string Version, DateTime Time, long Size);

/// <summary>
/// 文件版本存储（第二阶段“File Versioning”）：通过平台文件 API 覆盖 / 删除文件前，把旧内容保留为历史版本，可查看与恢复。
/// 版本文件放在数据卷 {DataDir}/versions/{workspaceId|sharedSpaceId}/{路径哈希}/ 下，每个文件默认保留最近 10 个版本。
/// 注意：Agent 在运行中直接改动文件不经过平台文件 API，不会产生历史版本（可用 Workspace 快照兜底）。
/// </summary>
public partial class FileVersionStore(StoragePaths paths, IOptions<DshOptions> opt)
{
    /// <summary>版本号格式校验，防止路径穿越。</summary>
    [GeneratedRegex("^[0-9]{17}$")] private static partial Regex VersionRegex();
    private const long MaxVersionedBytes = 50L * 1024 * 1024;

    /// <summary>某文件的版本目录。</summary>
    /// <param name="areaKey">区域键（Workspace ID 或共享空间 ID）。</param>
    /// <param name="rel">相对路径。</param>
    private string Dir(string areaKey, string rel) =>
        Path.Combine(paths.Data, "versions", areaKey, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? FileStore.Normalize(rel).ToLowerInvariant() : FileStore.Normalize(rel)))).ToLowerInvariant()[..20]);

    /// <summary>在文件被修改 / 删除前保存当前内容为一个历史版本，并只保留最近 N 个。</summary>
    /// <param name="areaKey">区域键。</param>
    /// <param name="root">区域根目录。</param>
    /// <param name="rel">相对路径。</param>
    public void Snapshot(string areaKey, string root, string rel)
    {
        var keep = opt.Value.Platform.FileVersionsKeep;
        if (keep <= 0) return;
        var full = FileStore.Resolve(root, rel);
        if (!File.Exists(full) || new FileInfo(full).Length > MaxVersionedBytes) return;
        var dir = Dir(areaKey, rel);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "path.txt"), FileStore.Normalize(rel), new UTF8Encoding(false));
        File.Copy(full, Path.Combine(dir, DateTime.Now.ToString("yyyyMMddHHmmssfff") + ".v"), true);
        foreach (var old in Directory.EnumerateFiles(dir, "*.v").OrderByDescending(f => f).Skip(keep)) File.Delete(old);
        var historyRoot = Path.GetDirectoryName(dir)!;
        var budget = opt.Value.Sandbox.Enabled ? (long)opt.Value.Sandbox.MaxStorageMb * 1024 * 1024 : 5120L * 1024 * 1024;
        var history = Directory.EnumerateFiles(historyRoot, "*.v", SearchOption.AllDirectories).Select(f => new FileInfo(f)).OrderBy(f => f.Name).ToList();
        var bytes = history.Sum(f => f.Length);
        foreach (var old in history) { if (bytes <= budget) break; bytes -= old.Length; old.Delete(); }
    }

    /// <summary>列出文件的历史版本（新到旧）。</summary>
    /// <param name="areaKey">区域键。</param>
    /// <param name="rel">相对路径。</param>
    public List<FileVersion> List(string areaKey, string rel)
    {
        var dir = Dir(areaKey, rel);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.v").OrderByDescending(f => f).Select(f =>
        {
            var v = Path.GetFileNameWithoutExtension(f);
            return new FileVersion(v, DateTime.ParseExact(v, "yyyyMMddHHmmssfff", null), new FileInfo(f).Length);
        }).ToList();
    }

    /// <summary>读取某历史版本的文本内容（用于对比预览）。</summary>
    /// <param name="areaKey">区域键。</param>
    /// <param name="rel">相对路径。</param>
    /// <param name="version">版本号。</param>
    public async Task<string> ReadAsync(string areaKey, string rel, string version)
    {
        var f = VersionFile(areaKey, rel, version);
        var bytes = await File.ReadAllBytesAsync(f);
        if (Array.IndexOf(bytes, (byte)0) >= 0) throw ApiException.BadRequest("二进制文件不支持预览，请恢复后下载");
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>恢复到某历史版本（恢复前会先把当前内容保存为新版本，因此恢复本身也可撤销）。</summary>
    /// <param name="areaKey">区域键。</param>
    /// <param name="root">区域根目录。</param>
    /// <param name="rel">相对路径。</param>
    /// <param name="version">版本号。</param>
    public void Restore(string areaKey, string root, string rel, string version)
    {
        var f = VersionFile(areaKey, rel, version);
        // Snapshot pruning may remove the selected version. Read it first.
        var content = File.ReadAllBytes(f);
        Snapshot(areaKey, root, rel);
        var target = FileStore.Resolve(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, content);
    }

    public long Size(string areaKey, string rel, string version) => new FileInfo(VersionFile(areaKey, rel, version)).Length;

    /// <summary>定位并校验版本文件。</summary>
    /// <param name="areaKey">区域键。</param>
    /// <param name="rel">相对路径。</param>
    /// <param name="version">版本号。</param>
    private string VersionFile(string areaKey, string rel, string version)
    {
        if (!VersionRegex().IsMatch(version)) throw ApiException.BadRequest("版本号格式不正确");
        var f = Path.Combine(Dir(areaKey, rel), version + ".v");
        return File.Exists(f) ? f : throw ApiException.NotFound("该历史版本不存在或已被清理");
    }
}
