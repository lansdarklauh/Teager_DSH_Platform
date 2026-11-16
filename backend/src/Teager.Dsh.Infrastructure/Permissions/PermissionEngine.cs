using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;

namespace Teager.Dsh.Infrastructure.Permissions;

/// <summary>权限缓存版本号：任何影响权限的变更调用 <see cref="Invalidate"/>，所有缓存键随版本号整体失效。</summary>
public class PermissionCache
{
    private long _version;
    /// <summary>当前版本号。</summary>
    public long Version => Interlocked.Read(ref _version);
    /// <summary>使全部权限缓存失效（成员 / 授权 / Pool 结构 / Workspace 权限变更时调用）。</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);
}

/// <summary>
/// 权限引擎：所有 API 不自行拼权限逻辑，统一由此判断。
/// 核心规则：父 → 子可继承；子 → 父默认不继承；兄弟 Pool 默认不共享；显式拒绝 &gt; 显式授权 &gt; 直接 Pool 授权 &gt; 继承授权 &gt; 默认拒绝。
/// </summary>
public class PermissionEngine(DshDbContext db, IMemoryCache cache, PermissionCache pc)
{
    private static readonly MemoryCacheEntryOptions CacheOpt = new() { SlidingExpiration = TimeSpan.FromMinutes(10) };

    // ─────────────────────────── Pool 树 ───────────────────────────

    /// <summary>获取 Pool 树内存图（由闭包表构建并缓存）。</summary>
    /// <returns>Pool 图。</returns>
    public async Task<PoolGraph> GetGraphAsync()
    {
        var key = $"graph:{pc.Version}";
        if (cache.TryGetValue(key, out PoolGraph? cached) && cached != null) return cached;
        var graph = new PoolGraph();
        foreach (var p in await db.Pools.AsNoTracking().Where(p => p.Status == PoolStatus.Active).ToListAsync())
        {
            graph.Nodes[p.Id] = new PoolNode(p.Id, p.ParentPoolId, p.Name, p.Depth, p.Status);
            graph.Ancestors[p.Id] = [];
            graph.Descendants[p.Id] = [];
        }
        var rows = await db.PoolClosures.AsNoTracking().Where(c => c.Depth > 0).OrderBy(c => c.Depth).ToListAsync();
        foreach (var r in rows)
        {
            if (!graph.Nodes.ContainsKey(r.AncestorPoolId) || !graph.Nodes.ContainsKey(r.DescendantPoolId)) continue;
            graph.Ancestors[r.DescendantPoolId].Add(r.AncestorPoolId);
            graph.Descendants[r.AncestorPoolId].Add(r.DescendantPoolId);
        }
        cache.Set(key, graph, CacheOpt);
        return graph;
    }

    /// <summary>获取用户在各 Pool 的直属角色（仅 DIRECT 成员关系）。</summary>
    /// <param name="userId">用户 ID。</param>
    /// <returns>PoolId → 角色。</returns>
    public async Task<IReadOnlyDictionary<string, PoolRole>> GetDirectRolesAsync(string userId)
    {
        var key = $"droles:{pc.Version}:{userId}";
        if (cache.TryGetValue(key, out Dictionary<string, PoolRole>? cached) && cached != null) return cached;
        var dict = await db.PoolMembers.AsNoTracking().Where(m => m.UserId == userId).ToDictionaryAsync(m => m.PoolId, m => m.Role);
        cache.Set(key, dict, CacheOpt);
        return dict;
    }

    /// <summary>用户的有效 Pool 集合 = 直属 Pool + 全部祖先 Pool（不含兄弟与子 Pool）。</summary>
    /// <param name="userId">用户 ID。</param>
    /// <returns>有效 Pool ID 集合。</returns>
    public async Task<HashSet<string>> GetEffectivePoolIdsAsync(string userId)
    {
        var graph = await GetGraphAsync();
        var set = new HashSet<string>();
        foreach (var pid in (await GetDirectRolesAsync(userId)).Keys)
        {
            if (!graph.Nodes.ContainsKey(pid)) continue;
            set.Add(pid);
            foreach (var a in graph.AncestorsOf(pid)) set.Add(a);
        }
        return set;
    }

