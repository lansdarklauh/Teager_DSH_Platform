using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Tests;

/// <summary>权限引擎测试：覆盖产品方案第 8、10、22、51、79 节的层级 Pool / 共享空间 / 显式拒绝规则。</summary>
public class PermissionTests
{
    /// <summary>产品方案的 A/B/C/D/E 场景：集团 Pool C ⊃ Pool A(A,B) / Pool B(C,D)，E 直属 C。</summary>
    private sealed record Scenario(TestEnv Env, Pool C, Pool A, Pool B, Actor UA, Actor UB, Actor UC, Actor UD, Actor UE, SharedSpace C1, SharedSpace A1, SharedSpace B1);

    /// <summary>搭建场景并授权：C1 授权 Pool C（RO，向下继承）；A1 授权 Pool A（RW，不继承）；B1 授权 Pool B（RW，不继承）。</summary>
    private static async Task<Scenario> BuildAsync()
    {
        var env = await TestEnv.CreateAsync();
        var c = await env.PoolAsync("集团"); var a = await env.PoolAsync("编辑部", c); var b = await env.PoolAsync("市场部", c);
        Actor ua = await env.UserAsync("a"), ub = await env.UserAsync("b"), uc = await env.UserAsync("c"), ud = await env.UserAsync("d"), ue = await env.UserAsync("e");
        await env.JoinAsync(a, ua); await env.JoinAsync(a, ub); await env.JoinAsync(b, uc); await env.JoinAsync(b, ud); await env.JoinAsync(c, ue);
        var c1 = await env.SpaceAsync("集团公共资料", c, c, SharePermission.RO, true);
        var a1 = await env.SpaceAsync("编辑内部资料", a, a, SharePermission.RW, false);
        var b1 = await env.SpaceAsync("市场内部资料", b, b, SharePermission.RW, false);
        return new Scenario(env, c, a, b, ua, ub, uc, ud, ue, c1, a1, b1);
    }

    /// <summary>取用户对共享空间的有效权限字符串（无权为 null）。</summary>
    private static async Task<string?> Perm(Scenario s, Actor u, SharedSpace space) =>
        (await s.Env.Engine.GetUserSpaceAccessAsync(u, await s.Env.Db.SharedSpaces.AsNoTracking().FirstAsync(x => x.Id == space.Id))).Permission?.ToString();

    /// <summary>方案第 8 节访问结果表：A/B 看得到 C1、A1；C/D 看得到 C1、B1；E 只看得到 C1。</summary>
    [Fact]
    public async Task Hierarchy_SharedSpace_Matrix_Matches_Spec()
    {
        var sc = await BuildAsync();
        await using var _env = sc.Env;
        // C1（集团公共，RO 向下继承）：所有人 RO
        foreach (var u in new[] { sc.UA, sc.UB, sc.UC, sc.UD, sc.UE }) Assert.Equal("RO", await Perm(sc, u, sc.C1));
        // A1（编辑内部，不继承）：仅 A、B
        Assert.Equal("RO", await Perm(sc, sc.UA, sc.A1)); Assert.Equal("RO", await Perm(sc, sc.UB, sc.A1));
        Assert.Null(await Perm(sc, sc.UC, sc.A1)); Assert.Null(await Perm(sc, sc.UD, sc.A1)); Assert.Null(await Perm(sc, sc.UE, sc.A1));
        // B1（市场内部）：仅 C、D
        Assert.Equal("RO", await Perm(sc, sc.UC, sc.B1)); Assert.Equal("RO", await Perm(sc, sc.UD, sc.B1));
        Assert.Null(await Perm(sc, sc.UA, sc.B1)); Assert.Null(await Perm(sc, sc.UB, sc.B1)); Assert.Null(await Perm(sc, sc.UE, sc.B1));
    }

