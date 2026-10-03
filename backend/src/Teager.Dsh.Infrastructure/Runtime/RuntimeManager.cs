using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>Runtime 进程内运行状态（单例）：每个 Pool 的并发计数、启动锁与运行取消令牌。</summary>
public class RuntimeState
{
    /// <summary>每个 Pool 的启动 / 停止互斥锁。</summary>
    public ConcurrentDictionary<string, SemaphoreSlim> Locks { get; } = new();
    /// <summary>运行中的任务取消令牌：runId → (poolId, sessionId, CTS)。</summary>
    public ConcurrentDictionary<string, (string PoolId, string SessionId, CancellationTokenSource Cts)> Runs { get; } = new();

    /// <summary>获取 Pool 的互斥锁。</summary>
    /// <param name="poolId">Pool ID。</param>
    public SemaphoreSlim LockFor(string poolId) => Locks.GetOrAdd(poolId, _ => new SemaphoreSlim(1, 1));
    /// <summary>某 Pool 当前运行中的任务数。</summary>
    /// <param name="poolId">Pool ID。</param>
    public int ActiveCount(string poolId) => Runs.Count(r => r.Value.PoolId == poolId);
}

/// <summary>
/// Runtime 管理器：负责每个 Pool 的 Runtime 生命周期（STOPPED → STARTING → RUNNING / IDLE → STOPPING）。
/// 用户访问 Workspace 时若 Runtime 未运行则自动启动。Runtime 生命周期与数据生命周期相互独立。
/// </summary>
public class RuntimeManager(DshDbContext db, IEnumerable<IRuntimeProvider> providers, RuntimeState state, StoragePaths paths, IOptions<DshOptions> opt)
{
    /// <summary>按名称获取 Provider。</summary>
    /// <param name="name">Provider 名称。</param>
    public IRuntimeProvider Provider(string name) =>
        providers.FirstOrDefault(p => p.Name == name) ?? throw ApiException.BadRequest($"未注册的 Runtime Provider：{name}");

    /// <summary>获取（不存在则创建）Pool 对应的 Runtime 记录。</summary>
    /// <param name="pool">Pool。</param>
    public async Task<RuntimeInstance> GetOrCreateAsync(Pool pool)
    {
        var rt = await db.Runtimes.FirstOrDefaultAsync(r => r.PoolId == pool.Id);
        if (rt != null) return rt;
        var node = await db.RuntimeNodes.OrderBy(n => n.CreatedAt).Select(n => n.Id).FirstOrDefaultAsync() ?? "local";
        rt = new RuntimeInstance { Id = IdGen.New("rt"), PoolId = pool.Id, Provider = pool.RuntimeProvider, NodeId = node, InternalEndpoint = $"process://{node}/{pool.Id}" };
        db.Runtimes.Add(rt);
        await db.SaveChangesAsync();
        return rt;
    }

    /// <summary>确保 Runtime 已启动（未运行则启动）；处于维护模式时拒绝。</summary>
    /// <param name="pool">Pool。</param>
    /// <returns>Runtime 记录。</returns>
    public async Task<RuntimeInstance> EnsureStartedAsync(Pool pool)
    {
        var rt = await GetOrCreateAsync(pool);
        if (rt.Status == RuntimeStatus.MAINTENANCE) throw ApiException.Conflict("该 Pool 的 Runtime 正在维护中，暂不接受新任务");
        if (rt.Provider != pool.RuntimeProvider) { rt.Provider = pool.RuntimeProvider; }
        if (rt.Status is RuntimeStatus.RUNNING or RuntimeStatus.IDLE) return rt;
        return await StartAsync(pool, rt);
    }

    /// <summary>启动 Runtime（幂等，按 Pool 加锁）。</summary>
    /// <param name="pool">Pool。</param>
    /// <param name="rt">Runtime 记录（可空则查询）。</param>
    public async Task<RuntimeInstance> StartAsync(Pool pool, RuntimeInstance? rt = null)
    {
        rt ??= await GetOrCreateAsync(pool);
        var gate = state.LockFor(pool.Id);
        await gate.WaitAsync();
        try
        {
            await db.Entry(rt).ReloadAsync();
            if (rt.Status is RuntimeStatus.RUNNING or RuntimeStatus.IDLE) return rt;
            rt.Status = RuntimeStatus.STARTING; rt.Provider = pool.RuntimeProvider; await db.SaveChangesAsync();
            var health = await Provider(rt.Provider).StartRuntimeAsync(new RuntimeSpec(pool.Id, paths.PoolDshHome(pool.Id)), CancellationToken.None);
            rt.Version = health.Version; rt.LastError = health.Healthy ? null : health.Message;
            rt.Status = health.Healthy ? (state.ActiveCount(pool.Id) > 0 ? RuntimeStatus.RUNNING : RuntimeStatus.IDLE) : RuntimeStatus.ERROR;
            if (health.Healthy) { rt.StartedAt = DateTime.Now; rt.LastActiveAt = DateTime.Now; }
            await db.SaveChangesAsync();
            if (!health.Healthy) throw ApiException.Conflict("Runtime 启动失败：" + health.Message);
            return rt;
        }
        finally { gate.Release(); }
    }

