using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Tests;

/// <summary>
/// 测试环境：每个测试创建独立的 SQL Server 数据库（走真实迁移）与独立的临时目录，结束后删除。
/// 连接串取环境变量 DSH_TEST_CONN，默认 127.0.0.1:14330（本地测试容器）。
/// </summary>
public sealed class TestEnv : IAsyncDisposable
{
    private readonly string _db = "dsh_test_" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsh_test_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _masterConn;
    /// <summary>根服务提供者。</summary>
    public ServiceProvider Root { get; private set; } = null!;
    /// <summary>作用域。</summary>
    public IServiceScope Scope { get; private set; } = null!;
    /// <summary>数据库上下文。</summary>
    public DshDbContext Db => Scope.ServiceProvider.GetRequiredService<DshDbContext>();
    /// <summary>权限引擎。</summary>
    public PermissionEngine Engine => Scope.ServiceProvider.GetRequiredService<PermissionEngine>();
    /// <summary>缓存版本。</summary>
    public PermissionCache Cache => Root.GetRequiredService<PermissionCache>();
    /// <summary>获取服务。</summary>
    /// <typeparam name="T">服务类型。</typeparam>
    public T Get<T>() where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();
    /// <summary>组织 ID。</summary>
    public string OrgId { get; private set; } = "";
    /// <summary>平台管理员。</summary>
    public Actor Admin { get; private set; } = null!;

    private TestEnv() { _masterConn = Environment.GetEnvironmentVariable("DSH_TEST_CONN") ?? "Server=127.0.0.1,14330;User ID=sa;Password=Dsh_Test_Pass123;TrustServerCertificate=True"; }

    /// <summary>创建并初始化环境（应用迁移、写入组织与管理员）。</summary>
    public static async Task<TestEnv> CreateAsync()
    {
        var env = new TestEnv();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = env._masterConn + ";Database=" + env._db,
            ["Dsh:Storage:DataDir"] = Path.Combine(env._dir, "data"), ["Dsh:Storage:LogDir"] = Path.Combine(env._dir, "logs"), ["Dsh:Storage:CacheDir"] = Path.Combine(env._dir, "cache"),
            ["Dsh:Runtime:DefaultProvider"] = "Mock", ["Dsh:Runtime:TerminalEnabled"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDshInfrastructure(cfg);
        env.Root = services.BuildServiceProvider();
        env.Scope = env.Root.CreateScope();
        await env.Db.Database.MigrateAsync();
        var org = new Organization { Id = IdGen.New("org"), Name = "测试组织" };
        env.OrgId = org.Id;
        env.Db.Organizations.Add(org);
        env.Db.RuntimeNodes.Add(new RuntimeNode { Id = "local", Name = "本机" });
        var admin = new User { Id = IdGen.New("usr"), OrganizationId = org.Id, Username = "admin", DisplayName = "管理员", Role = PlatformRole.Admin };
        env.Db.Users.Add(admin);
        await env.Db.SaveChangesAsync();
        env.Admin = new Actor { UserId = admin.Id, Username = "admin", IsPlatformAdmin = true };
        return env;
    }

    /// <summary>创建普通用户并返回其 Actor。</summary>
    /// <param name="name">登录名。</param>
    public async Task<Actor> UserAsync(string name)
    {
        var u = new User { Id = IdGen.New("usr"), OrganizationId = OrgId, Username = name, DisplayName = name };
        Db.Users.Add(u);
        await Db.SaveChangesAsync();
        return new Actor { UserId = u.Id, Username = name };
    }

    /// <summary>通过服务创建 Pool（管理员身份，创建者成为 Owner）。</summary>
    /// <param name="name">名称。</param>
    /// <param name="parent">父 Pool。</param>
    public async Task<Pool> PoolAsync(string name, Pool? parent = null) => await Get<PoolService>().CreateAsync(Admin, name, null, parent?.Id, "Mock");

    /// <summary>把用户加入 Pool。</summary>
    /// <param name="pool">Pool。</param>
    /// <param name="user">用户。</param>
    /// <param name="role">角色。</param>
    public async Task JoinAsync(Pool pool, Actor user, PoolRole role = PoolRole.Member) => await Get<PoolService>().UpsertMemberAsync(Admin, pool.Id, user.UserId, role);

    /// <summary>创建共享空间并授权给 Pool。</summary>
    /// <param name="name">名称。</param>
    /// <param name="owner">所属 Pool。</param>
    /// <param name="grantTo">授权对象 Pool。</param>
    /// <param name="perm">权限。</param>
    /// <param name="inherit">是否向下继承。</param>
    public async Task<SharedSpace> SpaceAsync(string name, Pool owner, Pool grantTo, SharePermission perm, bool inherit)
    {
        var svc = Get<SharedSpaceService>();
        var s = await svc.CreateAsync(Admin, name, null, owner.Id, 0, null);
        // 创建时所属 Pool 已有默认 RW 授权，先移除，再按用例授权
        foreach (var g in await Db.SharedSpaceGrants.Where(g => g.SharedSpaceId == s.Id).ToListAsync()) await svc.DeleteGrantAsync(Admin, s.Id, g.Id);
        await svc.UpsertGrantAsync(Admin, s.Id, GrantSubjectType.POOL, grantTo.Id, perm, GrantEffect.Allow, inherit);
        return s;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Scope.Dispose();
        try { await using var ctx = new DshDbContext(new DbContextOptionsBuilder<DshDbContext>().UseSqlServer(_masterConn + ";Database=master").Options); await ctx.Database.ExecuteSqlRawAsync($"ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]"); } catch { }
        await Root.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