    /// <summary>有效 Pool = 直属 Pool + 全部祖先，不含兄弟与子 Pool（方案第 10、11 节）。</summary>
    [Fact]
    public async Task EffectivePools_Are_Direct_Plus_Ancestors()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var a = await sc.Env.Engine.GetEffectivePoolIdsAsync(sc.UA.UserId);
        Assert.Equal(new[] { sc.A.Id, sc.C.Id }.OrderBy(x => x), a.OrderBy(x => x));
        var e = await sc.Env.Engine.GetEffectivePoolIdsAsync(sc.UE.UserId);
        Assert.Equal(new[] { sc.C.Id }, e.ToArray()); // E 直属 C：不含 A、B
    }

    /// <summary>inherit_to_children=false：只有 Pool C 自身直接成员可用，子 Pool 成员不可用（方案第 15 节）。</summary>
    [Fact]
    public async Task InheritToChildren_False_Blocks_Descendants()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var only = await sc.Env.SpaceAsync("仅集团直属", sc.C, sc.C, SharePermission.RW, false);
        Assert.Equal("RO", await Perm(sc, sc.UE, only));   // 直属 C
        Assert.Null(await Perm(sc, sc.UA, only));          // 通过子 Pool 间接属于 C，不继承
    }

    /// <summary>显式拒绝优先于一切授权（方案第 22、53 节）：用户级 Deny、Pool 级 Deny 均生效。</summary>
    [Fact]
    public async Task Explicit_Deny_Overrides_Grants()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var svc = sc.Env.Get<SharedSpaceService>();
        await svc.UpsertGrantAsync(sc.Env.Admin, sc.C1.Id, GrantSubjectType.USER, sc.UA.UserId, SharePermission.RO, GrantEffect.Deny, false);
        Assert.Null(await Perm(sc, sc.UA, sc.C1));
        Assert.Equal("RO", await Perm(sc, sc.UB, sc.C1));
        // Pool 级 Deny：拒绝 Pool B（含继承给其成员）
        await svc.UpsertGrantAsync(sc.Env.Admin, sc.C1.Id, GrantSubjectType.POOL, sc.B.Id, SharePermission.RO, GrantEffect.Deny, true);
        Assert.Null(await Perm(sc, sc.UC, sc.C1)); Assert.Null(await Perm(sc, sc.UD, sc.C1));
        Assert.Equal("RO", await Perm(sc, sc.UE, sc.C1));
    }

    /// <summary>用户级显式授权优先于 Pool 授权（可用来把某人降为 RO）。</summary>
    [Fact]
    public async Task User_Grant_Overrides_Pool_Grant()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        await sc.Env.Get<SharedSpaceService>().UpsertGrantAsync(sc.Env.Admin, sc.A1.Id, GrantSubjectType.USER, sc.UA.UserId, SharePermission.RW, GrantEffect.Allow, false);
        await sc.Env.Get<SharedSpaceService>().UpsertGrantAsync(sc.Env.Admin, sc.A1.Id, GrantSubjectType.USER, sc.UB.UserId, SharePermission.RO, GrantEffect.Allow, false);
        Assert.Equal("RO", await Perm(sc, sc.UB, sc.A1));
        Assert.Equal("RW", await Perm(sc, sc.UA, sc.A1));
    }

    /// <summary>Workspace 访问：直属成员、继承（角色封顶 Member）、inherit_pool_members=false、显式拒绝（方案第 21、22 节）。</summary>
    [Fact]
    public async Task Workspace_Access_Rules()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var wsSvc = sc.Env.Get<WorkspaceService>();
        var admin = sc.Env.Admin;
        var ws = await wsSvc.CreateAsync(admin, sc.C.Id, "集团工作台", null);
        Assert.Null((await sc.Env.Engine.GetWorkspaceAccessAsync(sc.UE, ws)).Role);
        Assert.Null((await sc.Env.Engine.GetWorkspaceAccessAsync(sc.UA, ws)).Role);
        await wsSvc.UpsertMemberAsync(admin, ws.Id, sc.UA.UserId, WorkspaceRole.Owner, GrantEffect.Allow);
        Assert.Null((await sc.Env.Engine.GetWorkspaceAccessAsync(sc.UA, ws)).Role); // legacy Owner is insufficient
        await sc.Env.Get<PersonalWorkspaceService>().AssignAsync(admin, sc.UA.UserId, ws.Id);
        var wa = await sc.Env.Engine.GetWorkspaceAccessAsync(sc.UA, ws);
        Assert.Equal(WorkspaceRole.Member, wa.Role); Assert.False(await sc.Env.Engine.CanManageWorkspaceAsync(sc.UA, ws, wa));
        Assert.Null((await sc.Env.Engine.GetWorkspaceAccessAsync(sc.UE, ws)).Role);
        await wsSvc.UpsertMemberAsync(admin, ws.Id, sc.UA.UserId, WorkspaceRole.Member, GrantEffect.Deny);
        var denied = await sc.Env.Engine.GetWorkspaceAccessAsync(sc.UA, ws);
        Assert.True(denied.Denied); Assert.Null(denied.Role);

    }

    /// <summary>挂载：权限 ≠ 挂载；覆盖只能降低不能扩大；未授权的空间不可挂载（方案第 20、58 节）。</summary>
    [Fact]
    public async Task Mount_Rules()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var wsSvc = sc.Env.Get<WorkspaceService>(); var admin = sc.Env.Admin;
        var ws = await wsSvc.CreateAsync(admin, sc.A.Id, "编辑工作台", null);
        await sc.Env.Get<PersonalWorkspaceService>().AssignAsync(admin, sc.UA.UserId, ws.Id);
        // Pool A 未获授权的 B1 不能挂载
        await Assert.ThrowsAsync<ApiException>(() => wsSvc.AddMountAsync(admin, ws.Id, sc.B1.Id, null, null));
        // C1（RO 继承）可挂载，覆盖不能提升为 RW
        await Assert.ThrowsAsync<ApiException>(() => wsSvc.AddMountAsync(admin, ws.Id, sc.C1.Id, null, SharePermission.RW));
        await wsSvc.AddMountAsync(admin, ws.Id, sc.C1.Id, "group", null);
        // A1（RW）挂载后可降为 RO
        await wsSvc.AddMountAsync(admin, ws.Id, sc.A1.Id, "editorial", SharePermission.RO);
        var (mounts, _) = await wsSvc.ListMountsAsync(sc.UA, ws, await sc.Env.Engine.GetWorkspaceAccessAsync(sc.UA, ws));
        var json = System.Text.Json.JsonSerializer.Serialize(mounts);
        Assert.Contains("\"EffectivePermission\":\"RO\"", json);
        // 未挂载 ≠ 有权限：授权仍在，但没有挂载记录时不会出现在 mounts 中，而在 available 中
        var ws2 = await wsSvc.CreateAsync(admin, sc.A.Id, "另一个工作台", null);
        var (m2, avail) = await wsSvc.ListMountsAsync(admin, ws2, await sc.Env.Engine.GetWorkspaceAccessAsync(admin, ws2));
        Assert.Empty(m2); Assert.Equal(2, avail.Count);
    }

    /// <summary>Viewer 角色在 Workspace 内对共享空间最高只有 RO。</summary>
    [Fact]
    public async Task Viewer_Is_Capped_To_RO()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var wsSvc = sc.Env.Get<WorkspaceService>(); var admin = sc.Env.Admin;
        var ws = await wsSvc.CreateAsync(admin, sc.A.Id, "编辑工作台", null);
        await wsSvc.AddMountAsync(admin, ws.Id, sc.A1.Id, "editorial", null);
        var viewer = await sc.Env.UserAsync("viewer");
        await wsSvc.UpsertMemberAsync(admin, ws.Id, viewer.UserId, WorkspaceRole.Viewer, GrantEffect.Allow);
        // viewer 不在 Pool A，因此没有 Pool 授权链路 → 无共享空间权限
        var wa = await sc.Env.Engine.GetWorkspaceAccessAsync(viewer, ws);
        var space = await sc.Env.Db.SharedSpaces.FirstAsync(s => s.Id == sc.A1.Id);
        var mount = await sc.Env.Db.WorkspaceSharedSpaceMounts.FirstAsync(m => m.WorkspaceId == ws.Id);
        // Pool 授权按 Workspace 所属 Pool 计算（RW），但 Viewer 角色封顶 RO
        var acc = await sc.Env.Engine.GetWorkspaceSpaceAccessAsync(viewer, ws, wa, space, mount);
        Assert.Null(acc.Permission);
    }

    /// <summary>权限缓存：变更成员后立即生效（缓存失效，方案第 71 节）。</summary>
    [Fact]
    public async Task Cache_Is_Invalidated_On_Change()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        Assert.Null(await Perm(sc, sc.UE, sc.A1));
        await sc.Env.JoinAsync(sc.A, sc.UE);   // E 加入 Pool A
        Assert.Equal("RO", await Perm(sc, sc.UE, sc.A1));
        await sc.Env.Get<PoolService>().RemoveMemberAsync(sc.Env.Admin, sc.A.Id, sc.UE.UserId);
        Assert.Null(await Perm(sc, sc.UE, sc.A1));
    }

    /// <summary>Pool 管理：祖先管理员可向下管理但不获得子 Pool 私有数据访问权。</summary>
    [Fact]
    public async Task Ancestor_Admin_Manages_But_Cannot_Read_Private_Data()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var boss = await sc.Env.UserAsync("boss");
        await sc.Env.JoinAsync(sc.C, boss, PoolRole.Admin);
        var access = await sc.Env.Engine.GetPoolAccessAsync(boss, sc.A.Id);
        Assert.False(access.CanManage); Assert.Equal("ancestor-member", access.Source);
        // 但没有 A1（编辑内部资料）的数据访问权：不是 A 的成员，也没有授权
        var a1 = await sc.Env.Db.SharedSpaces.FirstAsync(s => s.Id == sc.A1.Id);
        var acc = await sc.Env.Engine.GetUserSpaceAccessAsync(boss, a1);
        Assert.Null(acc.Permission);
        // 兄弟 Pool 管理员不能管理另一个兄弟
        var b = await sc.Env.Engine.GetPoolAccessAsync(sc.UC, sc.A.Id);
        Assert.False(b.CanView);
    }

    /// <summary>Token 的 Workspace 白名单：即使用户是 Pool 管理员，也不能通过管理权看到白名单之外的 Workspace。</summary>
    [Fact]
    public async Task Token_Allowed_Workspaces_Hide_Others_Even_For_Pool_Admin()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var wsSvc = sc.Env.Get<WorkspaceService>();
        var w1 = await wsSvc.CreateAsync(sc.Env.Admin, sc.A.Id, "w1", null); var w2 = await wsSvc.CreateAsync(sc.Env.Admin, sc.A.Id, "w2", null);
        await sc.Env.Get<PersonalWorkspaceService>().AssignAsync(sc.Env.Admin, sc.UA.UserId, w1.Id);
        var token = new Actor { UserId = sc.UA.UserId, Username = "a", Via = "token", Scopes = [Scopes.WorkspaceRead], AllowedWorkspaces = [w1.Id] }; // A 是 Pool A 的 Admin
        var list = await wsSvc.ListAsync(token, null, false);
        Assert.Equal(new[] { w1.Id }, list.Select(v => v.Workspace.Id).ToArray());
        await Assert.ThrowsAsync<ApiException>(() => sc.Env.Engine.RequireWorkspaceAsync(token, w2.Id, null));
    }

    /// <summary>Token 白名单：限定 Pool 后，只能访问该 Pool（及其后代）。</summary>
    [Fact]
    public async Task Token_Allowed_Pools_Restrict_Access()
    {
        var sc = await BuildAsync(); await using var _ = sc.Env;
        var token = new Actor { UserId = sc.Env.Admin.UserId, Username = "admin", IsPlatformAdmin = true, Via = "token", Scopes = [Scopes.PoolRead], AllowedPools = [sc.A.Id] };
        Assert.True((await sc.Env.Engine.GetPoolAccessAsync(token, sc.A.Id)).CanView);
        Assert.False((await sc.Env.Engine.GetPoolAccessAsync(token, sc.B.Id)).CanView);
        Assert.False((await sc.Env.Engine.GetPoolAccessAsync(token, sc.C.Id)).CanView);
        Assert.Throws<ApiException>(() => token.RequireScope(Scopes.PoolManage));
    }
}
