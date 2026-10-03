using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>用量统计服务：按 Pool / Workspace / 个人汇总运行次数、token、工具调用与存储占用。</summary>
public class UsageService(DshDbContext db, PermissionEngine engine, PolicyService policy)
{
    /// <summary>生成用量汇总。范围：workspaceId → Workspace；poolId → Pool（含子孙）；均为空 → 管理员看全平台，其他人只看自己。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="days">统计最近天数（1-365）。</param>
    /// <returns>汇总、按日趋势、分模型 / 用户 / Workspace 排行。</returns>
    public async Task<object> SummaryAsync(Actor actor, string? poolId, string? workspaceId, int days)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.Today.AddDays(-(days - 1));
        var q = db.UsageLogs.AsNoTracking().Where(u => u.Time >= since);
        var canBreakdownUsers = actor.IsPlatformAdmin; string scopeName = "平台"; object? quota = null;

        if (workspaceId != null)
        {
            var (ws, wa) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Viewer);
            q = q.Where(u => u.WorkspaceId == ws.Id); scopeName = "Workspace：" + ws.Name;
            canBreakdownUsers = await engine.CanManageWorkspaceAsync(actor, ws, wa);
            var eff = await policy.GetEffectiveAsync(ws.PoolId);
            quota = new { StorageBytes = ws.StorageBytes, StorageQuotaMb = eff.MaxStorageMbPerWorkspace };
        }
        else if (poolId != null)
        {
            var access = await engine.RequirePoolViewAsync(actor, poolId);
            var graph = await engine.GetGraphAsync();
            var ids = graph.DescendantsOf(poolId).Append(poolId).ToList();
            // 普通成员只能看到自己在该 Pool 的用量，管理者看全部
            q = q.Where(u => ids.Contains(u.PoolId));
            if (!access.CanManage) q = q.Where(u => u.UserId == actor.UserId);
            canBreakdownUsers = access.CanManage; scopeName = "Pool：" + graph.NameOf(poolId) + "（含子 Pool）";
            var eff = await policy.GetEffectiveAsync(poolId);
            var wsStorage = await db.Workspaces.AsNoTracking().Where(w => ids.Contains(w.PoolId) && w.Status == WorkspaceStatus.Active).SumAsync(w => (long?)w.StorageBytes) ?? 0;
            var todayRuns = await db.AgentRuns.CountAsync(r => r.PoolId == poolId && r.StartedAt >= DateTime.Today);
            var todayTokens = await db.UsageLogs.Where(u => u.PoolId == poolId && u.Time >= DateTime.Today).SumAsync(u => (long?)(u.InputTokens + u.OutputTokens)) ?? 0;
            quota = new { WorkspaceStorageBytes = wsStorage, MaxMessagesPerDay = eff.MaxMessagesPerDay, TodayMessages = todayRuns, MaxTokensPerDay = eff.MaxTokensPerDay, TodayTokens = todayTokens };
        }
        else if (!actor.IsPlatformAdmin) { q = q.Where(u => u.UserId == actor.UserId); scopeName = "我的用量"; }

        var rows = await q.Select(u => new { u.Time, u.UserId, u.PoolId, u.WorkspaceId, u.Model, u.InputTokens, u.OutputTokens, u.ToolCalls, u.DurationMs, u.Success }).ToListAsync();
        var daily = Enumerable.Range(0, days).Select(i => since.AddDays(i)).Select(d =>
        {
            var g = rows.Where(r => r.Time.Date == d).ToList();
            return new { Date = d.ToString("yyyy-MM-dd"), Runs = g.Count, InputTokens = g.Sum(x => x.InputTokens), OutputTokens = g.Sum(x => x.OutputTokens), ToolCalls = g.Sum(x => x.ToolCalls) };
        }).ToList();
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var wsNames = await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name);
        var pools = await engine.GetGraphAsync();
        return new
        {
            Scope = scopeName, Days = days, Quota = quota,
            Totals = new { Runs = rows.Count, Failed = rows.Count(r => !r.Success), InputTokens = rows.Sum(r => r.InputTokens), OutputTokens = rows.Sum(r => r.OutputTokens), ToolCalls = rows.Sum(r => r.ToolCalls), ActiveUsers = rows.Select(r => r.UserId).Distinct().Count(), AvgDurationMs = rows.Count == 0 ? 0 : (long)rows.Average(r => r.DurationMs) },
            Daily = daily,
            ByModel = rows.GroupBy(r => r.Model ?? "默认").Select(g => new { Name = g.Key, Runs = g.Count(), Tokens = g.Sum(x => x.InputTokens + x.OutputTokens) }).OrderByDescending(x => x.Tokens).Take(10).ToList(),
            ByUser = canBreakdownUsers ? rows.GroupBy(r => r.UserId).Select(g => new { Name = users.GetValueOrDefault(g.Key, g.Key), Runs = g.Count(), Tokens = g.Sum(x => x.InputTokens + x.OutputTokens) }).OrderByDescending(x => x.Tokens).Take(10).ToList() : [],
            ByWorkspace = rows.GroupBy(r => r.WorkspaceId).Select(g => new { Name = wsNames.GetValueOrDefault(g.Key, g.Key), Runs = g.Count(), Tokens = g.Sum(x => x.InputTokens + x.OutputTokens) }).OrderByDescending(x => x.Tokens).Take(10).ToList(),
            ByPool = actor.IsPlatformAdmin && poolId == null && workspaceId == null ? rows.GroupBy(r => r.PoolId).Select(g => new { Name = pools.NameOf(g.Key), Runs = g.Count(), Tokens = g.Sum(x => x.InputTokens + x.OutputTokens) }).OrderByDescending(x => x.Tokens).Take(10).ToList() : []
        };
    }
}
