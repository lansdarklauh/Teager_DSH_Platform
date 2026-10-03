using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>共享空间服务：创建 / 授权 / 列表 / 权限判断辅助。文件读写见 <see cref="FileStore"/>。</summary>
public class SharedSpaceService(DshDbContext db, PermissionEngine engine, PermissionCache pc, StoragePaths paths)
{
    /// <summary>创建共享空间。所属 Pool 需管理权；无所属 Pool（组织级）仅平台管理员可建。所属 Pool 默认获得 RW 授权且不向子 Pool 继承（最小权限）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="name">名称。</param>
    /// <param name="description">描述。</param>
    /// <param name="ownerPoolId">所属 Pool。</param>
    /// <param name="quotaMb">配额 MB（0 不限）。</param>
    /// <param name="mountName">默认挂载名（空则由名称生成）。</param>
    /// <returns>共享空间。</returns>
    public async Task<SharedSpace> CreateAsync(Actor actor, string name, string? description, string? ownerPoolId, int quotaMb, string? mountName)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 100) throw ApiException.BadRequest("名称不能为空且不超过 100 个字符");
        if (ownerPoolId != null) await engine.RequirePoolManageAsync(actor, ownerPoolId);
        else if (!actor.IsPlatformAdmin) throw ApiException.Forbidden("只有平台管理员可以创建组织级共享空间，请选择所属 Pool");
        if (quotaMb < 0) throw ApiException.BadRequest("配额不能为负数");
        var mn = string.IsNullOrWhiteSpace(mountName) ? Slug(name) : mountName.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(mn, "^[a-z0-9][a-z0-9._-]{0,47}$")) throw ApiException.BadRequest("挂载名只能包含小写字母、数字、点、下划线、连字符");
        var space = new SharedSpace { Id = IdGen.New("ss"), OrganizationId = await db.Organizations.Select(o => o.Id).FirstAsync(), OwnerPoolId = ownerPoolId, Name = name, Description = description, MountName = mn, QuotaMb = quotaMb, CreatedBy = actor.UserId };
        db.SharedSpaces.Add(space);
        if (ownerPoolId != null)
            db.SharedSpaceGrants.Add(new SharedSpaceGrant { Id = IdGen.New("grt"), SharedSpaceId = space.Id, SubjectType = GrantSubjectType.POOL, SubjectId = ownerPoolId, Permission = SharePermission.RW, InheritToChildren = false, CreatedBy = actor.UserId });
        await db.SaveChangesAsync();
        Directory.CreateDirectory(paths.SharedSpace(space.Id));
        pc.Invalidate();
        return space;
    }

    /// <summary>更新共享空间信息（需 MANAGE）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">共享空间 ID。</param>
    /// <param name="name">名称。</param>
    /// <param name="description">描述。</param>
    /// <param name="quotaMb">配额。</param>
    /// <param name="mountName">默认挂载名。</param>
    /// <returns>更新后的共享空间。</returns>
    public async Task<SharedSpace> UpdateAsync(Actor actor, string id, string? name, string? description, int? quotaMb, string? mountName)
    {
        var (space, _) = await RequireAsync(actor, id, SharePermission.MANAGE);
        if (!string.IsNullOrWhiteSpace(name)) space.Name = name.Trim();
        if (description != null) space.Description = description;
        if (quotaMb != null) { if (quotaMb < 0) throw ApiException.BadRequest("配额不能为负数"); space.QuotaMb = quotaMb.Value; }
        if (!string.IsNullOrWhiteSpace(mountName))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(mountName.Trim().ToLowerInvariant(), "^[a-z0-9][a-z0-9._-]{0,47}$")) throw ApiException.BadRequest("挂载名格式不正确");
            space.MountName = mountName.Trim().ToLowerInvariant();
        }
        space.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        return space;
    }

    /// <summary>删除共享空间：默认仅归档（数据保留）；permanent=true 且没有挂载时才彻底删除数据。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">共享空间 ID。</param>
    /// <param name="permanent">是否永久删除。</param>
    public async Task DeleteAsync(Actor actor, string id, bool permanent)
    {
        var (space, _) = await RequireAsync(actor, id, SharePermission.MANAGE);
        if (permanent)
        {
            if (await db.WorkspaceSharedSpaceMounts.AnyAsync(m => m.SharedSpaceId == id)) throw ApiException.Conflict("仍有 Workspace 挂载该共享空间，请先取消挂载");
            var dir = paths.SharedSpace(id); if (Directory.Exists(dir)) Directory.Delete(dir, true);
            db.SharedSpaceGrants.RemoveRange(await db.SharedSpaceGrants.Where(g => g.SharedSpaceId == id).ToListAsync());
            db.SharedSpaces.Remove(space);
        }
        else { space.Status = SharedSpaceStatus.Archived; space.UpdatedAt = DateTime.Now; }
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    /// <summary>加载共享空间并要求最小权限；无任何访问权返回 404，权限不足 403。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">共享空间 ID。</param>
    /// <param name="min">最小权限。</param>
    /// <returns>共享空间与访问结论。</returns>
    public async Task<(SharedSpace Space, SpaceAccess Access)> RequireAsync(Actor actor, string id, SharePermission min)
    {
        var space = await db.SharedSpaces.FirstOrDefaultAsync(s => s.Id == id) ?? throw ApiException.NotFound("共享空间不存在或无权访问");
        var access = await engine.GetUserSpaceAccessAsync(actor, space);
        if (access.Permission == null) throw ApiException.NotFound("共享空间不存在或无权访问");
        if (access.Permission < min) throw ApiException.Forbidden($"需要 {min} 权限，当前为 {access.Permission}");
        return (space, access);
    }

    /// <summary>列出当前用户可访问的共享空间（含有效权限与来源）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="ownerPoolId">按所属 Pool 过滤。</param>
    /// <param name="includeArchived">是否包含已归档。</param>
    /// <returns>视图列表。</returns>
    public async Task<List<object>> ListAsync(Actor actor, string? ownerPoolId, bool includeArchived)
    {
        var q = db.SharedSpaces.AsNoTracking().AsQueryable();
        if (!includeArchived) q = q.Where(s => s.Status == SharedSpaceStatus.Active);
        if (ownerPoolId != null) q = q.Where(s => s.OwnerPoolId == ownerPoolId);
        var graph = await engine.GetGraphAsync();
        var result = new List<object>();
        foreach (var s in await q.OrderBy(s => s.Name).ToListAsync())
        {
            var a = await engine.GetUserSpaceAccessAsync(actor, s);
            if (a.Permission == null) continue;
            result.Add(new
            {
                s.Id, s.Name, s.Description, s.MountName, s.OwnerPoolId, OwnerPoolName = s.OwnerPoolId == null ? "组织级" : graph.NameOf(s.OwnerPoolId),
                s.QuotaMb, s.StorageBytes, Status = s.Status.ToString(), s.CreatedAt,
                Permission = a.Permission.ToString(), a.Source, ViaPool = a.ViaPoolId == null ? null : graph.NameOf(a.ViaPoolId), a.Path
            });
        }
        return result;
    }

    /// <summary>某 Pool 的共享空间视图：直接授权、继承授权，以及本 Pool 拥有的空间。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <returns>直接 / 继承 / 拥有 三组。</returns>
    public async Task<(List<object> Direct, List<object> Inherited, List<object> Owned)> ForPoolAsync(string poolId)
    {
        var graph = await engine.GetGraphAsync();
        var map = await engine.GetPoolSharedSpacesAsync(poolId);
        var spaces = await db.SharedSpaces.AsNoTracking().Where(s => s.Status == SharedSpaceStatus.Active).ToDictionaryAsync(s => s.Id);
        object View(PoolSpaceAccess p) => new
        {
            Id = p.SharedSpaceId, spaces[p.SharedSpaceId].Name, spaces[p.SharedSpaceId].Description, spaces[p.SharedSpaceId].MountName, spaces[p.SharedSpaceId].StorageBytes,
            Permission = p.Permission?.ToString(), p.Denied, Source = p.Source, ViaPoolId = p.ViaPoolId, ViaPool = graph.NameOf(p.ViaPoolId), p.Path, p.GrantId
        };
        var all = map.Values.Where(p => spaces.ContainsKey(p.SharedSpaceId)).ToList();
        var owned = spaces.Values.Where(s => s.OwnerPoolId == poolId).Select(s => (object)new { s.Id, s.Name, s.Description, s.MountName, s.StorageBytes, s.QuotaMb }).ToList();
        return (all.Where(p => p.Source == "direct").Select(View).ToList(), all.Where(p => p.Source == "inherited").Select(View).ToList(), owned);
    }

    // ─────────────────────────── 授权 ───────────────────────────

    /// <summary>列出授权（含主体名称）。</summary>
    /// <param name="spaceId">共享空间 ID。</param>
    /// <returns>授权视图。</returns>
    public async Task<List<object>> ListGrantsAsync(string spaceId)
    {
        var grants = await db.SharedSpaceGrants.AsNoTracking().Where(g => g.SharedSpaceId == spaceId).OrderBy(g => g.CreatedAt).ToListAsync();
        var pools = await db.Pools.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName + "（" + u.Username + "）");
        return grants.Select(g => (object)new
        {
            g.Id, SubjectType = g.SubjectType.ToString(), g.SubjectId,
            SubjectName = g.SubjectType == GrantSubjectType.POOL ? pools.GetValueOrDefault(g.SubjectId, g.SubjectId) : users.GetValueOrDefault(g.SubjectId, g.SubjectId),
            Permission = g.Permission.ToString(), Effect = g.Effect.ToString(), g.InheritToChildren, g.CreatedAt
        }).ToList();
    }

    /// <summary>新增 / 更新授权（需 MANAGE）。同一主体同一效果只保留一条。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="spaceId">共享空间 ID。</param>
    /// <param name="type">主体类型。</param>
    /// <param name="subjectId">主体 ID。</param>
    /// <param name="permission">权限级别。</param>
    /// <param name="effect">允许 / 显式拒绝。</param>
    /// <param name="inherit">是否继承给子 Pool。</param>
    /// <returns>授权记录。</returns>
    public async Task<SharedSpaceGrant> UpsertGrantAsync(Actor actor, string spaceId, GrantSubjectType type, string subjectId, SharePermission permission, GrantEffect effect, bool inherit)
    {
        var (space, _) = await RequireAsync(actor, spaceId, SharePermission.MANAGE);
        if (type == GrantSubjectType.POOL)
        {
            if (!await db.Pools.AnyAsync(p => p.Id == subjectId && p.Status == PoolStatus.Active)) throw ApiException.NotFound("授权的 Pool 不存在");
        }
        else if (!await db.Users.AnyAsync(u => u.Id == subjectId && u.Status == UserStatus.Active)) throw ApiException.NotFound("授权的用户不存在");
        var g = await db.SharedSpaceGrants.FirstOrDefaultAsync(x => x.SharedSpaceId == space.Id && x.SubjectType == type && x.SubjectId == subjectId && x.Effect == effect);
        if (g == null) { g = new SharedSpaceGrant { Id = IdGen.New("grt"), SharedSpaceId = space.Id, SubjectType = type, SubjectId = subjectId, Effect = effect, CreatedBy = actor.UserId }; db.SharedSpaceGrants.Add(g); }
        g.Permission = permission; g.InheritToChildren = type == GrantSubjectType.POOL && inherit;
        await db.SaveChangesAsync();
        pc.Invalidate();
        return g;
    }

    /// <summary>删除授权（需 MANAGE）。已挂载的 Workspace 会因失去授权而自动失效（挂载记录保留但不生效）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="spaceId">共享空间 ID。</param>
    /// <param name="grantId">授权 ID。</param>
    public async Task<SharedSpaceGrant> DeleteGrantAsync(Actor actor, string spaceId, string grantId)
    {
        var (space, _) = await RequireAsync(actor, spaceId, SharePermission.MANAGE);
        var g = await db.SharedSpaceGrants.FirstOrDefaultAsync(x => x.Id == grantId && x.SharedSpaceId == space.Id) ?? throw ApiException.NotFound("授权不存在");
        db.SharedSpaceGrants.Remove(g);
        await db.SaveChangesAsync();
        pc.Invalidate();
        return g;
    }

    /// <summary>刷新共享空间占用统计。</summary>
    /// <param name="space">共享空间。</param>
    public async Task RefreshStorageAsync(SharedSpace space)
    {
        space.StorageBytes = await Task.Run(() => FileStore.DirSize(paths.SharedSpace(space.Id)));
        await db.SaveChangesAsync();
    }

    /// <summary>写入前检查共享空间配额。</summary>
    /// <param name="space">共享空间。</param>
    /// <param name="addBytes">新增字节数。</param>
    public async Task EnsureQuotaAsync(SharedSpace space, long addBytes)
    {
        if (space.QuotaMb <= 0) return;
        var used = await Task.Run(() => FileStore.DirSize(paths.SharedSpace(space.Id)));
        if (used + addBytes > (long)space.QuotaMb * 1024 * 1024) throw ApiException.Quota($"共享空间配额已满（{space.QuotaMb} MB）");
    }

    /// <summary>由名称生成挂载名。</summary>
    /// <param name="name">名称。</param>
    private static string Slug(string name)
    {
        var s = new string(name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length == 0 ? "shared-" + IdGen.New("s")[2..8] : s.Length > 40 ? s[..40] : s;
    }
}
