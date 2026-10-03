using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Hosting;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure;

/// <summary>基础设施层依赖注入注册。</summary>
public static class DependencyInjection
{
    /// <summary>注册数据库、权限引擎、业务服务、Runtime Provider 与后台任务。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="config">配置。</param>
    /// <returns>服务集合。</returns>
    public static IServiceCollection AddDshInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DshOptions>(config.GetSection(DshOptions.SectionName));
        var conn = config.GetConnectionString("Default") ?? throw new InvalidOperationException("缺少连接字符串 ConnectionStrings:Default");
        // 工厂 + 作用域上下文：审计等需要独立上下文的场景使用工厂，其余业务使用请求级上下文
        services.AddDbContextFactory<DshDbContext>(o => o.UseSqlServer(conn));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<DshDbContext>>().CreateDbContext());
        services.AddMemoryCache();

        services.AddSingleton<PermissionCache>().AddSingleton<StoragePaths>().AddSingleton<RuntimeState>().AddSingleton<SessionEventStore>().AddSingleton<RunCoordinator>().AddSingleton<FileVersionStore>();
        services.AddSingleton<IRuntimeProvider, DeepSeekHarnessProvider>().AddSingleton<IRuntimeProvider, MockProvider>();

        services.AddScoped<ActorAccessor>().AddScoped<PermissionEngine>().AddScoped<AuditService>().AddScoped<PolicyService>();
        services.AddScoped<PoolService>().AddScoped<WorkspaceService>().AddScoped<SharedSpaceService>().AddScoped<FileAccessService>();
        services.AddScoped<ConfigAccess>().AddScoped<ConfigService>().AddScoped<SecretService>();
        services.AddScoped<RuntimeManager>().AddScoped<RuntimeMaterializer>().AddScoped<SessionService>().AddScoped<WorkspaceListHelper>();
        services.AddScoped<AuthService>().AddScoped<UserService>().AddScoped<TokenService>().AddScoped<UsageService>();
        services.AddHostedService<MaintenanceService>();
        return services;
    }
}
