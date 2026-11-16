using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Api.Controllers;

/// <summary>创建会话请求。</summary>
/// <param name="WorkspaceId">Workspace ID。</param>
/// <param name="Title">标题。</param>
/// <param name="Model">模型键。</param>
public record CreateSessionRequest(string WorkspaceId, string? Title, string? Model);
/// <summary>更新会话请求。</summary>
/// <param name="Title">标题。</param>
/// <param name="Archived">是否归档。</param>
/// <param name="Model">模型键（空串表示恢复默认）。</param>
public record UpdateSessionRequest(string? Title, bool? Archived, string? Model);
/// <summary>发送消息请求。</summary>
/// <param name="Content">消息内容。</param>
/// <param name="Model">本次使用的模型。</param>
/// <param name="RequestId">Optional retry idempotency key.</param>
/// <param name="ProjectIds">Projects belonging to the session Workspace.</param>
public record SendMessageRequest(string Content, string? Model, string? RequestId = null, List<string>? ProjectIds = null);

/// <summary>会话与 Agent 交互：创建 / 检索 / 发送消息（异步或同步等待）/ SSE 流式事件 / 取消 / 导出。Web 与 SDK 走同一套接口。</summary>
public class SessionsController(ActorAccessor accessor, SessionService sessions, SessionEventStore events, RuntimeState state, AuditService audit, DshDbContext db,
    Microsoft.Extensions.Options.IOptions<Teager.Dsh.Infrastructure.Options.DshOptions> opt) : ApiBase(accessor)
{
    /// <summary>创建会话。</summary>
    [HttpPost("sessions"), Scope(Scopes.SessionCreate)]
    public async Task<object> Create(CreateSessionRequest req)
    {
        var s = await sessions.CreateAsync(Me, req.WorkspaceId, req.Title, req.Model);
        await audit.LogAsync(Me, "session.create", "Session", s.Id, s.PoolId, s.WorkspaceId, $"创建会话「{s.Title}」");
        return sessions.ToView(s);
    }

    /// <summary>会话列表（按 Workspace，可检索标题与消息内容）。</summary>
    [HttpGet("sessions"), Scope(Scopes.SessionRead)]
    public async Task<object> List([FromQuery] string workspaceId, [FromQuery] string? q, [FromQuery] bool includeArchived = false) => await sessions.ListAsync(Me, workspaceId, q, includeArchived);

    /// <summary>跨 Workspace 搜索会话。</summary>
    [HttpGet("sessions/search"), Scope(Scopes.SessionRead)]
    public async Task<object> Search([FromQuery] string q) => await sessions.SearchAsync(Me, q);

    /// <summary>会话详情。</summary>
    [HttpGet("sessions/{id}"), Scope(Scopes.SessionRead)]
    public async Task<object> Get(string id)
    {
        var (s, ws, wa) = await sessions.RequireAsync(Me, id, WorkspaceRole.Viewer);
        return new { Session = sessions.ToView(s), Workspace = new { ws.Id, ws.Name, ws.PoolId }, MyRole = wa.Role?.ToString(), LastEventSeq = s.LastEventSeq };
    }

    /// <summary>重命名 / 归档 / 切换模型。</summary>
    [HttpPatch("sessions/{id}"), Scope(Scopes.SessionCreate)]
    public async Task<object> Update(string id, UpdateSessionRequest req)
    {
        var s = await sessions.UpdateAsync(Me, id, req.Title, req.Archived, req.Model);
        await audit.LogAsync(Me, "session.update", "Session", id, s.PoolId, s.WorkspaceId, $"标题={s.Title}，状态={s.Status}");
        return sessions.ToView(s);
    }

    /// <summary>删除会话。</summary>
    [HttpDelete("sessions/{id}"), Scope(Scopes.SessionCreate)]
    public async Task<IActionResult> Delete(string id)
    {
        var s = await sessions.DeleteAsync(Me, id);
        await audit.LogAsync(Me, "session.delete", "Session", id, s.PoolId, s.WorkspaceId, $"删除会话「{s.Title}」");
        return NoContent();
    }

    /// <summary>复制会话（复制标题与消息文本）。</summary>
    [HttpPost("sessions/{id}/duplicate"), Scope(Scopes.SessionCreate)]
    public async Task<object> Duplicate(string id)
    {
        var s = await sessions.DuplicateAsync(Me, id);
        await audit.LogAsync(Me, "session.duplicate", "Session", s.Id, s.PoolId, s.WorkspaceId, $"复制自 {id}");
        return sessions.ToView(s);
    }

    /// <summary>导出会话：format = md / json。</summary>
    [HttpGet("sessions/{id}/export"), Scope(Scopes.SessionRead)]
    public async Task<IActionResult> Export(string id, [FromQuery] string format = "md")
    {
        var (name, type, text) = await sessions.ExportAsync(Me, id, format);
        var s = await db.Sessions.AsNoTracking().FirstAsync(x => x.Id == id);
        await audit.LogAsync(Me, "session.export", "Session", id, s.PoolId, s.WorkspaceId, $"导出 {format}");
        return File(System.Text.Encoding.UTF8.GetBytes(text), type + "; charset=utf-8", name);
    }

    /// <summary>消息列表（文本摘要）。</summary>
    [HttpGet("sessions/{id}/messages"), Scope(Scopes.SessionRead)]
    public async Task<object> Messages(string id, [FromQuery] long after = 0, [FromQuery] int limit = 200)
        => (await sessions.MessagesAsync(Me, id, after, limit)).Select(m => new { m.Id, m.Role, m.Content, m.RunId, m.CreatedAt, Projects = ProjectReferences.Read(m.ProjectReferencesJson) });

    /// <summary>
    /// 发送消息。默认异步：立即返回 202 与 runId，通过 events 接口订阅流式结果；
    /// wait=true 时同步等待运行结束并返回最终文本（SDK 的 session.send() 使用该模式）。
    /// </summary>
    [HttpPost("sessions/{id}/messages"), Scope(Scopes.SessionExecute)]
    public async Task<IActionResult> Send(string id, SendMessageRequest req, [FromQuery] bool wait = false)
    {
        if (!wait)
        {
            var r = await sessions.SendAsync(Me, id, req.Content, req.Model, req.RequestId, req.ProjectIds);
            return Accepted(new { r.RunId, r.SessionId, r.UserMessageId });
        }
        // 同步模式：先订阅再发送，避免错过 run_end
        var (reader, handle) = events.Subscribe(id);
        using var _ = handle;
        var res = await sessions.SendAsync(Me, id, req.Content, req.Model, req.RequestId, req.ProjectIds);
        var deadline = TimeSpan.FromMinutes(opt.Value.Runtime.RunTimeoutMinutes + 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); cts.CancelAfter(deadline);
        var current = await db.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == res.RunId);
        JsonObject? end = current.Status == RunStatus.Running ? null : new JsonObject { ["status"] = current.Status.ToString(), ["error"] = current.ErrorMessage, ["inputTokens"] = current.InputTokens, ["outputTokens"] = current.OutputTokens };
        try
        {
            if (end == null) await foreach (var e in reader.ReadAllAsync(cts.Token))
                if (e["type"]?.GetValue<string>() == "run_end" && e["runId"]?.GetValue<string>() == res.RunId) { end = e; break; }
        }
        catch (OperationCanceledException) { }
        if (end == null) return StatusCode(504, new { code = "run_timeout", message = "等待运行结果超时，任务仍在后台运行，可通过 events 接口继续订阅", runId = res.RunId });
        var msg = await db.SessionMessages.AsNoTracking().Where(m => m.RunId == res.RunId && m.Role != "user").OrderByDescending(m => m.Id).FirstOrDefaultAsync();
        var status = end["status"]?.GetValue<string>();
        return Ok(new { res.RunId, res.SessionId, Status = status, Text = msg?.Content, MessageId = msg?.Id, Error = end["error"]?.GetValue<string>(), InputTokens = end["inputTokens"]?.GetValue<long>(), OutputTokens = end["outputTokens"]?.GetValue<long>(), DurationMs = end["durationMs"]?.GetValue<long>() });
    }

    /// <summary>取消正在运行的任务。</summary>
    [HttpPost("sessions/{id}/cancel"), Scope(Scopes.SessionExecute)]
    public async Task<object> Cancel(string id)
    {
        var ok = await sessions.CancelAsync(Me, id);
        var s = await db.Sessions.AsNoTracking().FirstAsync(x => x.Id == id);
        if (ok) await audit.LogAsync(Me, "session.cancel", "Session", id, s.PoolId, s.WorkspaceId, "取消运行");
        return new { Cancelled = ok };
    }

    /// <summary>运行记录。</summary>
    [HttpGet("sessions/{id}/runs"), Scope(Scopes.SessionRead)]
    public async Task<object> Runs(string id)
        => (await sessions.RunsAsync(Me, id)).Select(r => new { r.Id, Status = r.Status.ToString(), r.Model, r.StartedAt, r.FinishedAt, r.InputTokens, r.OutputTokens, r.ToolCalls, r.ErrorMessage });

    /// <summary>历史事件（JSON 数组，来自文件日志）：用于回放某次运行的思考与工具调用过程。</summary>
    [HttpGet("sessions/{id}/events/history"), Scope(Scopes.SessionRead)]
    public async Task<object> History(string id, [FromQuery] string? runId, [FromQuery] long after = 0)
    {
        var (_, list) = await sessions.EventsAsync(Me, id, after, runId);
        return list;
    }

    /// <summary>
    /// SSE 事件流：先按 after 回放历史事件，再实时跟随；运行结束（run_end）后关闭。没有运行中的任务时回放完即关闭。
    /// 断线重连时带上最后收到的 seq 即可无缝续传。
    /// </summary>
    [HttpGet("sessions/{id}/events"), Scope(Scopes.SessionRead)]
    public async Task Events(string id, [FromQuery] long after = 0, [FromQuery] bool follow = true)
    {
        var (s, _, _) = await sessions.RequireAsync(Me, id, WorkspaceRole.Viewer);
        Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // 关闭 nginx 缓冲，保证流式
        await Response.Body.FlushAsync();

        var (reader, handle) = events.Subscribe(id);
        using var _h = handle;
        var last = after;
        async Task WriteAsync(JsonObject e)
        {
            var seq = e["seq"]?.GetValue<long>() ?? 0;
            if (seq <= last) return;
            last = seq;
            await Response.WriteAsync($"id: {seq}\nevent: {e["type"]?.GetValue<string>() ?? "message"}\ndata: {e.ToJsonString(JsonSerializerOptions.Web)}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }
        foreach (var e in events.Read(id, s.CreatedAt, after)) await WriteAsync(e);
        // 没有运行中的任务（或调用方不要求跟随）：补读一次回放期间可能新增的事件后结束
        if (!follow || !state.Runs.Any(r => r.Value.SessionId == id))
        {
            foreach (var e in events.Read(id, s.CreatedAt, last)) await WriteAsync(e);
            return;
        }
        try
        {
            while (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                using var hb = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted); hb.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await reader.WaitToReadAsync(hb.Token)) break;
                    while (reader.TryRead(out var e))
                    {
                        await WriteAsync(e);
                        if (e["type"]?.GetValue<string>() == "run_end") return;
                    }
                }
                catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    await Response.WriteAsync(": ping\n\n"); await Response.Body.FlushAsync(); // 心跳，防止代理断开空闲连接
                    if (!state.Runs.Any(r => r.Value.SessionId == id)) return; // 运行已结束但错过事件时兜底退出
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
