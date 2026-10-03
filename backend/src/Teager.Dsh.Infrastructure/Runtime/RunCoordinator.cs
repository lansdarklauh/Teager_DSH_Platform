using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>一次待执行的运行任务。</summary>
/// <param name="RunId">运行 ID。</param>
/// <param name="SessionId">会话 ID。</param>
/// <param name="Actor">发起人。</param>
/// <param name="Prompt">用户输入。</param>
/// <param name="ModelKey">请求的模型键。</param>
public record RunJob(string RunId, string SessionId, Actor Actor, string Prompt, string? ModelKey);

/// <summary>
/// 运行协调器（单例）：在后台执行 Agent 运行——启动 Runtime、物化环境、消费 Provider 事件流、写事件日志、
/// 落库消息与用量、检测共享空间被 Agent 修改的情况并审计。运行与 HTTP 请求解耦，浏览器断开不会中断任务。
/// </summary>
public class RunCoordinator(IServiceScopeFactory scopes, RuntimeState state, SessionEventStore events, ILogger<RunCoordinator> logger, IOptions<DshOptions> opt)
{
    /// <summary>登记并在后台启动运行。登记在同步阶段完成，使并发计数立即生效。</summary>
    /// <param name="job">运行任务。</param>
    /// <param name="poolId">Pool ID。</param>
    public void Start(RunJob job, string poolId)
    {
        var cts = new CancellationTokenSource();
        state.Runs[job.RunId] = (poolId, job.SessionId, cts);
        _ = Task.Run(async () =>
        {
            try { await ExecuteAsync(job, cts); }
            catch (Exception ex) { logger.LogError(ex, "运行 {RunId} 执行器异常", job.RunId); }
            finally { state.Runs.TryRemove(job.RunId, out _); cts.Dispose(); }
        });
    }

    /// <summary>取消会话中正在运行的任务。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <returns>是否有任务被取消。</returns>
    public bool Cancel(string sessionId)
    {
        var any = false;
        foreach (var r in state.Runs.Where(r => r.Value.SessionId == sessionId).ToList()) { r.Value.Cts.Cancel(); any = true; }
        return any;
    }

    /// <summary>执行一次运行的完整流程。</summary>
    /// <param name="job">任务。</param>
    /// <param name="cts">取消源。</param>
    private async Task ExecuteAsync(RunJob job, CancellationTokenSource cts)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DshDbContext>();
        var audit = sp.GetRequiredService<AuditService>();
        var run = await db.AgentRuns.FirstAsync(r => r.Id == job.RunId);
        var session = await db.Sessions.FirstAsync(s => s.Id == job.SessionId);
        var ws = await db.Workspaces.FirstAsync(w => w.Id == session.WorkspaceId);
        var pool = await db.Pools.FirstAsync(p => p.Id == ws.PoolId);
        var runtime = sp.GetRequiredService<RuntimeManager>();
        events.Init(session.Id, session.CreatedAt, session.LastEventSeq);

        JsonObject Emit(JsonObject e) => events.Append(session.Id, session.CreatedAt, run.Id, e);
        var sw = Stopwatch.StartNew();
        var status = RunStatus.Completed; string? error = null; string? finalText = null; string? runtimeSessionId = session.RuntimeSessionId;
        var text = new StringBuilder(); long inTok = 0, outTok = 0; var tools = 0;
        List<ActiveMount> mounts = []; var before = new Dictionary<string, Dictionary<string, (long, long)>>();

        Emit(new JsonObject { ["type"] = "run_start", ["prompt"] = job.Prompt, ["user"] = job.Actor.Username });
        try
        {
            await runtime.EnsureStartedAsync(pool);
            await runtime.MarkRunStartedAsync(pool.Id);
            var engine = sp.GetRequiredService<PermissionEngine>();
            var wa = await engine.GetWorkspaceAccessAsync(job.Actor, ws);
            var prep = await sp.GetRequiredService<RuntimeMaterializer>().PrepareAsync(job.Actor, ws, wa, run.Id, runtimeSessionId, job.Prompt, job.ModelKey, pool);
            mounts = prep.Mounts;
            run.Model = prep.Model;
            foreach (var m in mounts) before[m.MountName] = await Task.Run(() => FileStore.Manifest(m.Directory));

            var provider = runtime.Provider(pool.RuntimeProvider);
            var req = prep.Request;
            if (runtimeSessionId != null && !await provider.ResumeSessionAsync(new RuntimeSpec(pool.Id, req.DshHome), runtimeSessionId)) { runtimeSessionId = null; req = req with { RuntimeSessionId = null }; }
            if (runtimeSessionId == null) { runtimeSessionId = await provider.CreateSessionAsync(req); req = req with { RuntimeSessionId = runtimeSessionId }; }

            await foreach (var evt in provider.SendMessageAsync(req, cts.Token))
            {
                var type = evt["type"]?.GetValue<string>();
                switch (type)
                {
                    case "session": runtimeSessionId = evt["sessionId"]?.GetValue<string>() ?? runtimeSessionId; break;
                    case "text": text.Append(evt["text"]?.GetValue<string>()); break;
                    case "final": finalText = evt["text"]?.GetValue<string>(); break;
                    case "tool_call": tools++; break;
                    case "error": status = RunStatus.Failed; error = evt["message"]?.GetValue<string>() ?? "运行失败"; break;
                    case "status":
                        if (evt["usage"] is JsonObject u) { inTok += u["inputTokens"]?.GetValue<long>() ?? 0; outTok += u["outputTokens"]?.GetValue<long>() ?? 0; }
                        // DSH 把模型 / 凭据类错误放在 turn_end 的 reason 中
                        if (evt["reason"] is JsonObject r && r["kind"]?.GetValue<string>() == "error") { status = RunStatus.Failed; error = r["error"]?["message"]?.GetValue<string>() ?? "运行失败"; }
                        break;
                }
                Emit(evt);
            }
            if (cts.IsCancellationRequested) status = RunStatus.Cancelled;
        }
        catch (OperationCanceledException) { status = RunStatus.Cancelled; }
        catch (ApiException ex) { status = RunStatus.Failed; error = ex.Message; Emit(new JsonObject { ["type"] = "error", ["message"] = ex.Message }); }
        catch (Exception ex)
        {
            status = RunStatus.Failed; error = "平台内部错误：" + ex.Message;
            logger.LogError(ex, "运行 {RunId} 失败", run.Id);
            Emit(new JsonObject { ["type"] = "error", ["message"] = error });
        }
        if (status == RunStatus.Completed && cts.IsCancellationRequested) status = RunStatus.Cancelled;

