using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>可信的个人空间分配；不使用用户历史 Pool/Owner 权限推断归属。</summary>
public class PersonalWorkspaceService(DshDbContext db, StoragePaths paths, PermissionCache cache, IOptions<DshOptions> options, PolicyService policy)
{
    /// <summary>在独占实例的启动阶段同步统一引擎，旧 Mock 上下文不能交给真实引擎继续运行。</summary>
    public async Task<int> SynchronizePoolProviderAsync()
    {
        if (!options.Value.Runtime.SynchronizePersonalPoolProvider) return 0;
        var provider = options.Value.Runtime.DefaultProvider;
        if (provider is not ("DeepSeekHarness" or "Mock")) throw ApiException.BadRequest("默认执行引擎必须为 DeepSeekHarness 或 Mock");
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DatabaseAdmission.LockAsync(db);
        var ids = await db.Organizations.Select(o => "personal_" + o.Id).ToListAsync();
        var pools = await db.Pools.Where(p => ids.Contains(p.Id) && p.CreatedBy == null && p.RuntimeProvider != provider).ToListAsync();
        var changed = pools.Select(p => p.Id).ToList();
        if (await db.AgentRuns.AnyAsync(r => changed.Contains(r.PoolId) && r.Status == RunStatus.Running)) throw ApiException.Conflict("个人 Pool 仍有运行中的任务，不能切换统一执行引擎");
        foreach (var pool in pools) { pool.RuntimeProvider = provider; pool.UpdatedAt = DateTime.Now; }
        foreach (var runtime in await db.Runtimes.Where(r => changed.Contains(r.PoolId)).ToListAsync())
        {
            runtime.Provider = provider;
            if (runtime.Status != RuntimeStatus.MAINTENANCE) runtime.Status = RuntimeStatus.STOPPED;
            runtime.StartedAt = null;
        }
        foreach (var session in await db.Sessions.Where(s => changed.Contains(s.PoolId) && s.RuntimeSessionId != null).ToListAsync()) session.RuntimeSessionId = null;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        if (pools.Count > 0) cache.Invalidate();
        return pools.Count;
    }

    public async Task<Workspace?> EnsureAsync(string userId)
    {
        await using var transaction = db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null;
        await DatabaseAdmission.LockAsync(db);
        var admission = "dsh:personal-workspaces";
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sp_getapplock @Resource={admission}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, '个人空间分配锁超时', 1;");
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        if (user.IsServiceAccount || user.Role == PlatformRole.Admin) return null;
        if (user.DefaultWorkspaceId != null)
            return await db.Workspaces.FirstOrDefaultAsync(w => w.Id == user.DefaultWorkspaceId && w.AssignedUserId == userId && w.Status == WorkspaceStatus.Active);
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.AssignedUserId == userId);
        if (workspace == null)
        {
            var poolId = "personal_" + user.OrganizationId;
            if (!await db.Pools.AnyAsync(p => p.Id == poolId))
            {
                db.Pools.Add(new Pool { Id = poolId, OrganizationId = user.OrganizationId, Name = "个人工作空间", RuntimeProvider = options.Value.Runtime.DefaultProvider });
                db.PoolClosures.Add(new PoolClosure { AncestorPoolId = poolId, DescendantPoolId = poolId });
                await db.SaveChangesAsync();
            }
            if (!await db.Pools.AnyAsync(p => p.Id == poolId && p.Status == PoolStatus.Active)) throw ApiException.Conflict("个人空间 Pool 已归档，请联系管理员分配空间");
            await policy.EnsureCanCreateWorkspaceAsync(poolId);
            workspace = new Workspace { Id = "personal_" + userId, PoolId = poolId, Name = user.DisplayName[..Math.Min(user.DisplayName.Length, 90)] + "的工作台", AssignedUserId = userId, InheritPoolMembers = false };
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember { Id = IdGen.New("wm"), WorkspaceId = workspace.Id, UserId = userId, Role = WorkspaceRole.Member });
        }
        user.DefaultWorkspaceId = workspace.Id;
        await db.SaveChangesAsync();
        Directory.CreateDirectory(paths.WorkspaceFiles(workspace.Id));
        Directory.CreateDirectory(Path.Combine(paths.WorkspaceRoot(workspace.Id), "home"));
        if (transaction != null) await transaction.CommitAsync();
        cache.Invalidate();
        return workspace;
    }

    public async Task AssignAsync(Actor actor, string userId, string workspaceId)
    {
        actor.RequirePlatformAdmin();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await DatabaseAdmission.LockAsync(db);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsServiceAccount) ?? throw ApiException.NotFound("用户不存在");
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId && w.Status == WorkspaceStatus.Active) ?? throw ApiException.NotFound("工作空间不存在");
        if (!await db.Pools.AnyAsync(p => p.Id == ws.PoolId && p.Status == PoolStatus.Active)) throw ApiException.Conflict("不能分配归档 Pool 的空间");
        if (ws.AssignedUserId != null && ws.AssignedUserId != userId) throw ApiException.Conflict("该工作空间已分配给其他用户");
        var affected = await db.Workspaces.Where(w => w.AssignedUserId == userId || w.Id == workspaceId).Select(w => w.Id).ToListAsync();
        if (await db.AgentRuns.AnyAsync(r => affected.Contains(r.WorkspaceId) && r.Status == RunStatus.Running)
            || await db.Set<WorkspaceExecution>().AnyAsync(e => affected.Contains(e.WorkspaceId) && e.Status == "Running")) throw ApiException.Conflict("分配前请先停止相关工作空间的任务、终端和应用");
        foreach (var old in await db.Workspaces.Where(w => w.AssignedUserId == userId && w.Id != workspaceId).ToListAsync()) old.AssignedUserId = null;
        await db.SaveChangesAsync();
        ws.AssignedUserId = userId; ws.InheritPoolMembers = false; user.DefaultWorkspaceId = ws.Id;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        cache.Invalidate();
    }
}
