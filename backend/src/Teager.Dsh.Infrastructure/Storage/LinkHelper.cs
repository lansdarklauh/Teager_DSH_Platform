using System.Diagnostics;

namespace Teager.Dsh.Infrastructure.Storage;

/// <summary>目录链接工具：Linux 使用符号链接；Windows 优先符号链接，无权限时回退为目录联接（mklink /J）。</summary>
public static class LinkHelper
{
    /// <summary>确保 linkPath 是指向 targetDir 的目录链接（已存在且指向正确则不动，指向错误则重建）。</summary>
    /// <param name="linkPath">链接路径。</param>
    /// <param name="targetDir">目标目录（必须存在）。</param>
    public static void EnsureDirLink(string linkPath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var info = new DirectoryInfo(linkPath);
        if (info.Exists)
        {
            var current = info.LinkTarget;
            if (current != null && SamePath(System.IO.Path.IsPathRooted(current) ? current : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(linkPath)!, current)), targetDir)) return;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(linkPath); // 仅删除链接本身
            else throw new IOException($"挂载点 {linkPath} 已被普通目录占用");
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(linkPath)!);
        try { Directory.CreateSymbolicLink(linkPath, targetDir); }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetDir}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            if (p.ExitCode != 0) throw new IOException("创建目录联接失败：" + p.StandardError.ReadToEnd());
        }
    }

    /// <summary>删除目录下所有链接（只删除链接本身，不触碰目标数据），用于清理挂载点与彻底删除前的保护。</summary>
    /// <param name="dir">包含链接的目录。</param>
    /// <param name="keep">需要保留的链接名集合（为 null 表示全部删除）。</param>
    public static void RemoveLinks(string dir, ISet<string>? keep = null)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
        {
            if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint) && d.LinkTarget == null) continue;
            if (keep != null && keep.Contains(d.Name)) continue;
            Directory.Delete(d.FullName); // 非递归：只删链接
        }
    }

    /// <summary>比较两个路径是否指向同一位置。</summary>
    /// <param name="a">路径 A。</param>
    /// <param name="b">路径 B。</param>
    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