        // 共享空间变更检测：对比运行前后的文件清单，RO 挂载被改视为违规，RW 挂载被改记为写审计
        foreach (var m in mounts)
        {
            try
            {
                var after = await Task.Run(() => FileStore.Manifest(m.Directory));
                var b = before.GetValueOrDefault(m.MountName) ?? new();
                var added = after.Keys.Except(b.Keys).ToList(); var deleted = b.Keys.Except(after.Keys).ToList();
                var modified = after.Where(kv => b.TryGetValue(kv.Key, out var old) && old != kv.Value).Select(kv => kv.Key).ToList();
                if (added.Count + deleted.Count + modified.Count == 0) continue;
                var sample = string.Join(", ", added.Concat(modified).Concat(deleted).Take(5));
                var detail = $"Agent 运行期间共享空间「{m.SpaceName}」(shared/{m.MountName})：新增 {added.Count}、修改 {modified.Count}、删除 {deleted.Count}；示例：{sample}";
                if (m.Permission == SharePermission.RO)
                {
                    Emit(new JsonObject { ["type"] = "alert", ["level"] = "error", ["message"] = $"检测到对只读共享空间「{m.SpaceName}」的修改（{added.Count + modified.Count + deleted.Count} 处），已记录审计" });
                    await audit.LogAsync(job.Actor, "sharedspace.ro_violation", "SharedSpace", m.SharedSpaceId, pool.Id, ws.Id, detail, "denied");
                }
                else
                {
                    if (added.Count + modified.Count > 0) await audit.LogAsync(job.Actor, "sharedspace.write", "SharedSpace", m.SharedSpaceId, pool.Id, ws.Id, detail + "（来源：Agent）");
                    if (deleted.Count > 0) await audit.LogAsync(job.Actor, "sharedspace.delete", "SharedSpace", m.SharedSpaceId, pool.Id, ws.Id, detail + "（来源：Agent）");
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "共享空间变更检测失败 {Mount}", m.MountName); }
        }

        sw.Stop();
        try
        {
            var max = opt.Value.Platform.MessageMaxChars;
            var reply = status == RunStatus.Failed ? $"❌ 运行失败：{error}" : status == RunStatus.Cancelled ? "⏹ 已取消" : (finalText ?? text.ToString());
            if (status == RunStatus.Completed && string.IsNullOrWhiteSpace(reply)) reply = "（无文本输出）";
            if (reply.Length > max) reply = reply[..max] + "\n…（内容过长已截断，完整过程见事件日志）";
            db.SessionMessages.Add(new SessionMessage { SessionId = session.Id, Role = status == RunStatus.Completed ? "assistant" : "system", Content = reply, RunId = run.Id });
            run.Status = status; run.ErrorMessage = error; run.FinishedAt = DateTime.Now; run.InputTokens = inTok; run.OutputTokens = outTok; run.ToolCalls = tools;
            session.RuntimeSessionId = runtimeSessionId; session.LastMessageAt = DateTime.Now; session.UpdatedAt = DateTime.Now; session.MessageCount += 1;
            ws.LastActiveAt = DateTime.Now;
            db.UsageLogs.Add(new UsageLog { UserId = job.Actor.UserId, PoolId = pool.Id, WorkspaceId = ws.Id, SessionId = session.Id, RunId = run.Id, Model = run.Model, InputTokens = inTok, OutputTokens = outTok, ToolCalls = tools, DurationMs = sw.ElapsedMilliseconds, Success = status == RunStatus.Completed });
            // 一个会话同一时刻只有一个运行，因此 run_end 的 seq 必为当前序号 + 1；先落库再广播 run_end，保证 wait=true 的调用方读到 run_end 时消息已入库
            session.LastEventSeq = events.CurrentSeq(session.Id) + 1;
            await db.SaveChangesAsync();
            state.Runs.TryRemove(run.Id, out _);
            await runtime.MarkRunFinishedAsync(pool.Id);
            await audit.LogAsync(job.Actor, "session.run", "Session", session.Id, pool.Id, ws.Id, $"状态 {status}，耗时 {sw.ElapsedMilliseconds}ms，token {inTok}+{outTok}，工具调用 {tools}" + (error != null ? "，错误：" + error : ""), status == RunStatus.Failed ? "failed" : "success");
            Emit(new JsonObject { ["type"] = "run_end", ["status"] = status.ToString(), ["error"] = error, ["inputTokens"] = inTok, ["outputTokens"] = outTok, ["durationMs"] = sw.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "运行 {RunId} 收尾失败", run.Id);
            Emit(new JsonObject { ["type"] = "run_end", ["status"] = "Failed", ["error"] = "收尾失败：" + ex.Message });
        }
    }
}