    /// <summary>判断 Token 的 Pool 白名单是否覆盖某 Pool（白名单 Pool 及其后代均视为覆盖）。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="graph">Pool 图。</param>
    private static bool TokenCoversPool(Actor actor, string poolId, PoolGraph graph)
    {
        if (actor.AllowedPools.Count == 0) return true;
        if (actor.AllowedPools.Contains(poolId)) return true;
        return graph.AncestorsOf(poolId).Any(actor.AllowedPools.Contains);
    }

    /// <summary>Token 是否允许访问某 Workspace（Workspace 白名单或其所属 Pool 被覆盖）。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="graph">Pool 图。</param>
    private static bool TokenCoversWorkspace(Actor actor, Workspace ws, PoolGraph graph)
    {
        if (!actor.ResourceRestricted) return true;
        if (actor.AllowedWorkspaces.Contains(ws.Id)) return true;
        return actor.AllowedPools.Count > 0 && TokenCoversPool(actor, ws.PoolId, graph);
    }

    /// <summary>计算用户对某 Pool 的访问权限。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="poolId">Pool ID。</param>
    /// <returns>访问结果。</returns>
    public async Task<PoolAccess> GetPoolAccessAsync(Actor actor, string poolId)
    {
        var graph = await GetGraphAsync();
        if (!graph.Nodes.ContainsKey(poolId)) return new PoolAccess(false, false, null, "none", null);
        if (actor.AllowedPools.Count > 0 && !TokenCoversPool(actor, poolId, graph))
            return new PoolAccess(false, false, null, "none", null);
        if (actor.AllowedPools.Count == 0 && actor.ResourceRestricted)
        {
            if (actor.AllowedWorkspaces.Count == 0 || !await db.Workspaces.AnyAsync(w => actor.AllowedWorkspaces.Contains(w.Id) && w.PoolId == poolId))
                return new PoolAccess(false, false, null, "token-scope", null);
            return new PoolAccess(true, false, null, "token-workspace", null);
        }
        if (actor.IsPlatformAdmin && !actor.IsServiceAccount) return new PoolAccess(true, !actor.ResourceRestricted, PoolRole.Owner, "platform-admin", null);

        var roles = await GetDirectRolesAsync(actor.UserId);
        roles.TryGetValue(poolId, out var direct);
        var hasDirect = roles.ContainsKey(poolId);
        if (hasDirect && direct >= PoolRole.Admin) return new PoolAccess(true, false, direct, "direct", poolId);

        // 祖先 Pool 的管理员可向下委托管理子 Pool（仅管理权，不含私有资源的数据访问权）
        foreach (var a in graph.AncestorsOf(poolId))
            if (roles.TryGetValue(a, out var ar) && ar >= PoolRole.Admin)
                return new PoolAccess(true, false, hasDirect ? direct : null, "ancestor-member", a);

        if (hasDirect) return new PoolAccess(true, false, direct, "direct", poolId);
        if (await db.Workspaces.AnyAsync(w => w.PoolId == poolId && w.AssignedUserId == actor.UserId && w.Status == WorkspaceStatus.Active))
            return new PoolAccess(true, false, null, "personal-workspace", poolId);

        // 子 Pool 的成员可以看到祖先 Pool（用于展示层级与继承资源）
        foreach (var d in graph.DescendantsOf(poolId))
            if (roles.ContainsKey(d)) return new PoolAccess(true, false, null, "descendant-member", d);

        return new PoolAccess(false, false, null, "none", null);
    }

    /// <summary>列出用户可见的全部 Pool 及其权限（用于 Pool 树展示）。</summary>
    /// <param name="actor">操作主体。</param>
    /// <returns>PoolId → 访问结果。</returns>
    public async Task<Dictionary<string, PoolAccess>> GetAccessiblePoolsAsync(Actor actor)
    {
        var graph = await GetGraphAsync();
        var result = new Dictionary<string, PoolAccess>();
        foreach (var id in graph.Nodes.Keys)
        {
            var access = await GetPoolAccessAsync(actor, id);
            if (access.CanView) result[id] = access;
        }
        return result;
    }

