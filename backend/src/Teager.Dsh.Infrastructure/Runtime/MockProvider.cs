using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Teager.Dsh.Infrastructure.Storage;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Domain;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>
/// 模拟 Runtime Provider：不依赖 DSH 与模型，用于开发、联调、演示与自动化测试。
/// 事件格式与 DSH headless --json 保持一致。支持的指令：
/// <c>/sleep N</c> 等待 N 秒（测试取消与并发）；<c>/fail</c> 模拟失败；<c>/write 相对路径 内容</c> 写入 Workspace 内文件（测试共享空间读写与 RO 检测）。
/// </summary>
public class MockProvider(DockerSandbox sandbox, IServiceScopeFactory scopes) : IRuntimeProvider
{
    private readonly ConcurrentDictionary<string, int> _active = new();

    /// <inheritdoc />
    public string Name => "Mock";

    /// <inheritdoc />
    public Task<RuntimeHealth> StartRuntimeAsync(RuntimeSpec spec, CancellationToken ct)
    {
        Directory.CreateDirectory(spec.DshHome);
        return Task.FromResult(new RuntimeHealth(true, "mock-1.0", "模拟 Runtime 已就绪", 0));
    }

    /// <inheritdoc />
    public Task StopRuntimeAsync(string poolId) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<RuntimeHealth> RestartRuntimeAsync(RuntimeSpec spec, CancellationToken ct) => StartRuntimeAsync(spec, ct);

    /// <inheritdoc />
    public Task<RuntimeHealth> HealthCheckAsync(RuntimeSpec spec, CancellationToken ct) =>
        Task.FromResult(new RuntimeHealth(true, "mock-1.0", "模拟 Runtime 正常", _active.Count));

    /// <inheritdoc />
    public Task<string?> CreateSessionAsync(RunRequest req) => Task.FromResult<string?>("mock-" + Guid.NewGuid().ToString("N")[..12]);

    /// <inheritdoc />
    public Task<bool> ResumeSessionAsync(RuntimeSpec spec, string runtimeSessionId) => Task.FromResult(true);

