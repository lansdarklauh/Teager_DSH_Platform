using System.IO.Compression;
using Teager.Dsh.Infrastructure.Common;

namespace Teager.Dsh.Infrastructure.Storage;

/// <summary>文件条目。</summary>
/// <param name="Name">文件名。</param>
/// <param name="Path">相对根目录的路径（使用 / 分隔）。</param>
/// <param name="IsDir">是否目录。</param>
/// <param name="Size">大小（目录为 0）。</param>
/// <param name="Modified">最后修改时间（本地时间）。</param>
public record FileEntry(string Name, string Path, bool IsDir, long Size, DateTime Modified);

/// <summary>
/// 受限文件存储：所有操作限制在给定根目录内，拒绝路径穿越与符号链接逃逸。
/// 供 Workspace 私有文件与共享空间共用。
/// </summary>
public static class FileStore
{
    /// <summary>把相对路径解析为根目录内的绝对路径；含 .. 越界或经过符号链接则抛异常。</summary>
    /// <param name="root">根目录（绝对路径）。</param>
    /// <param name="relative">相对路径（/ 或 \ 分隔，允许为空表示根）。</param>
    /// <returns>绝对路径。</returns>
    public static string Resolve(string root, string? relative)
    {
        var rootFull = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var rel = (relative ?? "").Replace('\\', '/').Trim('/');
        if (rel.Contains('\0')) throw ApiException.BadRequest("路径包含非法字符");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, rel));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!(full.Equals(rootFull, cmp) || full.StartsWith(rootFull + System.IO.Path.DirectorySeparatorChar, cmp)))
            throw ApiException.BadRequest("非法路径：不允许访问根目录之外的位置");
        // 逐级检查已存在的路径段是否为符号链接 / 联接点，防止通过链接逃出根目录
        var cur = rootFull;
        foreach (var seg in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = System.IO.Path.Combine(cur, seg);
            if (!File.Exists(cur) && !Directory.Exists(cur)) break;
            if (new FileInfo(cur).Attributes.HasFlag(FileAttributes.ReparsePoint) || new FileInfo(cur).LinkTarget != null)
                throw ApiException.BadRequest("不允许通过符号链接访问文件");
        }
        return full;
    }

    /// <summary>列出目录内容（隐藏符号链接），目录在前，按名称排序。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="relative">目录相对路径。</param>
    /// <returns>条目列表。</returns>
    public static List<FileEntry> List(string root, string? relative)
    {
        var dir = Resolve(root, relative);
        if (!Directory.Exists(dir)) throw ApiException.NotFound("目录不存在");
        var rel = Normalize(relative);
        var list = new List<FileEntry>();
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null) continue;
            var p = rel.Length == 0 ? info.Name : rel + "/" + info.Name;
            list.Add(info is DirectoryInfo ? new FileEntry(info.Name, p, true, 0, info.LastWriteTime) : new FileEntry(info.Name, p, false, ((FileInfo)info).Length, info.LastWriteTime));
        }
        return list.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>打开文件读取流。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="relative">文件相对路径。</param>
    /// <returns>只读文件流。</returns>
    public static FileStream OpenRead(string root, string relative)
    {
        var full = Resolve(root, relative);
        if (!File.Exists(full)) throw ApiException.NotFound("文件不存在");
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>读取文本文件（用于在线预览 / 编辑），超过上限拒绝。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="relative">文件相对路径。</param>
    /// <param name="maxBytes">最大字节数。</param>
    /// <returns>文本内容。</returns>
    public static async Task<string> ReadTextAsync(string root, string relative, long maxBytes = 2 * 1024 * 1024)
    {
        var full = Resolve(root, relative);
        if (!File.Exists(full)) throw ApiException.NotFound("文件不存在");
        var len = new FileInfo(full).Length;
        if (len > maxBytes) throw ApiException.BadRequest($"文件过大（{len / 1024} KB），请下载后查看");
        var bytes = await File.ReadAllBytesAsync(full);
        if (Array.IndexOf(bytes, (byte)0) >= 0) throw ApiException.BadRequest("二进制文件不支持在线预览，请下载");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>写入（覆盖）文件，自动创建父目录。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="relative">文件相对路径。</param>
    /// <param name="content">内容流。</param>
    /// <param name="maxBytes">大小上限，超出中止并删除半成品。</param>
    /// <returns>写入字节数。</returns>
    public static async Task<long> WriteAsync(string root, string relative, Stream content, long maxBytes)
    {
        var full = Resolve(root, relative);
        if (Directory.Exists(full)) throw ApiException.BadRequest("目标是目录");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        var tmp = full + ".uploading-" + Guid.NewGuid().ToString("N")[..8];
        long total = 0;
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var buf = new byte[81920]; int n;
                while ((n = await content.ReadAsync(buf)) > 0)
                {
                    total += n;
                    if (maxBytes > 0 && total > maxBytes) throw ApiException.Quota($"文件超过大小上限（{maxBytes / 1024 / 1024} MB）");
                    await fs.WriteAsync(buf.AsMemory(0, n));
                }
            }
            File.Move(tmp, full, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        return total;
    }

    /// <summary>创建目录。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="relative">目录相对路径。</param>
    public static void Mkdir(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw ApiException.BadRequest("目录名不能为空");
        Directory.CreateDirectory(Resolve(root, relative));
    }

    /// <summary>删除文件或目录（不允许删除根目录本身）。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="relative">相对路径。</param>
    public static void Delete(string root, string relative)
    {
        if (Normalize(relative).Length == 0) throw ApiException.BadRequest("不能删除根目录");
        var full = Resolve(root, relative);
        if (full.Equals(System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw ApiException.BadRequest("不能删除根目录");
        if (Directory.Exists(full)) Directory.Delete(full, true);
        else if (File.Exists(full)) File.Delete(full);
        else throw ApiException.NotFound("文件或目录不存在");
    }

    /// <summary>重命名 / 移动（源与目标都必须在根目录内）。</summary>
    /// <param name="root">根目录。</param>
    /// <param name="from">源相对路径。</param>
    /// <param name="to">目标相对路径。</param>
    public static void Move(string root, string from, string to)
    {
        if (Normalize(from).Length == 0 || Normalize(to).Length == 0) throw ApiException.BadRequest("路径不能为空");
        var src = Resolve(root, from); var dst = Resolve(root, to);
        var rootFull = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (src.Equals(rootFull, comparison) || dst.Equals(rootFull, comparison)) throw ApiException.BadRequest("不能移动根目录");
        if (File.Exists(dst) || Directory.Exists(dst)) throw ApiException.Conflict("目标已存在");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
        if (Directory.Exists(src))
        {
            if ((dst + System.IO.Path.DirectorySeparatorChar).StartsWith(src + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw ApiException.BadRequest("不能把目录移动到自身内部");
            Directory.Move(src, dst);
        }
        else if (File.Exists(src)) File.Move(src, dst);
        else throw ApiException.NotFound("源文件或目录不存在");
    }

    /// <summary>递归统计目录大小（跳过符号链接）。</summary>
    /// <param name="dir">目录绝对路径。</param>
    /// <returns>总字节数。</returns>
    public static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        var stack = new Stack<string>(); stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(d).EnumerateFileSystemInfos())
                {
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null) continue;
                    if (info is DirectoryInfo di) stack.Push(di.FullName); else total += ((FileInfo)info).Length;
                }
            }
            catch (UnauthorizedAccessException) { /* 无权限目录忽略 */ }
        }
        return total;
    }

    /// <summary>生成目录文件清单（相对路径 → 大小|修改时间），用于运行前后对比检测对共享空间的改动。</summary>
    /// <param name="dir">目录绝对路径。</param>
    /// <param name="limit">最多记录的文件数，防止超大目录拖慢运行。</param>
    /// <returns>清单字典。</returns>
    public static Dictionary<string, (long Size, long Ticks)> Manifest(string dir, int limit = 20000)
    {
        var m = new Dictionary<string, (long, long)>();
        if (!Directory.Exists(dir)) return m;
        var stack = new Stack<string>(); stack.Push(dir);
        while (stack.Count > 0 && m.Count < limit)
        {
            var d = stack.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(d).EnumerateFileSystemInfos())
                {
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null) continue;
                    if (info is DirectoryInfo di) stack.Push(di.FullName);
                    else { var fi = (FileInfo)info; m[System.IO.Path.GetRelativePath(dir, fi.FullName).Replace('\\', '/')] = (fi.Length, fi.LastWriteTimeUtc.Ticks); }
                }
            }
            catch (UnauthorizedAccessException) { }
        }
        return m;
    }

    /// <summary>把目录打包为 zip 文件（跳过符号链接）。</summary>
    /// <param name="sourceDir">源目录。</param>
    /// <param name="zipPath">目标 zip 路径。</param>
    public static void ZipDirectory(string sourceDir, string zipPath)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(zipPath)!);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (!Directory.Exists(sourceDir)) return;
        var stack = new Stack<string>(); stack.Push(sourceDir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var info in new DirectoryInfo(d).EnumerateFileSystemInfos())
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null) continue;
                var rel = System.IO.Path.GetRelativePath(sourceDir, info.FullName).Replace('\\', '/');
                if (info is DirectoryInfo di) { zip.CreateEntry(rel + "/"); stack.Push(di.FullName); }
                else zip.CreateEntryFromFile(info.FullName, rel, CompressionLevel.Fastest);
            }
        }
    }

    /// <summary>把 zip 解压到目录（防 zip-slip，路径逃逸的条目直接拒绝）。</summary>
    /// <param name="zipPath">zip 文件。</param>
    /// <param name="destDir">目标目录。</param>
    public static void ExtractZip(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var destFull = System.IO.Path.GetFullPath(destDir).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(destDir, e.FullName));
            if (!target.StartsWith(destFull, StringComparison.Ordinal)) throw ApiException.BadRequest("压缩包包含非法路径");
            if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
    }

    /// <summary>递归复制目录（跳过符号链接）。</summary>
    /// <param name="src">源目录。</param>
    /// <param name="dst">目标目录。</param>
    public static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        if (!Directory.Exists(src)) return;
        foreach (var info in new DirectoryInfo(src).EnumerateFileSystemInfos())
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget != null) continue;
            var target = System.IO.Path.Combine(dst, info.Name);
            if (info is DirectoryInfo di) CopyDirectory(di.FullName, target); else File.Copy(info.FullName, target, true);
        }
    }

    /// <summary>规范化相对路径：统一分隔符并去掉首尾斜杠。</summary>
    /// <param name="relative">相对路径。</param>
    public static string Normalize(string? relative) => (relative ?? "").Replace('\\', '/').Trim('/');
}
