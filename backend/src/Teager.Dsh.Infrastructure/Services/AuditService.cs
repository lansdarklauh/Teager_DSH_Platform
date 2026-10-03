using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>审计查询条件。</summary>
/// <param name="PoolId">按 Pool 过滤。</param>
/// <param name="IncludeDescendants">是否包含子孙 Pool 的记录。</param>
/// <param name="WorkspaceId">按 Workspace 过滤。</param>
/// <param name="Action">操作前缀，如 sharedspace。</param>
/// <param name="ActorName">操作人登录名（模糊）。</param>
/// <param name="Result">结果：success / denied / failed。</param>
/// <param name="From">起始时间。</param>
/// <param name="To">结束时间。</param>
/// <param name="Keyword">关键字（详情 / 资源 ID）。</param>
/// <param name="Page">页码（从 1 开始）。</param>
/// <param name="PageSize">每页条数。</param>
public record AuditQuery(string? PoolId, bool IncludeDescendants, string? WorkspaceId, string? Action, string? ActorName, string? Result, DateTime? From, DateTime? To, string? Keyword, int Page = 1, int PageSize = 50);

/// <summary>审计服务：记录“谁、何时、从哪里、对什么资源做了什么”。详细内容写日志文件，库内仅存短摘要。</summary>
public class AuditService(IDbContextFactory<DshDbContext> factory, ILogger<AuditService> logger, PermissionEngine engine, DshDbContext db)
{
    /// <summary>写入一条审计（使用独立 DbContext，不受业务事务失败影响）。</summary>
    /// <param name="actor">操作主体，系统任务为 null。</param>
    /// <param name="action">操作标识，如 pool.create。</param>
    /// <param name="resourceType">资源类型。</param>
    /// <param name="resourceId">资源 ID。</param>
    /// <param name="poolId">相关 Pool。</param>
    /// <param name="workspaceId">相关 Workspace。</param>
    /// <param name="detail">简要说明。</param>
    /// <param name="result">结果 success / denied / failed。</param>
    public async Task LogAsync(Actor? actor, string action, string? resourceType = null, string? resourceId = null,
        string? poolId = null, string? workspaceId = null, string? detail = null, string result = "success")
    {
        try
        {
            await using var ctx = await factory.CreateDbContextAsync();
            ctx.AuditLogs.Add(new AuditLog
            {
                Time = DateTime.Now, ActorUserId = actor?.UserId, ActorName = actor?.Username ?? "system", Ip = actor?.Ip,
                Via = actor?.Via ?? "system", Action = action, ResourceType = resourceType, ResourceId = Trunc(resourceId, 300),
                PoolId = poolId, WorkspaceId = workspaceId, Result = result, Detail = Trunc(detail, 1000)
            });
            await ctx.SaveChangesAsync();
            logger.LogInformation("AUDIT {Action} actor={Actor} res={Type}/{Id} pool={Pool} ws={Ws} result={Result} {Detail}",
                action, actor?.Username ?? "system", resourceType, resourceId, poolId, workspaceId, result, detail);
        }
        catch (Exception ex)
        {
            // 审计失败不能影响主流程，但必须留下文件日志
            logger.LogError(ex, "审计写入失败：{Action}", action);
        }
    }

    /// <summary>分页查询审计日志，并按调用者权限收敛范围：管理员看全部，Pool 管理者看所辖 Pool，其他人只看自己。</summary>
    /// <param name="actor">调用者。</param>
    /// <param name="q">查询条件。</param>
    /// <returns>总数与当页数据。</returns>
    public async Task<(int Total, List<AuditLog> Items)> QueryAsync(Actor actor, AuditQuery q)
    {
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        var graph = await engine.GetGraphAsync();

        if (!actor.IsPlatformAdmin)
        {
            var access = await engine.GetAccessiblePoolsAsync(actor);
            var managed = access.Where(kv => kv.Value.CanManage).Select(kv => kv.Key).ToList();
            var self = actor.UserId;
            query = query.Where(a => a.ActorUserId == self || (a.PoolId != null && managed.Contains(a.PoolId)));
        }
        if (!string.IsNullOrWhiteSpace(q.PoolId))
        {
            var ids = new List<string> { q.PoolId };
            if (q.IncludeDescendants) ids.AddRange(graph.DescendantsOf(q.PoolId));
            query = query.Where(a => a.PoolId != null && ids.Contains(a.PoolId));
        }
        if (!string.IsNullOrWhiteSpace(q.WorkspaceId)) query = query.Where(a => a.WorkspaceId == q.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(q.Action)) query = query.Where(a => a.Action.StartsWith(q.Action));
        if (!string.IsNullOrWhiteSpace(q.ActorName)) query = query.Where(a => a.ActorName != null && a.ActorName.Contains(q.ActorName));
        if (!string.IsNullOrWhiteSpace(q.Result)) query = query.Where(a => a.Result == q.Result);
        if (q.From != null) query = query.Where(a => a.Time >= q.From);
        if (q.To != null) query = query.Where(a => a.Time <= q.To);
        if (!string.IsNullOrWhiteSpace(q.Keyword))
            query = query.Where(a => (a.Detail != null && a.Detail.Contains(q.Keyword)) || (a.ResourceId != null && a.ResourceId.Contains(q.Keyword)));

        var total = await query.CountAsync();
        var size = Math.Clamp(q.PageSize, 1, 200);
        var items = await query.OrderByDescending(a => a.Id).Skip((Math.Max(q.Page, 1) - 1) * size).Take(size).ToListAsync();
        return (total, items);
    }

    /// <summary>截断字符串。</summary>
    /// <param name="s">原文。</param>
    /// <param name="max">最大长度。</param>
    private static string? Trunc(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];
}