    /// <summary>要求可见某 Pool，否则 404；返回访问结果。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="poolId">Pool ID。</param>
    public async Task<PoolAccess> RequirePoolViewAsync(Actor actor, string poolId)
    {
        var a = await GetPoolAccessAsync(actor, poolId);
        if (!a.CanView) throw ApiException.NotFound("Pool 不存在或无权访问");
        return a;
    }

    /// <summary>要求可管理某 Pool，可见但无管理权返回 403。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="poolId">Pool ID。</param>
    public async Task<PoolAccess> RequirePoolManageAsync(Actor actor, string poolId)
    {
        actor.RequirePlatformAdmin();
        var a = await RequirePoolViewAsync(actor, poolId);
        if (!a.CanManage) throw ApiException.Forbidden("需要该 Pool 的管理员权限");
        return a;
    }

    // ─────────────────────────── Workspace ───────────────────────────

    /// <summary>计算用户对 Workspace 的访问：显式拒绝 &gt; 显式授权 &gt; 直属 Pool 成员 &gt; 继承（子 Pool 成员）&gt; 默认拒绝。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="preloaded">批量场景下预取的当前用户显式成员记录（WorkspaceId → 记录），避免逐个查询。</param>
    /// <returns>访问结果。</returns>
    public async Task<WorkspaceAccess> GetWorkspaceAccessAsync(Actor actor, Workspace ws, IReadOnlyDictionary<string, WorkspaceMember>? preloaded = null)
    {
        var graph = await GetGraphAsync();
        if (!TokenCoversWorkspace(actor, ws, graph)) return new WorkspaceAccess(null, false, "token-scope", null, []);

        var wsName = "Workspace:" + ws.Name;
        var explicitRow = preloaded != null
            ? preloaded.GetValueOrDefault(ws.Id)
            : await db.WorkspaceMembers.AsNoTracking().FirstOrDefaultAsync(m => m.WorkspaceId == ws.Id && m.UserId == actor.UserId);
        if (explicitRow is { Effect: GrantEffect.Deny })
            return new WorkspaceAccess(null, true, "explicit-deny", null, [wsName]);
        if (actor.IsPlatformAdmin)
            return new WorkspaceAccess(WorkspaceRole.Admin, false, "platform-admin", null, ["平台管理员", wsName]);
        if (actor.IsServiceAccount)
            return explicitRow == null ? new WorkspaceAccess(null, false, "none", null, [])
                : new WorkspaceAccess((WorkspaceRole)Math.Min((int)explicitRow.Role, (int)WorkspaceRole.Member), false, "workspace-member", null, [wsName]);
        if (ws.AssignedUserId != actor.UserId)
            return new WorkspaceAccess(null, false, "not-assigned", null, []);
        return new WorkspaceAccess(WorkspaceRole.Member, false, "personal-workspace", null, [wsName]);
    }

    /// <summary>判断用户能否管理 Workspace 元数据（改名、成员、挂载、删除）：平台管理员 / Workspace Admin 以上 / 所属 Pool 管理者。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="wa">已计算的访问结果。</param>
    public async Task<bool> CanManageWorkspaceAsync(Actor actor, Workspace ws, WorkspaceAccess wa)
    {
        if (!actor.IsPlatformAdmin || actor.IsServiceAccount || actor.ResourceRestricted) return false;
        if (wa.Denied || wa.Source == "token-scope") return false; // Token 白名单未覆盖：连“仅管理可见”也不允许，避免通过 Pool 管理权绕过白名单
        if (wa.Role >= WorkspaceRole.Admin) return true;
        return (await GetPoolAccessAsync(actor, ws.PoolId)).CanManage;
    }

