using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Hosting;

/// <summary>后台维护任务：Runtime 空闲回收（每分钟）；过期归档清理、会话 / 审计清理、存储统计刷新、临时文件清理（每小时）。</summary>
public class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger, IOptions<DshOptions> opt, StoragePaths paths) : BackgroundService
{
    private int workspaceCursor, sharedCursor;
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastHourly = DateTime.MinValue;
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RuntimeManager>().SweepAsync();
                var db = scope.ServiceProvider.GetRequiredService<DshDbContext>();
                var active = scope.ServiceProvider.GetRequiredService<RuntimeState>().Runs.Keys.ToList();
                await db.AgentRuns.Where(r => r.Status == RunStatus.Running && r.StartedAt < DateTime.Now.AddMinutes(-2) && !active.Contains(r.Id))
                    .ExecuteUpdateAsync(update => update.SetProperty(r => r.Status, RunStatus.Failed).SetProperty(r => r.FinishedAt, DateTime.Now).SetProperty(r => r.ErrorMessage, "执行器已结束，异常运行记录已回收"));
                if (DateTime.Now - lastHourly > TimeSpan.FromHours(1)) { lastHourly = DateTime.Now; await HourlyAsync(scope.ServiceProvider); }
            }
            catch (Exception ex) { logger.LogError(ex, "维护任务异常"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>每小时执行的清理任务。</summary>
    /// <param name="sp">作用域服务提供者。</param>
    private async Task HourlyAsync(IServiceProvider sp)
    {
        await using var fileLease = await sp.GetRequiredService<DockerSandbox>().AcquireFileLeaseAsync();
        var db = sp.GetRequiredService<DshDbContext>();
        await FileTrash.RecoverAsync(paths, db);
        var purged = await sp.GetRequiredService<WorkspaceService>().PurgeExpiredAsync();
        if (purged > 0) logger.LogInformation("已永久清理 {N} 个过期归档的 Workspace", purged);
        var expiredSessions = await db.PlatformSessions.Where(s => s.ExpiresAt < DateTime.Now).ExecuteDeleteAsync();
        var remaining = await db.Sessions.Select(s => s.Id).ToListAsync();
        sp.GetRequiredService<SessionEventStore>().ForgetDeleted(remaining.ToHashSet());
        var days = opt.Value.Platform.AuditRetentionDays;
        if (days > 0) await db.AuditLogs.Where(a => a.Time < DateTime.Now.AddDays(-days)).ExecuteDeleteAsync();
        // 刷新最近活跃 Workspace / 共享空间的存储统计（每轮限量，避免大目录拖慢）
        var ws = sp.GetRequiredService<WorkspaceService>();
        var workspaceBatch = await db.Workspaces.Where(w => w.Status == WorkspaceStatus.Active).OrderBy(w => w.Id).Skip(workspaceCursor).Take(30).ToListAsync();
        foreach (var w in workspaceBatch) await ws.RefreshStorageAsync(w);
        workspaceCursor = workspaceBatch.Count < 30 ? 0 : workspaceCursor + 30;
        var shared = sp.GetRequiredService<SharedSpaceService>();
        var sharedBatch = await db.SharedSpaces.Where(s => s.Status == SharedSpaceStatus.Active).OrderBy(s => s.Id).Skip(sharedCursor).Take(30).ToListAsync();
        foreach (var s in sharedBatch) await shared.RefreshStorageAsync(s);
        sharedCursor = sharedBatch.Count < 30 ? 0 : sharedCursor + 30;
        // 缓存目录里超过 1 天的临时文件
        foreach (var f in Directory.EnumerateFiles(paths.Temp)) try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f); } catch { }
        logger.LogInformation("每小时维护完成（清理过期登录会话 {S} 条）", expiredSessions);
    }
}
