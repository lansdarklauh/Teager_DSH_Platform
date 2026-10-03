using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>子进程工具：环境变量清洗与进程树终止。</summary>
public static class ProcessUtil
{
    /// <summary>构建子进程环境：只从宿主透传白名单变量，再叠加显式变量，防止平台连接串 / 密钥泄露给 Agent 进程。</summary>
    /// <param name="passthrough">允许透传的宿主变量名。</param>
    /// <param name="extra">显式注入的变量（覆盖同名透传项）。</param>
    /// <returns>环境变量字典。</returns>
    public static Dictionary<string, string> BuildEnv(IEnumerable<string> passthrough, IReadOnlyDictionary<string, string>? extra)
    {
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in passthrough)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(v)) env[name] = v;
        }
        if (extra != null) foreach (var kv in extra) env[kv.Key] = kv.Value;
        return env;
    }

    /// <summary>创建启动信息；Windows 上的 .cmd / .bat 通过 cmd.exe 间接启动。</summary>
    /// <param name="exe">可执行文件。</param>
    /// <param name="args">参数列表。</param>
    /// <param name="cwd">工作目录。</param>
    /// <param name="env">环境变量（会完全替换继承的环境）。</param>
    public static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args, string cwd, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows() && (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi.FileName = "cmd.exe"; psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        return psi;
    }

    /// <summary>终止进程树，忽略已退出的异常。</summary>
    /// <param name="p">进程。</param>
    public static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { /* 已退出 */ }
    }
}

/// <summary>基于 shell 子进程的终端（无 PTY，行式输入输出；Linux 用 sh，Windows 用 PowerShell）。</summary>
public sealed class ShellTerminal : ITerminalSession
{
    private readonly Process _proc;
    private readonly Channel<string> _out = Channel.CreateUnbounded<string>();

    /// <summary>启动 shell 进程并开始读取输出。</summary>
    /// <param name="req">终端请求。</param>
    public ShellTerminal(TerminalRequest req)
    {
        var (exe, args) = OperatingSystem.IsWindows() ? ("powershell.exe", new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-" }) : ("/bin/sh", Array.Empty<string>());
        _proc = Process.Start(ProcessUtil.CreateStartInfo(exe, args, req.WorkspaceRoot, req.Env)) ?? throw new InvalidOperationException("终端启动失败");
        _ = Pump(_proc.StandardOutput);
        _ = Pump(_proc.StandardError);
        _ = _proc.WaitForExitAsync().ContinueWith(_ => _out.Writer.TryComplete());
    }

    /// <summary>把一个流的内容按块写入输出通道。</summary>
    /// <param name="reader">流读取器。</param>
    private async Task Pump(StreamReader reader)
    {
        var buf = new char[2048];
        try { int n; while ((n = await reader.ReadAsync(buf)) > 0) await _out.Writer.WriteAsync(new string(buf, 0, n)); } catch { /* 进程结束 */ }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken ct) => _out.Reader.ReadAllAsync(ct);

    /// <inheritdoc />
    public async Task WriteAsync(string text)
    {
        if (_proc.HasExited) return;
        await _proc.StandardInput.WriteLineAsync(text);
        await _proc.StandardInput.FlushAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ProcessUtil.KillTree(_proc);
        _proc.Dispose();
        return ValueTask.CompletedTask;
    }
}
