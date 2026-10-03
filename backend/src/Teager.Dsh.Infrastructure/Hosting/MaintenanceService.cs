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
        var db = sp.GetRequiredService<DshDbContext>();
        var purged = await sp.GetRequiredService<WorkspaceService>().PurgeExpiredAsync();
        if (purged > 0) logger.LogInformation("已永久清理 {N} 个过期归档的 Workspace", purged);
        var expiredSessions = await db.PlatformSessions.Where(s => s.ExpiresAt < DateTime.Now).ExecuteDeleteAsync();
        var days = opt.Value.Platform.AuditRetentionDays;
        if (days > 0) await db.AuditLogs.Where(a => a.Time < DateTime.Now.AddDays(-days)).ExecuteDeleteAsync();
        // 刷新最近活跃 Workspace / 共享空间的存储统计（每轮限量，避免大目录拖慢）
        var ws = sp.GetRequiredService<WorkspaceService>();
        foreach (var w in await db.Workspaces.Where(w => w.Status == WorkspaceStatus.Active).OrderByDescending(w => w.LastActiveAt).Take(30).ToListAsync()) await ws.RefreshStorageAsync(w);
        var shared = sp.GetRequiredService<SharedSpaceService>();
        foreach (var s in await db.SharedSpaces.Where(s => s.Status == SharedSpaceStatus.Active).OrderBy(s => s.Id).Take(30).ToListAsync()) await shared.RefreshStorageAsync(s);
        // 缓存目录里超过 1 天的临时文件
        foreach (var f in Directory.EnumerateFiles(paths.Temp)) try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f); } catch { }
        logger.LogInformation("每小时维护完成（清理过期登录会话 {S} 条）", expiredSessions);
    }
}