    /// <summary>停止 Runtime：终止该 Pool 下所有运行中的任务（记录状态为 STOPPED，数据不受影响）。</summary>
    /// <param name="pool">Pool。</param>
    public async Task<RuntimeInstance> StopAsync(Pool pool)
    {
        var rt = await GetOrCreateAsync(pool);
        var gate = state.LockFor(pool.Id);
        await gate.WaitAsync();
        try
        {
            rt.Status = RuntimeStatus.STOPPING; await db.SaveChangesAsync();
            foreach (var r in state.Runs.Where(r => r.Value.PoolId == pool.Id).ToList()) r.Value.Cts.Cancel();
            await Provider(rt.Provider).StopRuntimeAsync(pool.Id);
            rt.Status = RuntimeStatus.STOPPED; rt.StartedAt = null; await db.SaveChangesAsync();
            return rt;
        }
        finally { gate.Release(); }
    }

    /// <summary>重启 Runtime。</summary>
    /// <param name="pool">Pool。</param>
    public async Task<RuntimeInstance> RestartAsync(Pool pool)
    {
        await StopAsync(pool);
        return await StartAsync(pool);
    }

    /// <summary>设置 / 取消维护模式（维护中拒绝新任务，已有任务不受影响）。</summary>
    /// <param name="pool">Pool。</param>
    /// <param name="enabled">是否进入维护。</param>
    public async Task<RuntimeInstance> SetMaintenanceAsync(Pool pool, bool enabled)
    {
        var rt = await GetOrCreateAsync(pool);
        if (enabled) rt.Status = RuntimeStatus.MAINTENANCE;
        else if (rt.Status == RuntimeStatus.MAINTENANCE) rt.Status = RuntimeStatus.STOPPED;
        await db.SaveChangesAsync();
        return rt;
    }

    /// <summary>健康检查：调用 Provider 并刷新记录。</summary>
    /// <param name="pool">Pool。</param>
    public async Task<(RuntimeInstance Runtime, RuntimeHealth Health)> HealthAsync(Pool pool)
    {
        var rt = await GetOrCreateAsync(pool);
        var health = await Provider(rt.Provider).HealthCheckAsync(new RuntimeSpec(pool.Id, paths.PoolDshHome(pool.Id)), CancellationToken.None);
        rt.Version = health.Version ?? rt.Version;
        if (!health.Healthy) rt.LastError = health.Message;
        await db.SaveChangesAsync();
        return (rt, health with { ActiveRuns = state.ActiveCount(pool.Id) });
    }

    /// <summary>运行开始时标记 RUNNING。</summary>
    /// <param name="poolId">Pool ID。</param>
    public async Task MarkRunStartedAsync(string poolId)
    {
        var rt = await db.Runtimes.FirstOrDefaultAsync(r => r.PoolId == poolId);
        if (rt == null || rt.Status == RuntimeStatus.MAINTENANCE) return;
        rt.Status = RuntimeStatus.RUNNING; rt.LastActiveAt = DateTime.Now;
        await db.SaveChangesAsync();
    }

    /// <summary>运行结束后刷新活跃时间；无其他任务则转为 IDLE。</summary>
    /// <param name="poolId">Pool ID。</param>
    public async Task MarkRunFinishedAsync(string poolId)
    {
        var rt = await db.Runtimes.FirstOrDefaultAsync(r => r.PoolId == poolId);
        if (rt == null) return;
        rt.LastActiveAt = DateTime.Now;
        if (state.ActiveCount(poolId) == 0 && rt.Status == RuntimeStatus.RUNNING) rt.Status = RuntimeStatus.IDLE;
        await db.SaveChangesAsync();
    }

    /// <summary>周期维护：RUNNING 无任务且空闲超时 → IDLE；IDLE 超过自动停止时长 → STOPPED。</summary>
    public async Task SweepAsync()
    {
        var now = DateTime.Now;
        foreach (var rt in await db.Runtimes.Where(r => r.Status == RuntimeStatus.RUNNING || r.Status == RuntimeStatus.IDLE).ToListAsync())
        {
            if (state.ActiveCount(rt.PoolId) > 0) continue;
            var idle = now - (rt.LastActiveAt ?? rt.StartedAt ?? now);
            if (rt.Status == RuntimeStatus.RUNNING && idle > TimeSpan.FromMinutes(opt.Value.Runtime.IdleMinutes)) rt.Status = RuntimeStatus.IDLE;
            if (rt.Status == RuntimeStatus.IDLE && idle > TimeSpan.FromMinutes(opt.Value.Runtime.AutoStopMinutes))
            {
                try { await Provider(rt.Provider).StopRuntimeAsync(rt.PoolId); } catch { /* 忽略 */ }
                rt.Status = RuntimeStatus.STOPPED; rt.StartedAt = null;
            }
        }
        await db.SaveChangesAsync();
    }
}
