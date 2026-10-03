using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>Pool 树节点视图。</summary>
/// <param name="Id">Pool ID。</param>
/// <param name="ParentPoolId">父 Pool。</param>
/// <param name="Name">名称。</param>
/// <param name="Description">描述。</param>
/// <param name="Depth">层级。</param>
/// <param name="DirectRole">我的直属角色。</param>
/// <param name="CanManage">我是否可管理。</param>
/// <param name="Access">访问来源。</param>
/// <param name="WorkspaceCount">Workspace 数。</param>
/// <param name="MemberCount">直属成员数。</param>
/// <param name="ChildCount">子 Pool 数。</param>
public record PoolNodeView(string Id, string? ParentPoolId, string Name, string? Description, int Depth, string? DirectRole, bool CanManage, string Access, int WorkspaceCount, int MemberCount, int ChildCount);

/// <summary>Pool 移动影响预览。</summary>
/// <param name="LostAncestors">将失去的父级 Pool。</param>
/// <param name="GainedAncestors">将新增的父级 Pool。</param>
/// <param name="LostSharedSpaces">将失去的继承共享空间。</param>
/// <param name="GainedSharedSpaces">将新增的继承共享空间。</param>
/// <param name="AffectedUsers">受影响用户数（子树内直属成员）。</param>
/// <param name="AffectedWorkspaces">受影响 Workspace 数。</param>
/// <param name="AffectedPools">受影响 Pool 数（含自身与后代）。</param>
/// <param name="NewDepth">移动后该 Pool 的层级。</param>
public record MovePreview(List<string> LostAncestors, List<string> GainedAncestors, List<string> LostSharedSpaces, List<string> GainedSharedSpaces, int AffectedUsers, int AffectedWorkspaces, int AffectedPools, int NewDepth);

/// <summary>Pool 服务：树结构、闭包表维护、成员、移动、删除（归档）。</summary>
public class PoolService(DshDbContext db, PermissionEngine engine, PermissionCache pc, IOptions<DshOptions> opt, WorkspaceService workspaces)
{
    /// <summary>创建 Pool（根 Pool 仅平台管理员可建；子 Pool 需父 Pool 管理权），并维护闭包表，创建者成为 Owner。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="name">名称。</param>
    /// <param name="description">描述。</param>
    /// <param name="parentId">父 Pool ID，null 为根。</param>
    /// <param name="runtimeProvider">Runtime Provider，空则用默认。</param>
    /// <returns>新 Pool。</returns>
    public async Task<Pool> CreateAsync(Actor actor, string name, string? description, string? parentId, string? runtimeProvider)
    {
        name = RequireName(name);
        int depth = 1; string? inheritedProvider = null;
        if (parentId != null)
        {
            await engine.RequirePoolManageAsync(actor, parentId);
            var parent = await db.Pools.FirstOrDefaultAsync(p => p.Id == parentId && p.Status == PoolStatus.Active) ?? throw ApiException.NotFound("父 Pool 不存在");
            depth = parent.Depth + 1;
            inheritedProvider = parent.RuntimeProvider;
        }
        else if (!actor.IsPlatformAdmin) throw ApiException.Forbidden("只有平台管理员可以创建顶级 Pool，请在某个 Pool 下创建子 Pool");
        if (depth > opt.Value.Platform.MaxPoolDepth) throw ApiException.BadRequest($"Pool 层级不能超过 {opt.Value.Platform.MaxPoolDepth} 层");
        if (await db.Pools.AnyAsync(p => p.ParentPoolId == parentId && p.Name == name && p.Status == PoolStatus.Active)) throw ApiException.Conflict("同一父级下已存在同名 Pool");

        var provider = ValidateProvider(runtimeProvider ?? inheritedProvider ?? opt.Value.Runtime.DefaultProvider);
        var orgId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var pool = new Pool { Id = IdGen.New("pool"), OrganizationId = orgId, ParentPoolId = parentId, Name = name, Description = description, Depth = depth, RuntimeProvider = provider, CreatedBy = actor.UserId };
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Pools.Add(pool);
        db.PoolClosures.Add(new PoolClosure { AncestorPoolId = pool.Id, DescendantPoolId = pool.Id, Depth = 0 });
        if (parentId != null)
            foreach (var c in await db.PoolClosures.Where(c => c.DescendantPoolId == parentId).ToListAsync())
                db.PoolClosures.Add(new PoolClosure { AncestorPoolId = c.AncestorPoolId, DescendantPoolId = pool.Id, Depth = c.Depth + 1 });
        db.PoolMembers.Add(new PoolMember { Id = IdGen.New("pm"), PoolId = pool.Id, UserId = actor.UserId, Role = PoolRole.Owner });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        pc.Invalidate();
        return pool;
    }

