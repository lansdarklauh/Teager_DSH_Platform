using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>Workspace 列表项（含当前用户的有效角色）。</summary>
/// <param name="Workspace">Workspace 实体。</param>
/// <param name="Access">当前用户的访问结果。</param>
/// <param name="CanManage">是否可管理元数据。</param>
public record WorkspaceView(Workspace Workspace, WorkspaceAccess Access, bool CanManage);

/// <summary>Workspace 服务：创建 / 修改 / 成员 / 共享空间挂载 / 快照 / 克隆 / 删除与归档。</summary>
public partial class WorkspaceService(DshDbContext db, PermissionEngine engine, PermissionCache pc, StoragePaths paths, PolicyService policy, IOptions<DshOptions> opt)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,47}$")]
    private static partial Regex MountNameRegex();

    /// <summary>创建 Workspace：Pool 成员（Member 及以上）或 Pool 管理者可创建，创建者成为 Workspace Owner；自动挂载 Pool 策略中的默认共享空间。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">所属 Pool。</param>
    /// <param name="name">名称。</param>
    /// <param name="description">描述。</param>
    /// <param name="inheritPoolMembers">是否继承 Pool 有效成员权限。</param>
    /// <returns>新 Workspace。</returns>
    public async Task<Workspace> CreateAsync(Actor actor, string poolId, string name, string? description, bool inheritPoolMembers = true)
    {
        var access = await engine.RequirePoolViewAsync(actor, poolId);
        if (!(access.CanManage || access.DirectRole >= PoolRole.Member)) throw ApiException.Forbidden("需要该 Pool 的成员（Member 及以上）权限才能创建 Workspace");
        var pool = await db.Pools.FirstOrDefaultAsync(p => p.Id == poolId && p.Status == PoolStatus.Active) ?? throw ApiException.NotFound("Pool 不存在");
        name = RequireName(name);
        if (await db.Workspaces.AnyAsync(w => w.PoolId == poolId && w.Name == name && w.Status == WorkspaceStatus.Active)) throw ApiException.Conflict("该 Pool 下已存在同名 Workspace");
        await policy.EnsureCanCreateWorkspaceAsync(poolId);

        var ws = new Workspace { Id = IdGen.New("ws"), PoolId = poolId, Name = name, Description = description, InheritPoolMembers = inheritPoolMembers, CreatedBy = actor.UserId };
        db.Workspaces.Add(ws);
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = IdGen.New("wm"), WorkspaceId = ws.Id, UserId = actor.UserId, Role = WorkspaceRole.Owner });
        // 默认共享空间：策略链上声明且当前 Pool 确有授权的才挂载
        var eff = await policy.GetEffectiveAsync(poolId);
        var available = await engine.GetPoolSharedSpacesAsync(poolId);
        foreach (var sid in eff.DefaultSharedSpaceIds)
        {
            if (!available.TryGetValue(sid, out var psa) || psa.Denied) continue;
            var space = await db.SharedSpaces.FirstOrDefaultAsync(s => s.Id == sid && s.Status == SharedSpaceStatus.Active);
            if (space == null) continue;
            db.WorkspaceSharedSpaceMounts.Add(new WorkspaceSharedSpaceMount { Id = IdGen.New("mnt"), WorkspaceId = ws.Id, SharedSpaceId = sid, MountName = await UniqueMountNameAsync(ws.Id, space.MountName) });
        }
        await db.SaveChangesAsync();
        Directory.CreateDirectory(paths.WorkspaceFiles(ws.Id));
        Directory.CreateDirectory(paths.WorkspaceShared(ws.Id));
        pc.Invalidate();
        return ws;
    }

    /// <summary>更新 Workspace 基本信息（需管理权）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">Workspace ID。</param>
    /// <param name="name">新名称。</param>
    /// <param name="description">新描述。</param>
    /// <param name="inheritPoolMembers">是否继承 Pool 成员。</param>
    /// <returns>更新后的 Workspace。</returns>
    public async Task<Workspace> UpdateAsync(Actor actor, string id, string? name, string? description, bool? inheritPoolMembers)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, id, null, manage: true);
        if (name != null)
        {
            name = RequireName(name);
            if (await db.Workspaces.AnyAsync(w => w.Id != id && w.PoolId == ws.PoolId && w.Name == name && w.Status == WorkspaceStatus.Active)) throw ApiException.Conflict("该 Pool 下已存在同名 Workspace");
            ws.Name = name;
        }
        if (description != null) ws.Description = description;
        if (inheritPoolMembers != null) ws.InheritPoolMembers = inheritPoolMembers.Value;
        ws.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        pc.Invalidate();
        return ws;
    }

    /// <summary>列出当前用户可访问（或可管理）的 Workspace。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="poolId">按 Pool 过滤（可空）。</param>
    /// <param name="includeArchived">是否包含已归档。</param>
    /// <param name="includeDescendants">按 Pool 过滤时是否包含子孙 Pool 的 Workspace。</param>
    /// <returns>Workspace 视图列表。</returns>
    public async Task<List<WorkspaceView>> ListAsync(Actor actor, string? poolId, bool includeArchived, bool includeDescendants = false)
    {
        var graph = await engine.GetGraphAsync();
        var activePoolIds = graph.Nodes.Keys.ToList();
        var q = db.Workspaces.AsNoTracking().Where(w => activePoolIds.Contains(w.PoolId));
        if (!includeArchived) q = q.Where(w => w.Status == WorkspaceStatus.Active);
        if (poolId != null)
        {
            var ids = new List<string> { poolId };
            if (includeDescendants) ids.AddRange(graph.DescendantsOf(poolId));
            q = q.Where(w => ids.Contains(w.PoolId));
        }
        var candidates = await q.OrderBy(w => w.Name).ToListAsync();
        var mine = await db.WorkspaceMembers.AsNoTracking().Where(m => m.UserId == actor.UserId).ToDictionaryAsync(m => m.WorkspaceId);
        var result = new List<WorkspaceView>();
        foreach (var w in candidates)
        {
            var wa = await engine.GetWorkspaceAccessAsync(actor, w, mine);
            if (wa.Denied) continue;
            var canManage = await engine.CanManageWorkspaceAsync(actor, w, wa);
            if (wa.Role == null && !canManage) continue;
            result.Add(new WorkspaceView(w, wa, canManage));
        }
        return result;
    }

    // ─────────────────────────── 成员 ───────────────────────────

    /// <summary>列出显式成员（含显式拒绝）以及有效访问的来源说明。</summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <returns>成员列表。</returns>
    public async Task<List<object>> ListMembersAsync(string workspaceId)
    {
        var rows = await (from m in db.WorkspaceMembers.AsNoTracking() where m.WorkspaceId == workspaceId
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                          orderby m.Effect, m.Role descending, u.Username
                          select new { m.Id, m.UserId, u.Username, u.DisplayName, Role = m.Role.ToString(), Effect = m.Effect.ToString(), m.CreatedAt }).ToListAsync();
        return rows.Cast<object>().ToList();
    }

    /// <summary>添加 / 更新显式成员或显式拒绝（需管理权）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="userId">用户 ID。</param>
    /// <param name="role">角色。</param>
    /// <param name="effect">Allow / Deny。</param>
    public async Task UpsertMemberAsync(Actor actor, string workspaceId, string userId, WorkspaceRole role, GrantEffect effect)
    {
        var (ws, wa) = await engine.RequireWorkspaceAsync(actor, workspaceId, null, manage: true);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active) ?? throw ApiException.NotFound("用户不存在或已禁用");
        var row = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ws.Id && m.UserId == userId);
        if ((role == WorkspaceRole.Owner || row?.Role == WorkspaceRole.Owner) && !actor.IsPlatformAdmin && wa.Role != WorkspaceRole.Owner && !(await engine.GetPoolAccessAsync(actor, ws.PoolId)).CanManage)
            throw ApiException.Forbidden("只有 Owner 或 Pool 管理者可以调整 Owner 角色");
        if (row == null) { row = new WorkspaceMember { Id = IdGen.New("wm"), WorkspaceId = ws.Id, UserId = user.Id }; db.WorkspaceMembers.Add(row); }
        row.Role = role; row.Effect = effect;
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    /// <summary>移除显式成员 / 显式拒绝记录（需管理权，不能移除最后一个 Owner）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="userId">用户 ID。</param>
    public async Task RemoveMemberAsync(Actor actor, string workspaceId, string userId)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, null, manage: true);
        var row = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == ws.Id && m.UserId == userId) ?? throw ApiException.NotFound("成员记录不存在");
        if (row.Role == WorkspaceRole.Owner && row.Effect == GrantEffect.Allow && !actor.IsPlatformAdmin
            && await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == ws.Id && m.Role == WorkspaceRole.Owner && m.Effect == GrantEffect.Allow) <= 1
            && !(await engine.GetPoolAccessAsync(actor, ws.PoolId)).CanManage)
            throw ApiException.BadRequest("不能移除最后一个 Owner");
        db.WorkspaceMembers.Remove(row);
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    // ─────────────────────────── 共享空间挂载 ───────────────────────────

    /// <summary>列出 Workspace 已配置的挂载及当前用户的有效权限；同时返回 Pool 已授权但尚未挂载的共享空间供选择。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="wa">用户访问结果。</param>
    /// <returns>挂载列表与可用候选。</returns>
    public async Task<(List<object> Mounts, List<object> Available)> ListMountsAsync(Actor actor, Workspace ws, WorkspaceAccess wa)
    {
        var mounts = await db.WorkspaceSharedSpaceMounts.AsNoTracking().Where(m => m.WorkspaceId == ws.Id).ToListAsync();
        var spaces = await db.SharedSpaces.AsNoTracking().ToDictionaryAsync(s => s.Id);
        var poolSpaces = await engine.GetPoolSharedSpacesAsync(ws.PoolId);
        var graph = await engine.GetGraphAsync();
        var mountList = new List<object>();
        foreach (var m in mounts)
        {
            if (!spaces.TryGetValue(m.SharedSpaceId, out var s)) continue;
            var eff = await engine.GetWorkspaceSpaceAccessAsync(actor, ws, wa, s, m);
            mountList.Add(new
            {
                m.Id, m.SharedSpaceId, SharedSpaceName = s.Name, m.MountName, m.Enabled,
                PermissionOverride = m.PermissionOverride?.ToString(), EffectivePermission = eff.Permission?.ToString(), Source = eff.Source,
                ViaPool = eff.ViaPoolId == null ? null : graph.NameOf(eff.ViaPoolId), eff.Path, eff.Notes,
                GrantedPermission = poolSpaces.TryGetValue(s.Id, out var ps) && !ps.Denied ? ps.Permission?.ToString() : null,
                Active = eff.Permission != null && m.Enabled
            });
        }
        var mountedIds = mounts.Select(m => m.SharedSpaceId).ToHashSet();
        var available = poolSpaces.Values.Where(p => !p.Denied && !mountedIds.Contains(p.SharedSpaceId) && spaces.ContainsKey(p.SharedSpaceId))
            .Select(p => (object)new { SharedSpaceId = p.SharedSpaceId, Name = spaces[p.SharedSpaceId].Name, DefaultMountName = spaces[p.SharedSpaceId].MountName, Permission = p.Permission?.ToString(), p.Source, ViaPool = graph.NameOf(p.ViaPoolId), p.Path }).ToList();
        return (mountList, available);
    }

    /// <summary>挂载共享空间到 Workspace（需管理权；共享空间必须已授权给所属 Pool；覆盖权限只能降低）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="sharedSpaceId">共享空间 ID。</param>
    /// <param name="mountName">挂载名（空则使用共享空间默认挂载名）。</param>
    /// <param name="overridePerm">权限覆盖（RO/RW），只能低于授权权限。</param>
    public async Task AddMountAsync(Actor actor, string workspaceId, string sharedSpaceId, string? mountName, SharePermission? overridePerm)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, null, manage: true);
        var space = await db.SharedSpaces.FirstOrDefaultAsync(s => s.Id == sharedSpaceId && s.Status == SharedSpaceStatus.Active) ?? throw ApiException.NotFound("共享空间不存在");
        var poolSpaces = await engine.GetPoolSharedSpacesAsync(ws.PoolId);
        if (!poolSpaces.TryGetValue(sharedSpaceId, out var psa) || psa.Denied) throw ApiException.Forbidden("该 Workspace 所属 Pool 未获得此共享空间的授权（权限 ≠ 挂载，必须先授权）");
        if (overridePerm != null && overridePerm > psa.Permission) throw ApiException.BadRequest($"挂载权限覆盖只能降低，不能超过 Pool 的授权权限（{psa.Permission}）");
        if (overridePerm == SharePermission.MANAGE) throw ApiException.BadRequest("挂载覆盖仅支持 RO / RW");
        if (await db.WorkspaceSharedSpaceMounts.AnyAsync(m => m.WorkspaceId == ws.Id && m.SharedSpaceId == sharedSpaceId)) throw ApiException.Conflict("该共享空间已挂载");
        var mn = string.IsNullOrWhiteSpace(mountName) ? space.MountName : mountName.Trim().ToLowerInvariant();
        if (!MountNameRegex().IsMatch(mn)) throw ApiException.BadRequest("挂载名只能包含小写字母、数字、点、下划线、连字符，且以字母或数字开头（最长 48）");
        if (await db.WorkspaceSharedSpaceMounts.AnyAsync(m => m.WorkspaceId == ws.Id && m.MountName == mn)) throw ApiException.Conflict("挂载名已被占用");
        db.WorkspaceSharedSpaceMounts.Add(new WorkspaceSharedSpaceMount { Id = IdGen.New("mnt"), WorkspaceId = ws.Id, SharedSpaceId = sharedSpaceId, MountName = mn, PermissionOverride = overridePerm });
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    /// <summary>更新挂载：启用 / 停用、权限覆盖（仅降低）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="mountId">挂载 ID。</param>
    /// <param name="enabled">是否启用。</param>
    /// <param name="overridePerm">权限覆盖。</param>
    /// <param name="clearOverride">是否清除覆盖。</param>
    public async Task UpdateMountAsync(Actor actor, string workspaceId, string mountId, bool? enabled, SharePermission? overridePerm, bool clearOverride)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, null, manage: true);
        var m = await db.WorkspaceSharedSpaceMounts.FirstOrDefaultAsync(x => x.Id == mountId && x.WorkspaceId == ws.Id) ?? throw ApiException.NotFound("挂载不存在");
        if (enabled != null) m.Enabled = enabled.Value;
        if (clearOverride) m.PermissionOverride = null;
        if (overridePerm != null)
        {
            var poolSpaces = await engine.GetPoolSharedSpacesAsync(ws.PoolId);
            if (!poolSpaces.TryGetValue(m.SharedSpaceId, out var psa) || psa.Denied) throw ApiException.Forbidden("Pool 已无该共享空间的授权");
            if (overridePerm > psa.Permission) throw ApiException.BadRequest($"挂载权限覆盖只能降低，不能超过 Pool 的授权权限（{psa.Permission}）");
            if (overridePerm == SharePermission.MANAGE) throw ApiException.BadRequest("挂载覆盖仅支持 RO / RW");
            m.PermissionOverride = overridePerm;
        }
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    /// <summary>移除挂载。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="mountId">挂载 ID。</param>
    public async Task RemoveMountAsync(Actor actor, string workspaceId, string mountId)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, null, manage: true);
        var m = await db.WorkspaceSharedSpaceMounts.FirstOrDefaultAsync(x => x.Id == mountId && x.WorkspaceId == ws.Id) ?? throw ApiException.NotFound("挂载不存在");
        db.WorkspaceSharedSpaceMounts.Remove(m);
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    /// <summary>生成不冲突的挂载名。</summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="baseName">期望名称。</param>
    private async Task<string> UniqueMountNameAsync(string workspaceId, string baseName)
    {
        var name = baseName; var i = 2;
        while (db.WorkspaceSharedSpaceMounts.Local.Any(m => m.WorkspaceId == workspaceId && m.MountName == name) || await db.WorkspaceSharedSpaceMounts.AnyAsync(m => m.WorkspaceId == workspaceId && m.MountName == name))
            name = $"{baseName}-{i++}";
        return name;
    }

    // ─────────────────────────── 删除 / 归档 / 恢复 / 转移 ───────────────────────────

    /// <summary>删除 Workspace。mode：runtime_context 仅清理运行上下文（会话与运行环境，保留文件）；archive 归档（默认，保留期后清理）；permanent 立即永久删除。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">Workspace ID。</param>
    /// <param name="mode">删除模式。</param>
    public async Task DeleteAsync(Actor actor, string id, string mode)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, id, null, manage: true);
        if (await db.AgentRuns.AnyAsync(r => r.WorkspaceId == id && r.Status == RunStatus.Running)) throw ApiException.Conflict("该 Workspace 仍有运行中的任务，请先取消");
        switch (mode)
        {
            case "runtime_context": await ClearRuntimeContextAsync(ws); break;
            case "archive": await ArchiveInternalAsync(ws); await db.SaveChangesAsync(); break;
            case "permanent": await PermanentDeleteAsync(ws); break;
            default: throw ApiException.BadRequest("mode 取值：runtime_context / archive / permanent");
        }
        pc.Invalidate();
    }

    /// <summary>归档（内部使用，不检查权限，调用方负责 SaveChanges）。</summary>
    /// <param name="ws">Workspace。</param>
    public Task ArchiveInternalAsync(Workspace ws)
    {
        ws.Status = WorkspaceStatus.Archived; ws.ArchivedAt = DateTime.Now;
        ws.PurgeAfter = DateTime.Now.AddDays(opt.Value.Platform.ArchiveRetentionDays); ws.UpdatedAt = DateTime.Now;
        return Task.CompletedTask;
    }

    /// <summary>恢复已归档的 Workspace（保留期内）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">Workspace ID。</param>
    public async Task RestoreAsync(Actor actor, string id)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id && w.Status == WorkspaceStatus.Archived) ?? throw ApiException.NotFound("归档的 Workspace 不存在");
        await engine.RequirePoolManageAsync(actor, ws.PoolId);
        if (await db.Workspaces.AnyAsync(w => w.PoolId == ws.PoolId && w.Name == ws.Name && w.Status == WorkspaceStatus.Active)) ws.Name += "-已恢复";
        ws.Status = WorkspaceStatus.Active; ws.ArchivedAt = null; ws.PurgeAfter = null; ws.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        pc.Invalidate();
    }

    /// <summary>把 Workspace 转移到另一个 Pool：会话的 Runtime 上下文重置（不同 Pool 的 DSH_HOME 相互独立），不再可用的挂载被移除。</summary>
    /// <param name="ws">Workspace。</param>
    /// <param name="newPoolId">目标 Pool。</param>
    public async Task TransferAsync(Workspace ws, string newPoolId)
    {
        ws.PoolId = newPoolId; ws.UpdatedAt = DateTime.Now;
        if (await db.Workspaces.AnyAsync(w => w.Id != ws.Id && w.PoolId == newPoolId && w.Name == ws.Name && w.Status == WorkspaceStatus.Active)) ws.Name += "-转入";
        foreach (var s in await db.Sessions.Where(s => s.WorkspaceId == ws.Id).ToListAsync()) { s.PoolId = newPoolId; s.RuntimeSessionId = null; }
        db.WorkspaceSharedSpaceMounts.RemoveRange(await db.WorkspaceSharedSpaceMounts.Where(m => m.WorkspaceId == ws.Id).ToListAsync());
        await db.SaveChangesAsync();
    }

    /// <summary>清理运行上下文：删除会话、消息、运行记录与事件日志，并移除挂载链接；保留文件与成员。</summary>
    /// <param name="ws">Workspace。</param>
    private async Task ClearRuntimeContextAsync(Workspace ws)
    {
        var sessions = await db.Sessions.Where(s => s.WorkspaceId == ws.Id).ToListAsync();
        await DeleteSessionDataAsync(sessions);
        LinkHelper.RemoveLinks(paths.WorkspaceShared(ws.Id));
        var dsh = Path.Combine(paths.WorkspaceRoot(ws.Id), ".dsh");
        if (Directory.Exists(dsh)) Directory.Delete(dsh, true);
        await db.SaveChangesAsync();
    }

    /// <summary>永久删除：数据库记录、文件、快照、事件日志全部清除（共享空间数据不受影响）。</summary>
    /// <param name="ws">Workspace。</param>
    public async Task PermanentDeleteAsync(Workspace ws)
    {
        var sessions = await db.Sessions.Where(s => s.WorkspaceId == ws.Id).ToListAsync();
        await DeleteSessionDataAsync(sessions);
        // 先移除共享挂载链接，防止递归删除误伤共享空间真实数据
        LinkHelper.RemoveLinks(paths.WorkspaceShared(ws.Id));
        var root = paths.WorkspaceRoot(ws.Id);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        foreach (var snap in await db.WorkspaceSnapshots.Where(s => s.WorkspaceId == ws.Id).ToListAsync())
        {
            var f = paths.SnapshotFile(snap.Id); if (File.Exists(f)) File.Delete(f);
            db.WorkspaceSnapshots.Remove(snap);
        }
        db.WorkspaceMembers.RemoveRange(await db.WorkspaceMembers.Where(m => m.WorkspaceId == ws.Id).ToListAsync());
        db.WorkspaceSharedSpaceMounts.RemoveRange(await db.WorkspaceSharedSpaceMounts.Where(m => m.WorkspaceId == ws.Id).ToListAsync());
        db.ConfigItems.RemoveRange(await db.ConfigItems.Where(c => c.Scope == ConfigScope.Workspace && c.ScopeId == ws.Id).ToListAsync());
        db.Secrets.RemoveRange(await db.Secrets.Where(c => c.Scope == ConfigScope.Workspace && c.ScopeId == ws.Id).ToListAsync());
        db.Workspaces.Remove(ws);
        await db.SaveChangesAsync();
    }

    /// <summary>删除会话相关的数据库记录与事件日志文件。</summary>
    /// <param name="sessions">会话列表。</param>
    public async Task DeleteSessionDataAsync(List<Session> sessions)
    {
        foreach (var s in sessions)
        {
            var log = paths.RunEventLog(s.Id, s.CreatedAt);
            if (File.Exists(log)) File.Delete(log);
            db.SessionMessages.RemoveRange(await db.SessionMessages.Where(m => m.SessionId == s.Id).ToListAsync());
            db.AgentRuns.RemoveRange(await db.AgentRuns.Where(r => r.SessionId == s.Id).ToListAsync());
            db.Sessions.Remove(s);
        }
    }

    /// <summary>清理超过保留期的归档 Workspace（维护任务调用）。</summary>
    /// <returns>清理数量。</returns>
    public async Task<int> PurgeExpiredAsync()
    {
        var expired = await db.Workspaces.Where(w => w.Status == WorkspaceStatus.Archived && w.PurgeAfter != null && w.PurgeAfter < DateTime.Now).ToListAsync();
        foreach (var w in expired) await PermanentDeleteAsync(w);
        return expired.Count;
    }

    // ─────────────────────────── 存储 / 快照 / 克隆 ───────────────────────────

    /// <summary>重新统计并更新 Workspace 私有文件占用。</summary>
    /// <param name="ws">Workspace。</param>
    /// <returns>占用字节数。</returns>
    public async Task<long> RefreshStorageAsync(Workspace ws)
    {
        ws.StorageBytes = await Task.Run(() => FileStore.DirSize(paths.WorkspaceFiles(ws.Id)));
        await db.SaveChangesAsync();
        return ws.StorageBytes;
    }

    /// <summary>写入前检查 Workspace 私有文件配额。</summary>
    /// <param name="ws">Workspace。</param>
    /// <param name="addBytes">即将新增的字节数。</param>
    public async Task EnsureStorageQuotaAsync(Workspace ws, long addBytes)
    {
        var eff = await policy.GetEffectiveAsync(ws.PoolId);
        if (eff.MaxStorageMbPerWorkspace <= 0) return;
        var used = await Task.Run(() => FileStore.DirSize(paths.WorkspaceFiles(ws.Id)));
        if (used + addBytes > (long)eff.MaxStorageMbPerWorkspace * 1024 * 1024) throw ApiException.Quota($"Workspace 存储配额已满（{eff.MaxStorageMbPerWorkspace} MB）");
    }

    /// <summary>创建快照：把私有文件打包为 zip 存放在数据卷。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="name">快照名称。</param>
    /// <param name="description">备注。</param>
    /// <returns>快照记录。</returns>
    public async Task<WorkspaceSnapshot> CreateSnapshotAsync(Actor actor, string workspaceId, string? name, string? description)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Member);
        var snap = new WorkspaceSnapshot { Id = IdGen.New("snap"), WorkspaceId = ws.Id, Name = string.IsNullOrWhiteSpace(name) ? "快照-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") : name.Trim(), Description = description, CreatedBy = actor.UserId };
        var file = paths.SnapshotFile(snap.Id);
        await Task.Run(() => FileStore.ZipDirectory(paths.WorkspaceFiles(ws.Id), file));
        snap.SizeBytes = new FileInfo(file).Length;
        snap.FilePath = Path.GetRelativePath(paths.Data, file).Replace('\\', '/');
        db.WorkspaceSnapshots.Add(snap);
        await db.SaveChangesAsync();
        return snap;
    }

    /// <summary>从快照恢复：先清空私有文件目录，再解压快照内容。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="snapshotId">快照 ID。</param>
    public async Task RestoreSnapshotAsync(Actor actor, string workspaceId, string snapshotId)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Admin);
        var snap = await db.WorkspaceSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId && s.WorkspaceId == ws.Id) ?? throw ApiException.NotFound("快照不存在");
        if (await db.AgentRuns.AnyAsync(r => r.WorkspaceId == ws.Id && r.Status == RunStatus.Running)) throw ApiException.Conflict("有运行中的任务，无法恢复快照");
        var file = paths.SnapshotFile(snap.Id);
        if (!File.Exists(file)) throw ApiException.NotFound("快照文件已丢失");
        var files = paths.WorkspaceFiles(ws.Id);
        await Task.Run(() => { if (Directory.Exists(files)) Directory.Delete(files, true); FileStore.ExtractZip(file, files); });
        await RefreshStorageAsync(ws);
    }

    /// <summary>删除快照。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="snapshotId">快照 ID。</param>
    public async Task DeleteSnapshotAsync(Actor actor, string workspaceId, string snapshotId)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Admin);
        var snap = await db.WorkspaceSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId && s.WorkspaceId == ws.Id) ?? throw ApiException.NotFound("快照不存在");
        var f = paths.SnapshotFile(snap.Id); if (File.Exists(f)) File.Delete(f);
        db.WorkspaceSnapshots.Remove(snap);
        await db.SaveChangesAsync();
    }

    /// <summary>克隆 Workspace：复制私有文件与共享挂载配置（不复制会话与成员，克隆人成为 Owner）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="sourceId">源 Workspace。</param>
    /// <param name="name">新名称。</param>
    /// <returns>新 Workspace。</returns>
    public async Task<Workspace> CloneAsync(Actor actor, string sourceId, string? name)
    {
        var (src, _) = await engine.RequireWorkspaceAsync(actor, sourceId, WorkspaceRole.Member);
        var srcBytes = await Task.Run(() => FileStore.DirSize(paths.WorkspaceFiles(src.Id)));
        var newName = string.IsNullOrWhiteSpace(name) ? src.Name + " (副本)" : name;
        var clone = await CreateAsync(actor, src.PoolId, newName, src.Description, src.InheritPoolMembers);
        await EnsureStorageQuotaAsync(clone, srcBytes);
        await Task.Run(() => FileStore.CopyDirectory(paths.WorkspaceFiles(src.Id), paths.WorkspaceFiles(clone.Id)));
        // 复制挂载配置（默认共享空间已在创建时挂载，这里补齐其余）
        var have = (await db.WorkspaceSharedSpaceMounts.Where(m => m.WorkspaceId == clone.Id).Select(m => m.SharedSpaceId).ToListAsync()).ToHashSet();
        foreach (var m in await db.WorkspaceSharedSpaceMounts.AsNoTracking().Where(m => m.WorkspaceId == src.Id).ToListAsync())
        {
            if (have.Contains(m.SharedSpaceId)) continue;
            db.WorkspaceSharedSpaceMounts.Add(new WorkspaceSharedSpaceMount { Id = IdGen.New("mnt"), WorkspaceId = clone.Id, SharedSpaceId = m.SharedSpaceId, MountName = await UniqueMountNameAsync(clone.Id, m.MountName), Enabled = m.Enabled, PermissionOverride = m.PermissionOverride });
        }
        await db.SaveChangesAsync();
        pc.Invalidate();
        await RefreshStorageAsync(clone);
        return clone;
    }

    /// <summary>校验并规范化名称。</summary>
    /// <param name="name">名称。</param>
    private static string RequireName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw ApiException.BadRequest("名称不能为空");
        if (name.Length > 100) throw ApiException.BadRequest("名称不能超过 100 个字符");
        return name;
    }
}