    /// <summary>加载 Workspace 并要求满足最小角色；无任何可见性返回 404，权限不足返回 403。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="min">要求的最小数据角色；null 表示仅需可见（含 Pool 管理者的元数据管理可见性）。</param>
    /// <param name="manage">是否要求元数据管理权。</param>
    /// <returns>Workspace 与访问结果。</returns>
    public async Task<(Workspace Ws, WorkspaceAccess Access)> RequireWorkspaceAsync(Actor actor, string workspaceId, WorkspaceRole? min, bool manage = false)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId) ?? throw ApiException.NotFound("Workspace 不存在或无权访问");
        var wa = await GetWorkspaceAccessAsync(actor, ws);
        var canManage = await CanManageWorkspaceAsync(actor, ws, wa);
        var visible = wa.Role != null || canManage;
        if (wa.Denied || !visible) throw ApiException.NotFound("Workspace 不存在或无权访问");
        if (manage && !canManage) throw ApiException.Forbidden("需要 Workspace 管理员权限");
        if (min != null && (wa.Role == null || wa.Role < min)) throw ApiException.Forbidden("当前角色无权执行该操作");
        if (min >= WorkspaceRole.Member && ws.Status != WorkspaceStatus.Active) throw ApiException.Conflict("Workspace 已归档，不能执行写入或任务");
        return (ws, wa);
    }

    // ─────────────────────────── 共享空间 ───────────────────────────

    /// <summary>计算某 Pool 通过授权（直接 + 祖先且 inherit_to_children）可用的共享空间集合。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <returns>SharedSpaceId → 有效授权（被显式拒绝的条目 Denied=true）。</returns>
    public async Task<IReadOnlyDictionary<string, PoolSpaceAccess>> GetPoolSharedSpacesAsync(string poolId)
    {
        var key = $"pspaces:{pc.Version}:{poolId}";
        if (cache.TryGetValue(key, out Dictionary<string, PoolSpaceAccess>? cached) && cached != null) return cached;

        var graph = await GetGraphAsync();
        var ancestors = graph.AncestorsOf(poolId).ToList();
        var chain = new List<string> { poolId };
        chain.AddRange(ancestors);
        var grants = await db.SharedSpaceGrants.AsNoTracking()
            .Where(g => g.SubjectType == GrantSubjectType.POOL && chain.Contains(g.SubjectId)).ToListAsync();
        var activeSpaces = (await db.SharedSpaces.AsNoTracking().Where(s => s.Status == SharedSpaceStatus.Active).Select(s => s.Id).ToListAsync()).ToHashSet();

        var result = new Dictionary<string, PoolSpaceAccess>();
        foreach (var grp in grants.Where(g => activeSpaces.Contains(g.SharedSpaceId)).GroupBy(g => g.SharedSpaceId))
        {
            // 适用的授权：直接授权给本 Pool，或来自祖先且开启向下继承
            var applicable = grp.Where(g => g.SubjectId == poolId || g.InheritToChildren).ToList();
            if (applicable.Count == 0) continue;

            var deny = applicable.Where(g => g.Effect == GrantEffect.Deny).OrderBy(g => chain.IndexOf(g.SubjectId)).FirstOrDefault();
            if (deny != null)
            {
                result[grp.Key] = new PoolSpaceAccess(grp.Key, null, true, deny.SubjectId == poolId ? "direct" : "inherited", deny.SubjectId, deny.Id, graph.PathNames(deny.SubjectId, poolId));
                continue;
            }
            // 取最高权限；同级权限优先直接授权、其次更近的祖先
            var best = applicable.OrderByDescending(g => g.Permission).ThenBy(g => chain.IndexOf(g.SubjectId)).First();
            result[grp.Key] = new PoolSpaceAccess(grp.Key, best.Permission, false, best.SubjectId == poolId ? "direct" : "inherited", best.SubjectId, best.Id, graph.PathNames(best.SubjectId, poolId));
        }
        cache.Set(key, result, CacheOpt);
        return result;
    }

    /// <summary>计算用户对共享空间本身的访问权限（用于共享空间 API 直接浏览 / 管理）。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="space">共享空间。</param>
    /// <returns>访问结论。</returns>
    public async Task<SpaceAccess> GetUserSpaceAccessAsync(Actor actor, SharedSpace space)
    {
        var notes = new List<string>();
        if ((space.Status != SharedSpaceStatus.Active && !actor.IsPlatformAdmin) || (actor.Via == "token" && (!actor.HasScope(Scopes.ShareRead)
            || (actor.ResourceRestricted && !actor.AllowedSharedSpaces.Contains(space.Id)))))
            return new SpaceAccess(null, "token-scope", null, null, [], []);
        var graph = await GetGraphAsync();
        if (actor.AllowedPools.Count > 0 && space.OwnerPoolId != null && !TokenCoversPool(actor, space.OwnerPoolId, graph))
            return new SpaceAccess(null, "token-scope", null, null, [], ["Token 的 Pool 白名单未覆盖该共享空间所属 Pool"]);

        var userGrants = await db.SharedSpaceGrants.AsNoTracking()
            .Where(g => g.SharedSpaceId == space.Id && g.SubjectType == GrantSubjectType.USER && g.SubjectId == actor.UserId).ToListAsync();
        var userDeny = userGrants.FirstOrDefault(g => g.Effect == GrantEffect.Deny);
        if (userDeny != null)
            return new SpaceAccess(null, "explicit-deny", null, userDeny.Id, [], ["用户被显式拒绝访问该共享空间（显式拒绝优先级最高）"]);

        // 用户所属（直属）Pool 上的有效授权；任一链路上存在显式拒绝则整体拒绝
        SharePermission? poolPerm = null; PoolSpaceAccess? poolBest = null;
        foreach (var pid in (await GetDirectRolesAsync(actor.UserId)).Keys)
        {
            var map = await GetPoolSharedSpacesAsync(pid);
            if (!map.TryGetValue(space.Id, out var psa)) continue;
            if (psa.Denied)
                return new SpaceAccess(null, "explicit-deny", psa.ViaPoolId, psa.GrantId, psa.Path, [$"Pool「{graph.NameOf(psa.ViaPoolId)}」被显式拒绝访问该共享空间"]);
            if (poolPerm == null || psa.Permission > poolPerm) { poolPerm = psa.Permission; poolBest = psa; }
        }

        SharePermission? basePerm; string source; string? via = null; string? grantId = null; IReadOnlyList<string> path = [];
        var userAllow = userGrants.FirstOrDefault(g => g.Effect == GrantEffect.Allow);
        if (userAllow != null)
        {
            basePerm = userAllow.Permission; source = "user-grant"; grantId = userAllow.Id;
            notes.Add($"用户级显式授权 {userAllow.Permission}（高于 Pool 授权的优先级）");
        }
        else if (poolBest != null)
        {
            basePerm = actor.IsPlatformAdmin ? poolBest.Permission : SharePermission.RO; source = poolBest.Source == "direct" ? "pool-direct" : "pool-inherited";
            via = poolBest.ViaPoolId; grantId = poolBest.GrantId; path = poolBest.Path;
            notes.Add(poolBest.Source == "direct"
                ? $"通过所属 Pool「{graph.NameOf(poolBest.ViaPoolId)}」的直接授权获得 {poolBest.Permission}"
                : $"通过祖先 Pool「{graph.NameOf(poolBest.ViaPoolId)}」向下继承获得 {poolBest.Permission}");
        }
        else { basePerm = null; source = "none"; notes.Add("没有任何授权路径，默认拒绝"); }

        // 管理类权限：平台管理员、所属 Pool 管理者、创建人
        SharePermission? managePerm = null; string? manageSource = null;
        if (actor.IsPlatformAdmin) { managePerm = SharePermission.MANAGE; manageSource = "platform-admin"; }
        // 仅“所属 Pool 的直属管理员”拥有管理权；祖先 Pool 管理员的委托管理不包含子 Pool 私有资源（父不自动获得子的私有资源）

        if (managePerm != null && (basePerm == null || managePerm > basePerm))
        {
            notes.Add(manageSource switch { "platform-admin" => "平台管理员拥有全部管理权限", "owner-pool-admin" => "所属 Pool 管理员拥有管理权限", _ => "创建人拥有管理权限" });
            return new SpaceAccess(managePerm, manageSource!, via, grantId, path, notes);
        }
        if (!actor.IsPlatformAdmin && basePerm > SharePermission.RW) basePerm = SharePermission.RW;
        return new SpaceAccess(basePerm, source, via, grantId, path, notes);
    }

    /// <summary>计算用户在某 Workspace 内对共享空间的有效权限：Pool 授权 → 用户级覆盖 → 挂载覆盖（只降不升）→ Workspace 角色上限。</summary>
    /// <param name="actor">操作主体。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="wa">用户在 Workspace 的访问结果。</param>
    /// <param name="space">共享空间。</param>
    /// <param name="mount">挂载记录（可空）。</param>
    /// <returns>访问结论。</returns>
    public async Task<SpaceAccess> GetWorkspaceSpaceAccessAsync(Actor actor, Workspace ws, WorkspaceAccess wa, SharedSpace space, WorkspaceSharedSpaceMount? mount)
    {
        var notes = new List<string>();
        if (wa.Role == null || wa.Denied || space.Status != SharedSpaceStatus.Active || mount is { Enabled: false }
            || (actor.Via == "token" && (!actor.HasScope(Scopes.ShareRead) || (actor.ResourceRestricted && !actor.AllowedSharedSpaces.Contains(space.Id)))))
            return new SpaceAccess(null, "not-allowed", null, null, [], []);
        var graph = await GetGraphAsync();
        var map = await GetPoolSharedSpacesAsync(ws.PoolId);
        if (!map.TryGetValue(space.Id, out var psa))
            return new SpaceAccess(null, "not-granted", null, null, [], [$"Workspace 所属 Pool「{graph.NameOf(ws.PoolId)}」未获得该共享空间授权"]);
        if (psa.Denied)
            return new SpaceAccess(null, "explicit-deny", psa.ViaPoolId, psa.GrantId, psa.Path, ["Pool 链路上存在显式拒绝"]);

        var perm = actor.IsPlatformAdmin ? psa.Permission!.Value : SharePermission.RO;
        notes.Add(psa.Source == "direct"
            ? $"Pool「{graph.NameOf(psa.ViaPoolId)}」直接授权 {perm}"
            : $"祖先 Pool「{graph.NameOf(psa.ViaPoolId)}」向下继承授权 {perm}");
        var source = psa.Source == "direct" ? "pool-direct" : "pool-inherited";

        var userGrant = await db.SharedSpaceGrants.AsNoTracking()
            .Where(g => g.SharedSpaceId == space.Id && g.SubjectType == GrantSubjectType.USER && g.SubjectId == actor.UserId).ToListAsync();
        if (userGrant.Any(g => g.Effect == GrantEffect.Deny))
            return new SpaceAccess(null, "explicit-deny", null, userGrant.First(g => g.Effect == GrantEffect.Deny).Id, [], ["用户被显式拒绝访问该共享空间"]);
        var ua = userGrant.FirstOrDefault(g => g.Effect == GrantEffect.Allow);
        if (ua != null) { perm = ua.Permission; source = "user-grant"; notes.Add($"用户级显式授权覆盖为 {perm}"); }
        if (!actor.IsPlatformAdmin && perm > SharePermission.RW) perm = SharePermission.RW;
        if (actor.Via == "token" && !actor.HasScope(Scopes.ShareWrite) && perm > SharePermission.RO) perm = SharePermission.RO;

        if (mount?.PermissionOverride != null && mount.PermissionOverride < perm)
        {
            perm = mount.PermissionOverride.Value; notes.Add($"挂载覆盖将权限降低为 {perm}（覆盖只能降低，不能扩大）");
        }
        if (wa.Role == WorkspaceRole.Viewer && perm > SharePermission.RO)
        {
            perm = SharePermission.RO; notes.Add("Workspace 角色为 Viewer，权限限制为 RO");
        }
        var path = psa.Path.ToList();
        path.Add("Workspace:" + ws.Name);
        return new SpaceAccess(perm, source, psa.ViaPoolId, psa.GrantId, path, notes);
    }
}
