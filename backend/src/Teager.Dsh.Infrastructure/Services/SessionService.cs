using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>发送消息后的运行信息。</summary>
/// <param name="RunId">运行 ID。</param>
/// <param name="SessionId">会话 ID。</param>
/// <param name="UserMessageId">用户消息 ID。</param>
public record SendResult(string RunId, string SessionId, long UserMessageId);

/// <summary>会话服务：创建 / 检索 / 重命名 / 归档 / 删除 / 复制 / 导出 / 发送消息 / 取消。Session 是一等对象，隶属于 Workspace。</summary>
public class SessionService(DshDbContext db, PermissionEngine engine, PolicyService policy, RunCoordinator coordinator, RuntimeState state, SessionEventStore events, ConfigService config, StoragePaths paths)
{
    /// <summary>创建会话（需要 Workspace Member 及以上）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="title">标题（空则首条消息自动生成）。</param>
    /// <param name="modelKey">模型键（空为默认）。</param>
    /// <returns>新会话。</returns>
    public async Task<Session> CreateAsync(Actor actor, string workspaceId, string? title, string? modelKey)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Member);
        if (ws.Status != WorkspaceStatus.Active) throw ApiException.Conflict("Workspace 已归档，不能创建会话");
        if (!string.IsNullOrEmpty(modelKey)) await ValidateModelAsync(ws, modelKey);
        var s = new Session { Id = IdGen.New("ses"), WorkspaceId = ws.Id, PoolId = ws.PoolId, Title = string.IsNullOrWhiteSpace(title) ? "新会话" : Cut(title.Trim(), 100), ModelKey = modelKey, CreatedBy = actor.UserId };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    /// <summary>加载会话并校验 Workspace 权限。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="min">最小 Workspace 角色。</param>
    /// <returns>会话、Workspace 与访问结果。</returns>
    public async Task<(Session Session, Workspace Ws, WorkspaceAccess Access)> RequireAsync(Actor actor, string sessionId, WorkspaceRole min)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == sessionId) ?? throw ApiException.NotFound("会话不存在或无权访问");
        var (ws, wa) = await engine.RequireWorkspaceAsync(actor, s.WorkspaceId, min);
        return (s, ws, wa);
    }

    /// <summary>列出 Workspace 的会话（可按标题 / 消息内容检索）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="q">关键字。</param>
    /// <param name="includeArchived">是否包含归档。</param>
    /// <returns>会话列表（含是否运行中）。</returns>
    public async Task<List<object>> ListAsync(Actor actor, string workspaceId, string? q, bool includeArchived)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Viewer);
        var query = db.Sessions.AsNoTracking().Where(s => s.WorkspaceId == ws.Id);
        if (!includeArchived) query = query.Where(s => s.Status == SessionStatus.Active);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Title.Contains(q) || db.SessionMessages.Any(m => m.SessionId == s.Id && m.Content.Contains(q)));
        var list = await query.OrderByDescending(s => s.UpdatedAt).Take(200).ToListAsync();
        return list.Select(s => (object)View(s)).ToList();
    }

    /// <summary>跨 Workspace 搜索会话：在当前用户可访问的 Workspace 内按标题 / 消息内容检索。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="q">关键字。</param>
    /// <returns>命中会话列表。</returns>
    public async Task<List<object>> SearchAsync(Actor actor, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return [];
        var accessible = (await new WorkspaceListHelper(db, engine).AccessibleWorkspaceIdsAsync(actor));
        var hits = await db.Sessions.AsNoTracking().Where(s => accessible.Contains(s.WorkspaceId) && (s.Title.Contains(q) || db.SessionMessages.Any(m => m.SessionId == s.Id && m.Content.Contains(q))))
            .OrderByDescending(s => s.UpdatedAt).Take(50).ToListAsync();
        var wsNames = await db.Workspaces.AsNoTracking().Where(w => accessible.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);
        return hits.Select(s => (object)new { s.Id, s.Title, s.WorkspaceId, WorkspaceName = wsNames.GetValueOrDefault(s.WorkspaceId), s.UpdatedAt, s.MessageCount }).ToList();
    }

    /// <summary>更新会话：重命名、归档 / 取消归档、切换模型（创建人或 Workspace 管理员）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="title">新标题。</param>
    /// <param name="archived">是否归档。</param>
    /// <param name="modelKey">新模型键。</param>
    /// <returns>更新后的会话。</returns>
    public async Task<Session> UpdateAsync(Actor actor, string sessionId, string? title, bool? archived, string? modelKey)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DatabaseAdmission.LockAsync(db);
        var (s, ws, wa) = await RequireAsync(actor, sessionId, WorkspaceRole.Member);
        EnsureOwnerOrAdmin(actor, s, wa);
        if ((archived != null || modelKey != null) && await db.AgentRuns.AnyAsync(r => r.SessionId == sessionId && r.Status == RunStatus.Running)) throw ApiException.Conflict("会话正在运行，不能归档或切换模型");
        if (title != null) s.Title = Cut(title.Trim(), 100);
        if (archived != null) s.Status = archived.Value ? SessionStatus.Archived : SessionStatus.Active;
        if (modelKey != null) { if (modelKey.Length > 0) await ValidateModelAsync(ws, modelKey); s.ModelKey = modelKey.Length == 0 ? null : modelKey; }
        s.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return s;
    }

    /// <summary>删除会话（含消息与事件日志；创建人或 Workspace 管理员；运行中不可删）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    public async Task<Session> DeleteAsync(Actor actor, string sessionId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DatabaseAdmission.LockAsync(db);
        var (s, _, wa) = await RequireAsync(actor, sessionId, WorkspaceRole.Member);
        EnsureOwnerOrAdmin(actor, s, wa);
        if (state.Runs.Any(r => r.Value.SessionId == s.Id) || await db.AgentRuns.AnyAsync(r => r.SessionId == s.Id && r.Status == RunStatus.Running)) throw ApiException.Conflict("会话正在运行，请先取消");
        using var trash = new FileTrash(paths, "session", s.Id);
        trash.Stage(paths.RunEventLog(s.Id, s.CreatedAt));
        await DeleteDataAsync(s);
        await transaction.CommitAsync();
        trash.Commit();
        events.Delete(s.Id, s.CreatedAt);
        return s;
    }

    /// <summary>复制会话：复制标题与消息文本，不复制 Runtime 上下文（新会话在下一条消息时开启全新的 Runtime 会话）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">源会话。</param>
    /// <returns>新会话。</returns>
    public async Task<Session> DuplicateAsync(Actor actor, string sessionId)
    {
        var (src, ws, _) = await RequireAsync(actor, sessionId, WorkspaceRole.Member);
        var copy = new Session { Id = IdGen.New("ses"), WorkspaceId = ws.Id, PoolId = ws.PoolId, Title = Cut(src.Title + "（副本）", 100), ModelKey = src.ModelKey, CreatedBy = actor.UserId };
        db.Sessions.Add(copy);
        foreach (var m in await db.SessionMessages.AsNoTracking().Where(m => m.SessionId == src.Id).OrderBy(m => m.Id).ToListAsync())
            db.SessionMessages.Add(new SessionMessage { SessionId = copy.Id, Role = m.Role, Content = m.Content, CreatedAt = m.CreatedAt, ProjectReferencesJson = m.ProjectReferencesJson });
        copy.MessageCount = src.MessageCount;
        await db.SaveChangesAsync();
        return copy;
    }

    /// <summary>分页读取消息。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="afterId">起始消息 ID（不含）。</param>
    /// <param name="limit">最大条数。</param>
    /// <returns>消息列表。</returns>
    public async Task<List<SessionMessage>> MessagesAsync(Actor actor, string sessionId, long afterId, int limit)
    {
        await RequireAsync(actor, sessionId, WorkspaceRole.Viewer);
        return await db.SessionMessages.AsNoTracking().Where(m => m.SessionId == sessionId && m.Id > afterId).OrderBy(m => m.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync();
    }

    /// <summary>导出会话为 Markdown 或 JSON 文本。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="format">md / json。</param>
    /// <returns>文件名、内容类型与文本。</returns>
    public async Task<(string FileName, string ContentType, string Text)> ExportAsync(Actor actor, string sessionId, string format)
    {
        var (s, ws, _) = await RequireAsync(actor, sessionId, WorkspaceRole.Viewer);
        var msgs = await db.SessionMessages.AsNoTracking().Where(m => m.SessionId == s.Id).OrderBy(m => m.Id).ToListAsync();
        var safe = string.Concat(s.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (format == "json")
        {
            var arr = new JsonArray(msgs.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content, ["time"] = m.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), ["projects"] = System.Text.Json.JsonSerializer.SerializeToNode(ProjectReferences.Read(m.ProjectReferencesJson), System.Text.Json.JsonSerializerOptions.Web) }).ToArray());
            var root = new JsonObject { ["id"] = s.Id, ["title"] = s.Title, ["workspace"] = ws.Name, ["createdAt"] = s.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), ["messages"] = arr };
            return (safe + ".json", "application/json", root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"# {s.Title}").AppendLine().AppendLine($"- Workspace：{ws.Name}").AppendLine($"- 创建时间：{s.CreatedAt:yyyy-MM-dd HH:mm:ss}").AppendLine();
        foreach (var m in msgs)
        {
            sb.AppendLine($"## {(m.Role == "user" ? "用户" : m.Role == "assistant" ? "助手" : "系统")}（{m.CreatedAt:HH:mm:ss}）").AppendLine();
            foreach (var project in ProjectReferences.Read(m.ProjectReferencesJson)) sb.AppendLine($"引用项目：{project.Name}（{project.Directory}）");
            sb.AppendLine().AppendLine(m.Content).AppendLine();
        }
        return (safe + ".md", "text/markdown", sb.ToString());
    }

    /// <summary>列出会话的运行记录。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    public async Task<List<AgentRun>> RunsAsync(Actor actor, string sessionId)
    {
        await RequireAsync(actor, sessionId, WorkspaceRole.Viewer);
        return await db.AgentRuns.AsNoTracking().Where(r => r.SessionId == sessionId).OrderByDescending(r => r.StartedAt).Take(100).ToListAsync();
    }

    /// <summary>读取会话的历史事件（来自文件日志）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="afterSeq">起始序号。</param>
    /// <param name="runId">按运行过滤。</param>
    public async Task<(Session Session, List<JsonObject> Events)> EventsAsync(Actor actor, string sessionId, long afterSeq, string? runId)
    {
        var (s, _, _) = await RequireAsync(actor, sessionId, WorkspaceRole.Viewer);
        return (s, events.Read(s.Id, s.CreatedAt, afterSeq, runId));
    }

    /// <summary>发送消息：校验权限 / 配额 / 并发 → 写入用户消息 → 后台启动 Agent 运行。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="content">消息内容。</param>
    /// <param name="modelKey">本次使用的模型（可空）。</param>
    /// <returns>运行信息。</returns>
    /// <param name="requestId">Optional retry idempotency key.</param>
    /// <param name="projectIds">Projects explicitly referenced in this message.</param>
    public async Task<SendResult> SendAsync(Actor actor, string sessionId, string content, string? modelKey, string? requestId = null, IReadOnlyList<string>? projectIds = null)
    {
        if (string.IsNullOrWhiteSpace(content)) throw ApiException.BadRequest("消息内容不能为空");
        if (content.Length > 100_000) throw ApiException.BadRequest("消息过长（上限 100000 字符）");
        var ids = ProjectReferences.ValidateIds(projectIds);
        var (s, ws, _) = await RequireAsync(actor, sessionId, WorkspaceRole.Member);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DatabaseAdmission.LockAsync(db);
        var resource = "dsh:admission:" + ws.PoolId;
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, '任务准入锁超时，请重试', 1;");
        await db.Entry(s).ReloadAsync(); await db.Entry(ws).ReloadAsync();
        actor = await ExecutionAuthorization.RefreshAsync(db, actor);
        await engine.RequireWorkspaceAsync(actor, ws.Id, WorkspaceRole.Member);
        if (ids.Length > 0) actor.RequireScope(Scopes.FilesRead);
        if (requestId is { Length: 0 or > 64 }) throw ApiException.BadRequest("requestId 长度必须为 1—64 字符");
        var requestHash = AuthService.Hash(ids.Length == 0 ? System.Text.Json.JsonSerializer.Serialize(new { sessionId, content, modelKey })
            : System.Text.Json.JsonSerializer.Serialize(new { sessionId, content, modelKey, projectIds = ids }));
        if (requestId != null)
        {
            var requestLock = "dsh:request:" + actor.UserId + ":" + requestId;
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sp_getapplock @Resource={requestLock}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, '请求准入锁超时', 1;");
            var existing = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == actor.UserId && r.RequestId == requestId);
            if (existing != null)
            {
                if (existing.RequestHash != requestHash) throw ApiException.Conflict("requestId 已用于另一个请求，请更换标识");
                var messageId = await db.SessionMessages.Where(m => m.RunId == existing.Id && m.Role == "user").Select(m => m.Id).FirstAsync();
                await transaction.CommitAsync();
                return new SendResult(existing.Id, existing.SessionId, messageId);
            }
        }
        if (ws.Status != WorkspaceStatus.Active) throw ApiException.Conflict("Workspace 已归档，不能发送消息");
        if (s.Status != SessionStatus.Active) throw ApiException.Conflict("会话已归档，请先取消归档");
        var projects = await ProjectReferences.ResolveAsync(db, paths, ws, ids);
        var pool = await db.Pools.FirstOrDefaultAsync(p => p.Id == ws.PoolId && p.Status == PoolStatus.Active) ?? throw ApiException.NotFound("所属 Pool 不存在");
        var selectedModel = modelKey ?? s.ModelKey;
        if (!string.IsNullOrEmpty(selectedModel)) await ValidateModelAsync(ws, selectedModel);

        // 同一会话同一时刻只允许一个运行；Pool 并发与配额检查
        if (state.Runs.Any(r => r.Value.SessionId == s.Id) || await db.AgentRuns.AnyAsync(r => r.SessionId == s.Id && r.Status == RunStatus.Running)) throw ApiException.Conflict("该会话正在运行，请等待完成或先取消");
        var rt = await db.Runtimes.AsNoTracking().FirstOrDefaultAsync(r => r.PoolId == pool.Id);
        if (rt?.Status == RuntimeStatus.MAINTENANCE) throw ApiException.Conflict("该 Pool 的 Runtime 正在维护中，暂不接受新任务");
        var eff = await policy.GetEffectiveAsync(pool.Id);
        if (eff.MaxConcurrentRuns > 0 && await db.AgentRuns.CountAsync(r => r.PoolId == pool.Id && r.Status == RunStatus.Running) >= eff.MaxConcurrentRuns) throw ApiException.Quota($"该 Pool 并发运行数已达上限（{eff.MaxConcurrentRuns}），请稍后重试");
        await policy.EnsureRunQuotaAsync(pool.Id);
        var availableModels = (await config.ResolveAsync(pool.Id, ws.Id)).Where(m => m.Kind == ConfigKind.Model && m.Enabled).ToList();
        var model = availableModels.FirstOrDefault(m => m.Key == selectedModel) ?? availableModels.FirstOrDefault(m => m.IsDefault) ?? availableModels.FirstOrDefault();
        var reservedTokens = ModelExecutionLimits.FromJson(model?.ParamsJson).TaskBudget;
        if (eff.MaxTokensPerDay > 0)
        {
            var today = DateTime.Today;
            var used = await db.UsageLogs.Where(u => u.PoolId == pool.Id && u.Time >= today).SumAsync(u => (long?)(u.InputTokens + u.OutputTokens)) ?? 0;
            var completedReservations = await db.AgentRuns.Where(r => r.PoolId == pool.Id && r.StartedAt >= today && r.Status != RunStatus.Running).SumAsync(r => (long?)r.ReservedTokens) ?? 0;
            used = Math.Max(used, completedReservations);
            var reserved = await db.AgentRuns.Where(r => r.PoolId == pool.Id && r.Status == RunStatus.Running).SumAsync(r => (long?)r.ReservedTokens) ?? 0;
            var remaining = eff.MaxTokensPerDay - used - reserved;
            reservedTokens = reservedTokens == 0 ? remaining : Math.Min(reservedTokens, remaining);
            if (reservedTokens <= 0) throw ApiException.Quota("今日模型额度已用完或已被运行中的任务预留");
        }

        var run = new AgentRun { Id = IdGen.New("run"), SessionId = s.Id, WorkspaceId = ws.Id, PoolId = pool.Id, UserId = actor.UserId, RequestId = requestId, RequestHash = requestId == null ? null : requestHash, ReservedTokens = reservedTokens };
        var um = new SessionMessage { SessionId = s.Id, Role = "user", Content = content, RunId = run.Id, ProjectReferencesJson = projects.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(projects) };
        db.AgentRuns.Add(run); db.SessionMessages.Add(um);
        s.MessageCount += 1; s.LastMessageAt = DateTime.Now; s.UpdatedAt = DateTime.Now;
        if (s.Title == "新会话") s.Title = Cut(content.Replace('\n', ' ').Trim(), 30);
        if (!string.IsNullOrEmpty(modelKey)) s.ModelKey = modelKey;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        coordinator.Start(new RunJob(run.Id, s.Id, actor, content, modelKey ?? s.ModelKey) { Projects = projects }, pool.Id);
        return new SendResult(run.Id, s.Id, um.Id);
    }

    /// <summary>取消会话中正在运行的任务。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sessionId">会话 ID。</param>
    /// <returns>是否取消了运行。</returns>
    public async Task<bool> CancelAsync(Actor actor, string sessionId)
    {
        var (s, _, wa) = await RequireAsync(actor, sessionId, WorkspaceRole.Member);
        var runUser = await db.AgentRuns.Where(r => r.SessionId == s.Id && r.Status == RunStatus.Running).Select(r => r.UserId).FirstOrDefaultAsync();
        if (runUser != null && runUser != actor.UserId && wa.Role < WorkspaceRole.Admin && !actor.IsPlatformAdmin) throw ApiException.Forbidden("只能取消自己发起的运行（或需要 Workspace 管理员权限）");
        return coordinator.Cancel(s.Id);
    }

    /// <summary>删除会话及其数据（消息、运行记录、事件日志文件）。</summary>
    /// <param name="s">会话。</param>
    private async Task DeleteDataAsync(Session s)
    {
        db.SessionMessages.RemoveRange(await db.SessionMessages.Where(m => m.SessionId == s.Id).ToListAsync());
        db.AgentRuns.RemoveRange(await db.AgentRuns.Where(r => r.SessionId == s.Id).ToListAsync());
        db.Sessions.Remove(s);
        await db.SaveChangesAsync();
    }

    /// <summary>创建人或 Workspace 管理员才可修改 / 删除会话。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="s">会话。</param>
    /// <param name="wa">Workspace 访问结果。</param>
    private static void EnsureOwnerOrAdmin(Actor actor, Session s, WorkspaceAccess wa)
    {
        if (s.CreatedBy != actor.UserId && wa.Role < WorkspaceRole.Admin) throw ApiException.Forbidden("只有会话创建人或 Workspace 管理员可以执行该操作");
    }

    /// <summary>校验模型键在该 Workspace 的有效配置中启用。</summary>
    /// <param name="ws">Workspace。</param>
    /// <param name="modelKey">模型键。</param>
    private async Task ValidateModelAsync(Workspace ws, string modelKey)
    {
        var eff = await config.ResolveAsync(ws.PoolId, ws.Id);
        if (!eff.Any(e => e.Kind == ConfigKind.Model && e.Enabled && e.Key == modelKey)) throw ApiException.BadRequest($"模型「{modelKey}」在当前 Workspace 不可用（未配置或已被上级停用）");
    }

    /// <summary>会话视图。</summary>
    /// <param name="s">会话。</param>
    private object View(Session s) => new
    {
        s.Id, s.WorkspaceId, s.Title, Status = s.Status.ToString(), s.ModelKey, s.MessageCount, s.CreatedBy, s.CreatedAt, s.UpdatedAt, s.LastMessageAt,
        Running = state.Runs.Any(r => r.Value.SessionId == s.Id)
    };

    /// <summary>公开会话视图。</summary>
    /// <param name="s">会话。</param>
    public object ToView(Session s) => View(s);

    /// <summary>截断字符串。</summary>
    /// <param name="s">原文。</param>
    /// <param name="n">最大长度。</param>
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>辅助：计算用户可访问的 Workspace ID 集合（用于跨空间检索）。</summary>
public class WorkspaceListHelper(DshDbContext db, PermissionEngine engine)
{
    /// <summary>列出当前用户可访问的（未归档）Workspace ID。</summary>
    /// <param name="actor">操作者。</param>
    public async Task<List<string>> AccessibleWorkspaceIdsAsync(Actor actor)
    {
        var graph = await engine.GetGraphAsync();
        var activePools = graph.Nodes.Keys.ToList();
        var mine = await db.WorkspaceMembers.AsNoTracking().Where(m => m.UserId == actor.UserId).ToDictionaryAsync(m => m.WorkspaceId);
        var result = new List<string>();
        foreach (var w in await db.Workspaces.AsNoTracking().Where(w => w.Status == WorkspaceStatus.Active && activePools.Contains(w.PoolId)).ToListAsync())
        {
            var wa = await engine.GetWorkspaceAccessAsync(actor, w, mine);
            if (wa.Role != null && !wa.Denied) result.Add(w.Id);
        }
        return result;
    }
}