    /// <inheritdoc />
    public async IAsyncEnumerable<JsonObject> SendMessageAsync(RunRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        _active[req.RunId] = 1;
        try
        {
            var sid = req.RuntimeSessionId ?? "mock-" + Guid.NewGuid().ToString("N")[..12];
            yield return new JsonObject { ["type"] = "session", ["sessionId"] = sid, ["cwd"] = req.WorkspaceRoot };
            yield return new JsonObject { ["type"] = "status", ["phase"] = "turn_start" };
            yield return new JsonObject { ["type"] = "status", ["phase"] = "step_start", ["step"] = 1 };
            yield return new JsonObject { ["type"] = "thinking", ["text"] = "（模拟）正在理解你的请求……" };
            var prompt = req.Prompt.Trim();
            long inTok = Math.Max(20, prompt.Length * 2), outTok = 0;

            if (prompt.StartsWith("/sleep", StringComparison.OrdinalIgnoreCase))
            {
                var n = int.TryParse(prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var s) ? s : 5;
                for (var i = 0; i < n * 5; i++) { await Task.Delay(200, ct); if (i % 5 == 4) yield return new JsonObject { ["type"] = "text", ["text"] = $"…已等待 {(i + 1) / 5} 秒\n" }; }
            }
            if (prompt.StartsWith("/fail", StringComparison.OrdinalIgnoreCase))
            {
                yield return new JsonObject { ["type"] = "error", ["message"] = "（模拟）运行失败" };
                yield break;
            }

            var toolCalls = 0;
            if (prompt.StartsWith("/write ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = prompt.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var rel = parts.ElementAtOrDefault(1) ?? "files/mock-output.txt"; var text = parts.ElementAtOrDefault(2) ?? "hello from mock";
                var callId = "call_" + Guid.NewGuid().ToString("N")[..8];
                yield return new JsonObject { ["type"] = "tool_call", ["callId"] = callId, ["tool"] = "write", ["input"] = new JsonObject { ["path"] = rel, ["content"] = text } };
                string status, result;
                try
                {
                    await using var lease = await sandbox.AcquireFileLeaseAsync(ct);
                    if (req.Env.GetValueOrDefault("DSH_PERMISSION_MODE") == "read-only") throw ApiException.Forbidden("Agent 文件写入被只读策略禁止");
                    var normalized = rel.Replace('\\', '/');
                    string root, path;
                    if (normalized.StartsWith("files/", StringComparison.Ordinal)) { root = Path.Combine(req.WorkspaceRoot, "files"); path = normalized[6..]; }
                    else if (normalized.StartsWith("shared/", StringComparison.Ordinal))
                    {
                        var segments = normalized.Split('/', 3);
                        var mount = req.Mounts.FirstOrDefault(m => m.MountName == segments.ElementAtOrDefault(1)) ?? throw ApiException.Forbidden("共享空间未授权");
                        if (mount.Permission < SharePermission.RW) throw ApiException.Forbidden("共享空间只读");
                        root = mount.Directory; path = segments.ElementAtOrDefault(2) ?? "";
                    }
                    else throw ApiException.Forbidden("只能写入个人 files 或已授权的共享文件区");
                    await using var scope = scopes.CreateAsyncScope();
                    var fileAccess = scope.ServiceProvider.GetRequiredService<FileAccessService>();
                    var areaName = normalized.StartsWith("files/", StringComparison.Ordinal) ? "files" : "shared/" + normalized.Split('/')[1];
                    var area = await fileAccess.ResolveWorkspaceAreaAsync(req.ExecutionActor!, req.WorkspaceId, areaName, true);
                    await fileAccess.EnsureQuotaAsync(area, System.Text.Encoding.UTF8.GetByteCount(text), path);
                    await using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
                    await FileStore.WriteAsync(root, path, content, 1024 * 1024);
                    status = "ok"; result = $"已写入 {rel}（{text.Length} 字符）";
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { status = "error"; result = "写入失败：" + ex.Message; }
                toolCalls++;
                yield return new JsonObject { ["type"] = "tool_result", ["callId"] = callId, ["status"] = status, ["result"] = result };
            }
            else if (prompt.StartsWith("/ls", StringComparison.OrdinalIgnoreCase))
            {
                var callId = "call_" + Guid.NewGuid().ToString("N")[..8];
                yield return new JsonObject { ["type"] = "tool_call", ["callId"] = callId, ["tool"] = "ls", ["input"] = new JsonObject { ["path"] = "." } };
                var names = string.Join(", ", Directory.EnumerateFileSystemEntries(req.WorkspaceRoot).Select(Path.GetFileName));
                toolCalls++;
                yield return new JsonObject { ["type"] = "tool_result", ["callId"] = callId, ["status"] = "ok", ["result"] = names };
            }

            var reply = $"（模拟回复）已收到：{(prompt.Length > 200 ? prompt[..200] + "…" : prompt)}。Workspace 目录：{Path.GetFileName(req.WorkspaceRoot)}，会话：{sid}。";
            foreach (var chunk in Chunk(reply, 12))
            {
                await Task.Delay(30, ct);
                outTok += chunk.Length;
                yield return new JsonObject { ["type"] = "text", ["text"] = chunk };
            }
            yield return new JsonObject { ["type"] = "status", ["phase"] = "step_end", ["step"] = 1, ["usage"] = new JsonObject { ["inputTokens"] = inTok, ["outputTokens"] = outTok, ["totalTokens"] = inTok + outTok } };
            yield return new JsonObject { ["type"] = "status", ["phase"] = "turn_end", ["reason"] = new JsonObject { ["kind"] = "completed" } };
            yield return new JsonObject { ["type"] = "final", ["text"] = reply };
        }
        finally { _active.TryRemove(req.RunId, out _); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetFilesAsync(string workspaceRoot, string? relative) =>
        Task.FromResult<IReadOnlyList<FileEntry>>(FileStore.List(workspaceRoot, relative));

    /// <inheritdoc />
    public Task<ITerminalSession> OpenTerminalAsync(TerminalRequest req) => Task.FromResult<ITerminalSession>(new ShellTerminal(req));

    /// <summary>按固定长度切分文本，模拟流式输出。</summary>
    /// <param name="s">文本。</param>
    /// <param name="n">块长度。</param>
    private static IEnumerable<string> Chunk(string s, int n) { for (var i = 0; i < s.Length; i += n) yield return s.Substring(i, Math.Min(n, s.Length - i)); }
}
