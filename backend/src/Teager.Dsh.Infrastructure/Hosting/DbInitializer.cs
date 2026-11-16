using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Hosting;

/// <summary>启动初始化：数据库迁移、默认组织 / 管理员 / 本机运行节点、异常中断的运行收尾、（可选）演示数据。</summary>
public static class DbInitializer
{
    /// <summary>执行初始化。</summary>
    /// <param name="sp">根服务提供者。</param>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DshDbContext>();
        var opt = scope.ServiceProvider.GetRequiredService<IOptions<DshOptions>>().Value;
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        if (opt.Platform.AutoMigrate) await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<PlatformLease>().AcquireAsync(db);
        if (await db.Organizations.CountAsync() > 1) throw new InvalidOperationException("当前部署只支持单组织，不能混用多个组织的数据");
        if (!await db.Organizations.AnyAsync())
            db.Organizations.Add(new Organization { Id = IdGen.New("org"), Name = opt.Seed.OrganizationName });
        if (!await db.RuntimeNodes.AnyAsync())
            db.RuntimeNodes.Add(new RuntimeNode { Id = "local", Name = "本机节点", Host = Environment.MachineName });
        await db.SaveChangesAsync();

        // 首个管理员：仅在库里没有任何管理员且显式配置了密码时创建（生产环境不带默认口令）
        if (!await db.Users.AnyAsync(u => u.Role == PlatformRole.Admin))
        {
            if (string.IsNullOrEmpty(opt.Seed.AdminPassword)) log.LogWarning("数据库中没有平台管理员，且未配置 Dsh__Seed__AdminPassword，无法登录。请配置后重启。");
            else
            {
                Services.AuthService.ValidatePassword(opt.Seed.AdminPassword);
                db.Users.Add(new User { Id = IdGen.New("usr"), OrganizationId = (await db.Organizations.FirstAsync()).Id, Username = opt.Seed.AdminUsername, DisplayName = opt.Seed.AdminDisplayName, Role = PlatformRole.Admin, PasswordHash = BCrypt.Net.BCrypt.HashPassword(opt.Seed.AdminPassword) });
                await db.SaveChangesAsync();
                log.LogInformation("已创建初始管理员 {User}", opt.Seed.AdminUsername);
            }
        }

        // 进程异常退出时遗留的“运行中”记录统一标记失败，Runtime 状态复位
        var stale = await db.AgentRuns.Where(r => r.Status == RunStatus.Running).ToListAsync();
        foreach (var r in stale) { r.Status = RunStatus.Failed; r.ErrorMessage = "平台重启，运行被中断"; r.FinishedAt = DateTime.Now; }
        foreach (var rt in await db.Runtimes.Where(r => r.Status != RuntimeStatus.STOPPED && r.Status != RuntimeStatus.MAINTENANCE).ToListAsync()) rt.Status = RuntimeStatus.STOPPED;
        await db.SaveChangesAsync();
        if (stale.Count > 0) log.LogWarning("已将 {N} 条中断的运行标记为失败", stale.Count);
        await FileTrash.RecoverAsync(scope.ServiceProvider.GetRequiredService<StoragePaths>(), db);

        if (opt.Seed.DemoData) await DemoSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<StoragePaths>(), log);
        var credentials = await scope.ServiceProvider.GetRequiredService<Services.ModelCredentialMigration>().NormalizeAsync();
        if (credentials > 0) log.LogInformation("已将 {N} 个旧模型配置中的上游密钥迁移至同层级加密存储", credentials);
        var personal = scope.ServiceProvider.GetRequiredService<Services.PersonalWorkspaceService>();
        foreach (var id in await db.Users.Where(u => u.Role == PlatformRole.User && !u.IsServiceAccount).Select(u => u.Id).ToListAsync())
            await personal.EnsureAsync(id);
        var synchronized = await personal.SynchronizePoolProviderAsync();
        if (synchronized > 0) log.LogInformation("已将 {N} 个系统个人 Pool 同步为默认引擎 {Provider}；保留模型、密钥和历史消息", synchronized, opt.Runtime.DefaultProvider);
    }
}