    /// <summary>更新 Pool 基本信息。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">Pool ID。</param>
    /// <param name="name">新名称。</param>
    /// <param name="description">新描述。</param>
    /// <param name="runtimeProvider">新 Provider（仅平台管理员可改）。</param>
    /// <param name="inheritConfig">是否继承父级配置。</param>
    /// <returns>更新后的 Pool。</returns>
    public async Task<Pool> UpdateAsync(Actor actor, string id, string? name, string? description, string? runtimeProvider, bool? inheritConfig)
    {
        await engine.RequirePoolManageAsync(actor, id);
        var pool = await GetActiveAsync(id);
        if (name != null)
        {
            name = RequireName(name);
            if (await db.Pools.AnyAsync(p => p.Id != id && p.ParentPoolId == pool.ParentPoolId && p.Name == name && p.Status == PoolStatus.Active)) throw ApiException.Conflict("同一父级下已存在同名 Pool");
            pool.Name = name;
        }
        if (description != null) pool.Description = description;
        if (inheritConfig != null) pool.InheritConfig = inheritConfig.Value;
        if (runtimeProvider != null && runtimeProvider != pool.RuntimeProvider)
        {
            if (!actor.IsPlatformAdmin) throw ApiException.Forbidden("只有平台管理员可以更换 Runtime Provider");
            pool.RuntimeProvider = ValidateProvider(runtimeProvider);
        }
        pool.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        pc.Invalidate();
        return pool;
    }

