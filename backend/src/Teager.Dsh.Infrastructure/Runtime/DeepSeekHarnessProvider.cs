using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>
/// DeepSeek Harness Provider：每次消息启动一个 <c>dsh headless --json</c> 子进程，
/// 通过 <c>--session-id</c> 续接同一 Runtime 会话；每个 Pool 使用独立的 DSH_HOME，实现会话 / 凭据 / 插件隔离。
/// </summary>
public class DeepSeekHarnessProvider(IOptions<DshOptions> opt, ILogger<DeepSeekHarnessProvider> logger, DockerSandbox sandbox) : IRuntimeProvider
{
    /// <summary>运行中的子进程：runId → (poolId, 进程)，用于停止 Runtime 时批量终止。</summary>
    private readonly ConcurrentDictionary<string, (string PoolId, Process Proc)> _active = new();

    /// <inheritdoc />
    public string Name => "DeepSeekHarness";

    /// <inheritdoc />
    public async Task<RuntimeHealth> StartRuntimeAsync(RuntimeSpec spec, CancellationToken ct)
    {
        Directory.CreateDirectory(spec.DshHome);
        if (!opt.Value.Sandbox.Enabled) SeedHome(spec.DshHome);
        return await HealthCheckAsync(spec, ct);
    }

    /// <inheritdoc />
    public Task StopRuntimeAsync(string poolId)
    {
        foreach (var kv in _active.Where(k => k.Value.PoolId == poolId).ToList()) ProcessUtil.KillTree(kv.Value.Proc);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<RuntimeHealth> RestartRuntimeAsync(RuntimeSpec spec, CancellationToken ct)
    {
        await StopRuntimeAsync(spec.PoolId);
        return await StartRuntimeAsync(spec, ct);
    }

    /// <inheritdoc />
    public async Task<RuntimeHealth> HealthCheckAsync(RuntimeSpec spec, CancellationToken ct)
    {
        var active = _active.Count(k => k.Value.PoolId == spec.PoolId);
        try
        {
            if (opt.Value.Sandbox.Enabled)
            {
                sandbox.RequireEnabled();
                await sandbox.CliAsync(["image", "inspect", opt.Value.Sandbox.Image], ct);
                return new RuntimeHealth(true, "isolated", "隔离 Agent 镜像已就绪", active);
            }
            var env = ProcessUtil.BuildEnv(opt.Value.Runtime.EnvPassthrough, new Dictionary<string, string> { ["DSH_HOME"] = spec.DshHome, ["NODE_NO_WARNINGS"] = "1" });
            using var p = Process.Start(ProcessUtil.CreateStartInfo(opt.Value.Runtime.DshExecutable, ["--version"], spec.DshHome, env)) ?? throw new InvalidOperationException("无法启动 dsh");
            p.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            var version = stdout.Trim().Split('\n').LastOrDefault()?.Trim();
            return p.ExitCode == 0 ? new RuntimeHealth(true, version, "dsh 可用", active) : new RuntimeHealth(false, version, $"dsh --version 退出码 {p.ExitCode}", active);
        }
        catch (Exception ex)
        {
            return new RuntimeHealth(false, null, "无法执行 dsh：" + ex.Message + "（请检查 Dsh:Runtime:DshExecutable 与 Node.js 环境）", active);
        }
    }

    /// <inheritdoc />
    public Task<string?> CreateSessionAsync(RunRequest req) => Task.FromResult<string?>(null); // DSH 在首条消息时创建会话并回传 sessionId

    /// <inheritdoc />
    public Task<bool> ResumeSessionAsync(RuntimeSpec spec, string runtimeSessionId) => Task.FromResult(Directory.Exists(spec.DshHome));

    /// <inheritdoc />
    public async IAsyncEnumerable<JsonObject> SendMessageAsync(RunRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        if (req.ExecutionActor != null && (!req.ExecutionActor.IsPlatformAdmin || opt.Value.Sandbox.Enabled))
        {
            await foreach (var item in sandbox.SendAgentAsync(req, ct)) yield return item;
            yield break;
        }
        var args = new List<string> { "headless" };
        if (!string.IsNullOrEmpty(req.PatchFile) && File.Exists(req.PatchFile)) { args.Add("--patch"); args.Add(req.PatchFile); }
        args.Add("--json");
        if (!string.IsNullOrEmpty(req.RuntimeSessionId)) { args.Add("--session-id"); args.Add(req.RuntimeSessionId); }
        args.Add("-"); // 任务文本从 stdin 读取，避免命令行转义与长度问题

        var extra = new Dictionary<string, string>(req.Env) { ["DSH_HOME"] = req.DshHome, ["NODE_NO_WARNINGS"] = "1" };
        var env = ProcessUtil.BuildEnv(opt.Value.Runtime.EnvPassthrough, extra);

        Process? proc = null; string? startError = null;
        try { proc = Process.Start(ProcessUtil.CreateStartInfo(opt.Value.Runtime.DshExecutable, args, req.WorkspaceRoot, env)); }
        catch (Exception ex) { startError = ex.Message; }
        if (proc == null)
        {
            yield return Err("无法启动 dsh：" + (startError ?? "未知错误"));
            yield break;
        }

        using var procLease = proc;
        _active[req.RunId] = (req.PoolId, proc);
        var stderrTail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            string? l; while ((l = await proc.StandardError.ReadLineAsync()) != null) { lock (stderrTail) { stderrTail.Enqueue(l); if (stderrTail.Count > 30) stderrTail.Dequeue(); } }
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(req.Timeout);
        using var killReg = timeoutCts.Token.Register(() => ProcessUtil.KillTree(proc));
        var sawFinal = false;
        try
        {
            await proc.StandardInput.WriteAsync(req.Prompt);
            proc.StandardInput.Close();
            string? line;
            while ((line = await ReadLineSafe(proc.StandardOutput)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? evt = null;
                try { evt = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { /* 非 JSON 行忽略 */ }
                if (evt == null) continue;
                if (evt["type"]?.GetValue<string>() == "final") sawFinal = true;
                yield return evt;
            }
            await proc.WaitForExitAsync(CancellationToken.None);
        }
        finally
        {
            _active.TryRemove(req.RunId, out _);
            ProcessUtil.KillTree(proc);
        }

        if (ct.IsCancellationRequested) yield break;
        if (timeoutCts.IsCancellationRequested) { yield return Err($"运行超过 {req.Timeout.TotalMinutes:0} 分钟，已被终止"); yield break; }
        if (proc.ExitCode != 0 && !sawFinal)
        {
            await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });
            string tail; lock (stderrTail) tail = string.Join("\n", stderrTail);
            logger.LogWarning("dsh 非正常退出 code={Code} stderr={Tail}", proc.ExitCode, tail);
            yield return Err($"dsh 退出码 {proc.ExitCode}：{tail}");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetFilesAsync(string workspaceRoot, string? relative) =>
        Task.FromResult<IReadOnlyList<FileEntry>>(FileStore.List(workspaceRoot, relative));

    /// <inheritdoc />
    public Task<ITerminalSession> OpenTerminalAsync(TerminalRequest req) => Task.FromResult<ITerminalSession>(new ShellTerminal(req));

    /// <summary>读取一行；进程被终止导致的读取异常视为流结束。</summary>
    /// <param name="r">标准输出读取器。</param>
    private static async Task<string?> ReadLineSafe(StreamReader r)
    {
        try { return await r.ReadLineAsync(); } catch (Exception) { return null; }
    }

    /// <summary>构造错误事件。</summary>
    /// <param name="message">错误信息。</param>
    private static JsonObject Err(string message) => new() { ["type"] = "error", ["message"] = message };

    /// <summary>首次创建 Pool 的 DSH_HOME 时，从 SeedHome 复制基础凭据与设置（不覆盖已有文件）。</summary>
    /// <param name="dshHome">目标 DSH_HOME。</param>
    private void SeedHome(string dshHome)
    {
        var seed = opt.Value.Runtime.SeedHome;
        if (string.IsNullOrWhiteSpace(seed) || !Directory.Exists(seed)) return;
        foreach (var f in new[] { ".credentials.yaml", "settings.yaml" })
        {
            var src = Path.Combine(seed, f); var dst = Path.Combine(dshHome, f);
            if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
        }
    }
}
