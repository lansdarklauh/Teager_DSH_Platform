using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Hosting;

/// <summary>
/// 演示数据：按产品方案第 8、79 节的 A/B/C/D/E 场景搭建——
/// 集团(C) ⊃ 编辑部(A: 用户 a、b) / 市场部(B: 用户 c、d)，用户 e 直属集团；并创建 C1 / A1 / B1 三个共享空间。
/// 仅用于体验与验收，默认关闭（Dsh__Seed__DemoData=false）。演示用户密码统一为 Demo@1234。
/// </summary>
public static class DemoSeeder
{
    /// <summary>写入演示数据（幂等：已存在集团 Pool 则跳过）。</summary>
    /// <param name="db">数据库。</param>
    /// <param name="paths">存储路径。</param>
    /// <param name="log">日志。</param>
    public static async Task SeedAsync(DshDbContext db, StoragePaths paths, ILogger log)
    {
        if (await db.Pools.AnyAsync(p => p.Name == "集团")) return;
        var orgId = (await db.Organizations.FirstAsync()).Id;
        var admin = await db.Users.FirstAsync(u => u.Role == PlatformRole.Admin);
        User U(string name, string display) => new() { Id = IdGen.New("usr"), OrganizationId = orgId, Username = name, DisplayName = display, PasswordHash = BCrypt.Net.BCrypt.HashPassword("Demo@1234") };
        var users = new[] { U("a", "用户A"), U("b", "用户B"), U("c", "用户C"), U("d", "用户D"), U("e", "用户E") };
        foreach (var u in users) if (!await db.Users.AnyAsync(x => x.Username == u.Username)) db.Users.Add(u);
        await db.SaveChangesAsync();
        string Uid(string n) => db.Users.Local.FirstOrDefault(x => x.Username == n)?.Id ?? db.Users.First(x => x.Username == n).Id;

        Pool P(string name, string? parent, int depth, string? desc) => new() { Id = IdGen.New("pool"), OrganizationId = orgId, ParentPoolId = parent, Name = name, Description = desc, Depth = depth, RuntimeProvider = "Mock", CreatedBy = admin.Id };
        var c = P("集团", null, 1, "根 Pool：集团公共资源");
        var a = P("编辑部", c.Id, 2, "Pool A：用户 A、B");
        var b = P("市场部", c.Id, 2, "Pool B：用户 C、D");
        db.Pools.AddRange(c, a, b);
        db.PoolClosures.AddRange(
            new PoolClosure { AncestorPoolId = c.Id, DescendantPoolId = c.Id, Depth = 0 }, new PoolClosure { AncestorPoolId = a.Id, DescendantPoolId = a.Id, Depth = 0 },
            new PoolClosure { AncestorPoolId = b.Id, DescendantPoolId = b.Id, Depth = 0 }, new PoolClosure { AncestorPoolId = c.Id, DescendantPoolId = a.Id, Depth = 1 },
            new PoolClosure { AncestorPoolId = c.Id, DescendantPoolId = b.Id, Depth = 1 });
        void M(Pool p, string user, PoolRole r) => db.PoolMembers.Add(new PoolMember { Id = IdGen.New("pm"), PoolId = p.Id, UserId = Uid(user), Role = r });
        M(a, "a", PoolRole.Admin); M(a, "b", PoolRole.Member); M(b, "c", PoolRole.Admin); M(b, "d", PoolRole.Member); M(c, "e", PoolRole.Member);
        db.PoolMembers.Add(new PoolMember { Id = IdGen.New("pm"), PoolId = c.Id, UserId = admin.Id, Role = PoolRole.Owner });

        SharedSpace S(string name, string mount, string? desc, Pool owner) => new() { Id = IdGen.New("ss"), OrganizationId = orgId, OwnerPoolId = owner.Id, Name = name, MountName = mount, Description = desc, CreatedBy = admin.Id };
        var c1 = S("集团公共资料", "group-public", "C1：集团公共资料（向下继承，只读）", c);
        var a1 = S("编辑内部资料", "editorial", "A1：仅编辑部可见", a);
        var b1 = S("市场内部资料", "market", "B1：仅市场部可见", b);
        db.SharedSpaces.AddRange(c1, a1, b1);
        db.SharedSpaceGrants.AddRange(
            new SharedSpaceGrant { Id = IdGen.New("grt"), SharedSpaceId = c1.Id, SubjectId = c.Id, Permission = SharePermission.RO, InheritToChildren = true, CreatedBy = admin.Id },
            new SharedSpaceGrant { Id = IdGen.New("grt"), SharedSpaceId = a1.Id, SubjectId = a.Id, Permission = SharePermission.RW, InheritToChildren = false, CreatedBy = admin.Id },
            new SharedSpaceGrant { Id = IdGen.New("grt"), SharedSpaceId = b1.Id, SubjectId = b.Id, Permission = SharePermission.RW, InheritToChildren = false, CreatedBy = admin.Id });
        foreach (var s in new[] { c1, a1, b1 })
        {
            Directory.CreateDirectory(paths.SharedSpace(s.Id));
            File.WriteAllText(Path.Combine(paths.SharedSpace(s.Id), "README.txt"), $"共享空间：{s.Name}\n{s.Description}\n");
        }
        await db.SaveChangesAsync();
        log.LogInformation("已写入演示数据（集团 / 编辑部 / 市场部；用户 a~e，密码 Demo@1234）");
    }
}