    /// <summary>获取用户可见的 Pool 树（含祖先路径、子树、计数）。</summary>
    /// <param name="actor">操作者。</param>
    /// <returns>扁平节点列表，前端按 ParentPoolId 组树。</returns>
    public async Task<List<PoolNodeView>> GetTreeAsync(Actor actor)
    {
        var access = await engine.GetAccessiblePoolsAsync(actor);
        var ids = access.Keys.ToList();
        var pools = await db.Pools.AsNoTracking().Where(p => ids.Contains(p.Id) && p.Status == PoolStatus.Active).ToListAsync();
        var wsCounts = await db.Workspaces.AsNoTracking().Where(w => w.Status == WorkspaceStatus.Active && ids.Contains(w.PoolId)).GroupBy(w => w.PoolId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var memberCounts = await db.PoolMembers.AsNoTracking().Where(m => ids.Contains(m.PoolId)).GroupBy(m => m.PoolId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var childCounts = pools.Where(p => p.ParentPoolId != null).GroupBy(p => p.ParentPoolId!).ToDictionary(g => g.Key, g => g.Count());
        return pools.OrderBy(p => p.Depth).ThenBy(p => p.Name).Select(p =>
        {
            var a = access[p.Id];
            return new PoolNodeView(p.Id, p.ParentPoolId != null && access.ContainsKey(p.ParentPoolId) ? p.ParentPoolId : null, p.Name, p.Description, p.Depth,
                a.DirectRole?.ToString(), a.CanManage, a.Source, wsCounts.GetValueOrDefault(p.Id), memberCounts.GetValueOrDefault(p.Id), childCounts.GetValueOrDefault(p.Id));
        }).ToList();
    }

    /// <summary>获取有效 Pool（不存在 / 已归档抛 404）。</summary>
    /// <param name="id">Pool ID。</param>
    public async Task<Pool> GetActiveAsync(string id) =>
        await db.Pools.FirstOrDefaultAsync(p => p.Id == id && p.Status == PoolStatus.Active) ?? throw ApiException.NotFound("Pool 不存在");

    // ─────────────────────────── 成员 ───────────────────────────

    /// <summary>列出成员：直接成员 + 继承成员（来自子孙 Pool 成员的“有效成员”）。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <returns>直接成员与继承成员。</returns>
    public async Task<(List<object> Direct, List<object> Inherited)> ListMembersAsync(string poolId)
    {
        var graph = await engine.GetGraphAsync();
        var direct = await (from m in db.PoolMembers.AsNoTracking() where m.PoolId == poolId
                            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                            orderby m.Role descending, u.Username
                            select new { m.Id, m.UserId, u.Username, u.DisplayName, Role = m.Role.ToString(), m.CreatedAt }).ToListAsync();
        var descIds = graph.DescendantsOf(poolId).ToList();
        var directUserIds = direct.Select(d => d.UserId).ToHashSet();
        var inh = await (from m in db.PoolMembers.AsNoTracking() where descIds.Contains(m.PoolId)
                         join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                         select new { m.UserId, u.Username, u.DisplayName, m.PoolId, Role = m.Role.ToString() }).ToListAsync();
        var inherited = inh.Where(x => !directUserIds.Contains(x.UserId))
            .GroupBy(x => x.UserId).Select(g => g.First())
            .Select(x => (object)new { x.UserId, x.Username, x.DisplayName, ViaPoolId = x.PoolId, ViaPoolName = graph.NameOf(x.PoolId), Path = graph.PathNames(poolId, x.PoolId), x.Role }).ToList();
        return (direct.Cast<object>().ToList(), inherited);
    }

    /// <summary>添加 / 更新直属成员。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="userId">用户 ID。</param>
    /// <param name="role">角色。</param>
    /// <returns>成员记录。</returns>
    public async Task<PoolMember> UpsertMemberAsync(Actor actor, string poolId, string userId, PoolRole role)
    {
        await engine.RequirePoolManageAsync(actor, poolId);
        await GetActiveAsync(poolId);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active) ?? throw ApiException.NotFound("用户不存在或已禁用");
        _ = user; // 服务账号同样可以加入 Pool（其权限来源与普通用户一致），再通过服务 Token 的白名单进一步收窄
        // 只有 Owner / 平台管理员可以授予或调整 Owner 角色，避免管理员越权提升
        var access = await engine.GetPoolAccessAsync(actor, poolId);
        var existing = await db.PoolMembers.FirstOrDefaultAsync(m => m.PoolId == poolId && m.UserId == userId);
        if ((role == PoolRole.Owner || existing?.Role == PoolRole.Owner) && !actor.IsPlatformAdmin && access.DirectRole != PoolRole.Owner)
            throw ApiException.Forbidden("只有 Owner 或平台管理员可以调整 Owner 角色");
        if (existing == null) { existing = new PoolMember { Id = IdGen.New("pm"), PoolId = poolId, UserId = userId, Role = role }; db.PoolMembers.Add(existing); }
        else existing.Role = role;
        await db.SaveChangesAsync();
        pc.Invalidate();
        return existing;
    }

    /// <summary>移除直属成员（不允许移除最后一个 Owner）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="userId">用户 ID。</param>
    public async Task RemoveMemberAsync(Actor actor, string poolId, string userId)
    {
        await engine.RequirePoolManageAsync(actor, poolId);
        var m = await db.PoolMembers.FirstOrDefaultAsync(x => x.PoolId == poolId && x.UserId == userId) ?? throw ApiException.NotFound("该用户不是此 Pool 的直属成员");
        if (m.Role == PoolRole.Owner)
        {
            var access = await engine.GetPoolAccessAsync(actor, poolId);
            if (!actor.IsPlatformAdmin && access.DirectRole != PoolRole.Owner) throw ApiException.Forbidden("只有 Owner 或平台管理员可以移除 Owner");
            if (await db.PoolMembers.CountAsync(x => x.PoolId == poolId && x.Role == PoolRole.Owner) <= 1) throw ApiException.BadRequest("不能移除最后一个 Owner");
        }
        db.PoolMembers.Remove(m);
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    // ─────────────────────────── 移动 ───────────────────────────

    /// <summary>预览移动影响：将失去 / 新增哪些父级资源，影响多少用户与 Workspace。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">被移动的 Pool。</param>
    /// <param name="newParentId">新父 Pool，null 为移到根。</param>
    /// <returns>影响预览。</returns>
    public async Task<MovePreview> PreviewMoveAsync(Actor actor, string poolId, string? newParentId)
    {
        await engine.RequirePoolManageAsync(actor, poolId);
        var (pool, subtree, newDepth) = await ValidateMoveAsync(actor, poolId, newParentId);
        var graph = await engine.GetGraphAsync();
        var oldAnc = graph.AncestorsOf(poolId).ToList();
        var newAnc = newParentId == null ? [] : graph.AncestorsOf(newParentId).Prepend(newParentId).ToList();
        var lost = oldAnc.Except(newAnc).ToList(); var gained = newAnc.Except(oldAnc).ToList();
        async Task<List<string>> Spaces(List<string> pools) => pools.Count == 0 ? [] :
            await (from g in db.SharedSpaceGrants.AsNoTracking() where g.SubjectType == GrantSubjectType.POOL && pools.Contains(g.SubjectId) && g.InheritToChildren && g.Effect == GrantEffect.Allow
                   join s in db.SharedSpaces.AsNoTracking() on g.SharedSpaceId equals s.Id select s.Name).Distinct().ToListAsync();
        var ids = subtree.Select(s => s.Id).ToList();
        return new MovePreview(lost.Select(graph.NameOf).ToList(), gained.Select(graph.NameOf).ToList(), await Spaces(lost), await Spaces(gained),
            await db.PoolMembers.Where(m => ids.Contains(m.PoolId)).Select(m => m.UserId).Distinct().CountAsync(),
            await db.Workspaces.CountAsync(w => ids.Contains(w.PoolId) && w.Status == WorkspaceStatus.Active), ids.Count, newDepth);
    }

    /// <summary>移动 Pool 到新父级：重建闭包表并更新子树深度。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">被移动的 Pool。</param>
    /// <param name="newParentId">新父 Pool，null 为根（仅平台管理员）。</param>
    public async Task MoveAsync(Actor actor, string poolId, string? newParentId)
    {
        await engine.RequirePoolManageAsync(actor, poolId);
        var (pool, subtree, newDepth) = await ValidateMoveAsync(actor, poolId, newParentId);
        await using var tx = await db.Database.BeginTransactionAsync();
        await ApplyMoveAsync(pool, subtree, newParentId, newDepth);
        await tx.CommitAsync();
        pc.Invalidate();
    }

    /// <summary>校验移动合法性（无环、层级不超限、权限）并返回子树信息。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">被移动的 Pool。</param>
    /// <param name="newParentId">新父 Pool。</param>
    private async Task<(Pool Pool, List<Pool> Subtree, int NewDepth)> ValidateMoveAsync(Actor actor, string poolId, string? newParentId)
    {
        var pool = await GetActiveAsync(poolId);
        var graph = await engine.GetGraphAsync();
        var subtreeIds = graph.DescendantsOf(poolId).Append(poolId).ToList();
        if (newParentId == pool.ParentPoolId) throw ApiException.BadRequest("目标父级与当前父级相同");
        if (newParentId != null)
        {
            if (subtreeIds.Contains(newParentId)) throw ApiException.BadRequest("不能把 Pool 移动到自身或其子孙之下（会形成环）");
            await engine.RequirePoolManageAsync(actor, newParentId);
            await GetActiveAsync(newParentId);
        }
        else if (!actor.IsPlatformAdmin) throw ApiException.Forbidden("只有平台管理员可以把 Pool 移动到顶级");
        var subtree = await db.Pools.Where(p => subtreeIds.Contains(p.Id)).ToListAsync();
        var newParentDepth = newParentId == null ? 0 : graph.Nodes[newParentId].Depth;
        var newDepth = newParentDepth + 1;
        var maxRel = subtree.Max(p => p.Depth) - pool.Depth;
        if (newDepth + maxRel > opt.Value.Platform.MaxPoolDepth) throw ApiException.BadRequest($"移动后子树最深将达 {newDepth + maxRel} 层，超过上限 {opt.Value.Platform.MaxPoolDepth} 层");
        if (await db.Pools.AnyAsync(p => p.Id != poolId && p.ParentPoolId == newParentId && p.Name == pool.Name && p.Status == PoolStatus.Active)) throw ApiException.Conflict("目标父级下已存在同名 Pool");
        return (pool, subtree, newDepth);
    }

    /// <summary>执行移动：删除子树与旧祖先的闭包行，再与新祖先链重新建立闭包行。</summary>
    /// <param name="pool">被移动 Pool。</param>
    /// <param name="subtree">子树（含自身）。</param>
    /// <param name="newParentId">新父 Pool。</param>
    /// <param name="newDepth">移动后自身层级。</param>
    private async Task ApplyMoveAsync(Pool pool, List<Pool> subtree, string? newParentId, int newDepth)
    {
        var subIds = subtree.Select(s => s.Id).ToList();
        var rel = await db.PoolClosures.Where(c => c.AncestorPoolId == pool.Id).ToDictionaryAsync(c => c.DescendantPoolId, c => c.Depth);
        await db.PoolClosures.Where(c => subIds.Contains(c.DescendantPoolId) && !subIds.Contains(c.AncestorPoolId)).ExecuteDeleteAsync();
        // 批量删除绕过了变更跟踪：清掉本上下文里已跟踪的闭包实体，避免随后新增同主键行时冲突
        foreach (var e in db.ChangeTracker.Entries<PoolClosure>().ToList()) e.State = EntityState.Detached;
        if (newParentId != null)
        {
            var newAnc = await db.PoolClosures.Where(c => c.DescendantPoolId == newParentId).ToListAsync();
            foreach (var a in newAnc)
                foreach (var (sid, r) in rel)
                    db.PoolClosures.Add(new PoolClosure { AncestorPoolId = a.AncestorPoolId, DescendantPoolId = sid, Depth = a.Depth + 1 + r });
        }
        var baseDepth = pool.Depth;
        foreach (var s in subtree) { s.Depth = newDepth + (s.Depth - baseDepth); s.UpdatedAt = DateTime.Now; }
        pool.ParentPoolId = newParentId;
        await db.SaveChangesAsync();
    }

    // ─────────────────────────── 删除 ───────────────────────────

    /// <summary>删除（归档）Pool：子 Pool 与 Workspace 按选项处理，共享空间默认保留（转为组织级）。不会级联删除数据。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="childMode">子 Pool 处理：ToParent（移到父级）/ ToRoot（移到顶级，仅管理员）/ Archive（连同归档）。</param>
    /// <param name="workspaceMode">Workspace 处理：Archive（归档）/ Transfer（转移到 transferToPoolId）。</param>
    /// <param name="transferToPoolId">转移目标 Pool。</param>
    public async Task DeleteAsync(Actor actor, string poolId, string childMode, string workspaceMode, string? transferToPoolId)
    {
        await engine.RequirePoolManageAsync(actor, poolId);
        var pool = await GetActiveAsync(poolId);
        var graph = await engine.GetGraphAsync();
        var children = await db.Pools.Where(p => p.ParentPoolId == poolId && p.Status == PoolStatus.Active).ToListAsync();
        if (children.Count > 0 && childMode is not ("ToParent" or "ToRoot" or "Archive")) throw ApiException.BadRequest("该 Pool 含子 Pool，请选择处理方式：ToParent / ToRoot / Archive");
        if (childMode == "ToRoot" && !actor.IsPlatformAdmin) throw ApiException.Forbidden("只有平台管理员可以把子 Pool 移到顶级");

        var wsList = await db.Workspaces.Where(w => w.PoolId == poolId && w.Status == WorkspaceStatus.Active).ToListAsync();
        if (wsList.Count > 0 && workspaceMode is not ("Archive" or "Transfer")) throw ApiException.BadRequest("该 Pool 含 Workspace，请选择处理方式：Archive / Transfer");
        Pool? target = null;
        if (wsList.Count > 0 && workspaceMode == "Transfer")
        {
            if (string.IsNullOrEmpty(transferToPoolId)) throw ApiException.BadRequest("请选择转移目标 Pool");
            if (transferToPoolId == poolId || graph.DescendantsOf(poolId).Contains(transferToPoolId)) throw ApiException.BadRequest("不能转移到被删除的 Pool 或其子孙");
            await engine.RequirePoolManageAsync(actor, transferToPoolId);
            target = await GetActiveAsync(transferToPoolId);
        }

        // 子 Pool 处理
        foreach (var c in children)
        {
            if (childMode == "Archive") continue;
            var np = childMode == "ToParent" ? pool.ParentPoolId : null;
            var fresh = await GetActiveAsync(c.Id);
            var (p, sub, depth) = await ValidateMoveAsyncNoPermission(fresh, np);
            await using var tx = await db.Database.BeginTransactionAsync();
            await ApplyMoveAsync(p, sub, np, depth);
            await tx.CommitAsync();
        }
        pc.Invalidate();
        var archiveIds = new List<string> { poolId };
        if (childMode == "Archive") archiveIds.AddRange(graph.DescendantsOf(poolId));
        foreach (var pid in archiveIds)
        {
            var list = await db.Workspaces.Where(w => w.PoolId == pid && w.Status == WorkspaceStatus.Active).ToListAsync();
            foreach (var w in list)
            {
                if (pid == poolId && target != null) await workspaces.TransferAsync(w, target.Id);
                else await workspaces.ArchiveInternalAsync(w);
            }
            // 共享空间保留，转为组织级；移除针对该 Pool 的授权；清理成员
            foreach (var s in await db.SharedSpaces.Where(s => s.OwnerPoolId == pid).ToListAsync()) s.OwnerPoolId = null;
            db.SharedSpaceGrants.RemoveRange(await db.SharedSpaceGrants.Where(g => g.SubjectType == GrantSubjectType.POOL && g.SubjectId == pid).ToListAsync());
            db.PoolMembers.RemoveRange(await db.PoolMembers.Where(m => m.PoolId == pid).ToListAsync());
            var p2 = await db.Pools.FirstAsync(x => x.Id == pid);
            p2.Status = PoolStatus.Archived; p2.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    /// <summary>删除流程中的内部移动校验（权限已在外层校验）。</summary>
    /// <param name="pool">被移动 Pool。</param>
    /// <param name="newParentId">新父 Pool。</param>
    private async Task<(Pool, List<Pool>, int)> ValidateMoveAsyncNoPermission(Pool pool, string? newParentId)
    {
        var graph = await engine.GetGraphAsync();
        var ids = graph.DescendantsOf(pool.Id).Append(pool.Id).ToList();
        var subtree = await db.Pools.Where(p => ids.Contains(p.Id)).ToListAsync();
        var newDepth = (newParentId == null ? 0 : graph.Nodes[newParentId].Depth) + 1;
        // 重名时自动加后缀，避免删除流程被阻断
        if (await db.Pools.AnyAsync(p => p.Id != pool.Id && p.ParentPoolId == newParentId && p.Name == pool.Name && p.Status == PoolStatus.Active)) pool.Name += "-" + DateTime.Now.ToString("MMddHHmm");
        return (pool, subtree, newDepth);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    /// <summary>校验并规范化名称。</summary>
    /// <param name="name">名称。</param>
    private static string RequireName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw ApiException.BadRequest("名称不能为空");
        if (name.Length > 100) throw ApiException.BadRequest("名称不能超过 100 个字符");
        return name;
    }

    /// <summary>校验 Runtime Provider 名称。</summary>
    /// <param name="provider">Provider 名。</param>
    private static string ValidateProvider(string provider) =>
        provider is "DeepSeekHarness" or "Mock" ? provider : throw ApiException.BadRequest("Runtime Provider 仅支持 DeepSeekHarness / Mock");
}
