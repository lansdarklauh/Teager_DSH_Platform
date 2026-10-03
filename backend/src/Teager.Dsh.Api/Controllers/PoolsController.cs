using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Api.Controllers;

/// <summary>控制器基类：统一路由前缀与当前主体。</summary>
[ApiController, Authorize, Route("api/v1")]
public abstract class ApiBase(ActorAccessor accessor) : ControllerBase
{
    /// <summary>当前请求的操作主体。</summary>
    protected Actor Me => accessor.Require();
}

/// <summary>创建 Pool 请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Description">描述。</param>
/// <param name="ParentPoolId">父 Pool。</param>
/// <param name="RuntimeProvider">Runtime Provider。</param>
public record CreatePoolRequest(string Name, string? Description, string? ParentPoolId, string? RuntimeProvider);
/// <summary>更新 Pool 请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Description">描述。</param>
/// <param name="RuntimeProvider">Runtime Provider。</param>
/// <param name="InheritConfig">是否继承配置。</param>
public record UpdatePoolRequest(string? Name, string? Description, string? RuntimeProvider, bool? InheritConfig);
/// <summary>添加成员请求。</summary>
/// <param name="UserId">用户 ID。</param>
/// <param name="Role">角色。</param>
public record MemberRequest(string UserId, PoolRole Role);
/// <summary>角色变更请求。</summary>
/// <param name="Role">角色。</param>
public record RoleRequest(PoolRole Role);
/// <summary>移动请求。</summary>
/// <param name="NewParentId">新父 Pool（空为顶级）。</param>
public record MoveRequest(string? NewParentId);
/// <summary>维护模式请求。</summary>
/// <param name="Enabled">是否维护。</param>
public record MaintenanceRequest(bool Enabled);

/// <summary>Pool 管理：层级树、成员（直接 / 继承）、移动、删除、策略、共享空间视图、Runtime 控制。</summary>
public class PoolsController(ActorAccessor accessor, PermissionEngine engine, PoolService pools, AuditService audit, DshDbContext db, SharedSpaceService spaces,
    PolicyService policy, ConfigService config, RuntimeManager runtime, WorkspaceService workspaces) : ApiBase(accessor)
{
    /// <summary>Pool 树（扁平节点，前端按 parentPoolId 组树）。</summary>
    [HttpGet("pools"), Scope(Scopes.PoolRead)]
    public async Task<object> Tree() => await pools.GetTreeAsync(Me);

    /// <summary>创建 Pool（可指定父 Pool）。</summary>
    [HttpPost("pools"), Scope(Scopes.PoolManage)]
    public async Task<object> Create(CreatePoolRequest req)
    {
        var p = await pools.CreateAsync(Me, req.Name, req.Description, req.ParentPoolId, req.RuntimeProvider);
        await audit.LogAsync(Me, "pool.create", "Pool", p.Id, p.Id, detail: $"创建 Pool「{p.Name}」，父级：{req.ParentPoolId ?? "无（顶级）"}");
        return new { p.Id, p.Name };
    }

    /// <summary>Pool 详情：基本信息、我的访问来源、概览统计、策略。</summary>
    [HttpGet("pools/{id}"), Scope(Scopes.PoolRead)]
    public async Task<object> Get(string id)
    {
        var access = await engine.RequirePoolViewAsync(Me, id);
        var p = await pools.GetActiveAsync(id);
        var graph = await engine.GetGraphAsync();
        var (direct, inherited) = await pools.ListMembersAsync(id);
        var (sd, si, so) = await spaces.ForPoolAsync(id);
        var rt = await db.Runtimes.AsNoTracking().FirstOrDefaultAsync(r => r.PoolId == id);
        var descIds = graph.DescendantsOf(id).Append(id).ToList();
        var wsCount = await db.Workspaces.CountAsync(w => w.PoolId == id && w.Status == WorkspaceStatus.Active);
        var subWsCount = await db.Workspaces.CountAsync(w => descIds.Contains(w.PoolId) && w.PoolId != id && w.Status == WorkspaceStatus.Active);
        var storage = await db.Workspaces.Where(w => w.PoolId == id && w.Status == WorkspaceStatus.Active).SumAsync(w => (long?)w.StorageBytes) ?? 0;
        var eff = await policy.GetEffectiveAsync(id);
        var today = DateTime.Today;
        var todayRuns = await db.AgentRuns.CountAsync(r => r.PoolId == id && r.StartedAt >= today);
        return new
        {
            p.Id, p.Name, p.Description, p.ParentPoolId, ParentName = p.ParentPoolId == null ? null : graph.NameOf(p.ParentPoolId), p.Depth, p.RuntimeProvider, p.InheritConfig, p.CreatedAt, p.UpdatedAt,
            Ancestors = graph.AncestorsOf(id).Reverse().Select(a => new { Id = a, Name = graph.NameOf(a) }),
            My = new { access.CanManage, DirectRole = access.DirectRole?.ToString(), access.Source, ViaPool = access.ViaPoolId == null ? null : graph.NameOf(access.ViaPoolId) },
            Stats = new { Workspaces = wsCount, SubPoolWorkspaces = subWsCount, Children = graph.Nodes.Values.Count(n => n.ParentId == id), DirectMembers = direct.Count, InheritedMembers = inherited.Count,
                SharedDirect = sd.Count, SharedInherited = si.Count, SharedOwned = so.Count, StorageBytes = storage, TodayRuns = todayRuns },
            Runtime = rt == null ? null : new { Status = rt.Status.ToString(), rt.Provider, rt.Version, rt.StartedAt, rt.LastActiveAt },
            Policy = new { Own = PolicyService.Parse(p.PolicyJson), Effective = eff }
        };
    }

    /// <summary>更新 Pool。</summary>
    [HttpPatch("pools/{id}"), Scope(Scopes.PoolManage)]
    public async Task<object> Update(string id, UpdatePoolRequest req)
    {
        var p = await pools.UpdateAsync(Me, id, req.Name, req.Description, req.RuntimeProvider, req.InheritConfig);
        await audit.LogAsync(Me, "pool.update", "Pool", id, id, detail: $"更新 Pool「{p.Name}」");
        return new { p.Id, p.Name };
    }

    /// <summary>删除（归档）Pool：子 Pool / Workspace 按选项处理，共享空间默认保留。</summary>
    [HttpDelete("pools/{id}"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> Delete(string id, [FromQuery] string? childMode, [FromQuery] string? workspaceMode, [FromQuery] string? transferTo)
    {
        var name = (await pools.GetActiveAsync(id)).Name;
        await pools.DeleteAsync(Me, id, childMode ?? "", workspaceMode ?? "", transferTo);
        await audit.LogAsync(Me, "pool.delete", "Pool", id, id, detail: $"删除 Pool「{name}」，子 Pool：{childMode ?? "-"}，Workspace：{workspaceMode ?? "-"}{(transferTo != null ? " → " + transferTo : "")}");
        return NoContent();
    }

    /// <summary>直接子 Pool。</summary>
    [HttpGet("pools/{id}/children"), Scope(Scopes.PoolRead)]
    public async Task<object> Children(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var access = await engine.GetAccessiblePoolsAsync(Me);
        var tree = await pools.GetTreeAsync(Me);
        return tree.Where(n => n.ParentPoolId == id && access.ContainsKey(n.Id));
    }

    /// <summary>在指定 Pool 下创建子 Pool。</summary>
    [HttpPost("pools/{id}/children"), Scope(Scopes.PoolManage)]
    public async Task<object> CreateChild(string id, CreatePoolRequest req) => await Create(req with { ParentPoolId = id });

    /// <summary>祖先 Pool 链（由根到父）。</summary>
    [HttpGet("pools/{id}/ancestors"), Scope(Scopes.PoolRead)]
    public async Task<object> Ancestors(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var g = await engine.GetGraphAsync();
        return g.AncestorsOf(id).Reverse().Select(a => new { Id = a, Name = g.NameOf(a), Depth = g.Nodes[a].Depth });
    }

    /// <summary>后代 Pool（全部层级）。</summary>
    [HttpGet("pools/{id}/descendants"), Scope(Scopes.PoolRead)]
    public async Task<object> Descendants(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var g = await engine.GetGraphAsync();
        return g.DescendantsOf(id).Select(a => new { Id = a, Name = g.NameOf(a), Depth = g.Nodes[a].Depth, ParentPoolId = g.Nodes[a].ParentId });
    }

    // ─────────── 成员 ───────────

    /// <summary>成员列表：直接成员与继承成员（来自子孙 Pool）。</summary>
    [HttpGet("pools/{id}/members"), Scope(Scopes.PoolRead)]
    public async Task<object> Members(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var (direct, inherited) = await pools.ListMembersAsync(id);
        return new { direct, inherited };
    }

    /// <summary>添加 / 更新直属成员。</summary>
    [HttpPost("pools/{id}/members"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> AddMember(string id, MemberRequest req)
    {
        var m = await pools.UpsertMemberAsync(Me, id, req.UserId, req.Role);
        await audit.LogAsync(Me, "pool.member.add", "Pool", id, id, detail: $"成员 {req.UserId} 角色 {req.Role}");
        return Ok(new { m.Id });
    }

    /// <summary>调整成员角色。</summary>
    [HttpPatch("pools/{id}/members/{userId}"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> UpdateMember(string id, string userId, RoleRequest req)
    {
        await pools.UpsertMemberAsync(Me, id, userId, req.Role);
        await audit.LogAsync(Me, "pool.member.role", "Pool", id, id, detail: $"成员 {userId} 角色调整为 {req.Role}");
        return NoContent();
    }

    /// <summary>移除直属成员。</summary>
    [HttpDelete("pools/{id}/members/{userId}"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> RemoveMember(string id, string userId)
    {
        await pools.RemoveMemberAsync(Me, id, userId);
        await audit.LogAsync(Me, "pool.member.remove", "Pool", id, id, detail: $"移除成员 {userId}");
        return NoContent();
    }

    // ─────────── 移动 ───────────

    /// <summary>移动前预览影响范围。</summary>
    [HttpGet("pools/{id}/move-preview"), Scope(Scopes.PoolManage)]
    public async Task<object> MovePreview(string id, [FromQuery] string? newParentId) => await pools.PreviewMoveAsync(Me, id, string.IsNullOrEmpty(newParentId) ? null : newParentId);

    /// <summary>移动 Pool 到新父级。</summary>
    [HttpPost("pools/{id}/move"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> Move(string id, MoveRequest req)
    {
        var newParent = string.IsNullOrEmpty(req.NewParentId) ? null : req.NewParentId;
        await pools.MoveAsync(Me, id, newParent);
        await audit.LogAsync(Me, "pool.move", "Pool", id, id, detail: $"移动到父级 {newParent ?? "顶级"}");
        return NoContent();
    }

    // ─────────── 资源视图 ───────────

    /// <summary>Pool 的 Workspace 列表（可含子孙 Pool）。</summary>
    [HttpGet("pools/{id}/workspaces"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> Workspaces(string id, [FromQuery] bool includeDescendants = false, [FromQuery] bool includeArchived = false)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var graph = await engine.GetGraphAsync();
        var list = await workspaces.ListAsync(Me, id, includeArchived, includeDescendants);
        return list.Select(v => WorkspacesController.ToView(v, graph));
    }

    /// <summary>共享空间：直接授权 / 继承授权 / 本 Pool 拥有。</summary>
    [HttpGet("pools/{id}/shared-spaces"), Scope(Scopes.ShareRead)]
    public async Task<object> SharedSpaces(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var (direct, inherited, owned) = await spaces.ForPoolAsync(id);
        return new { direct, inherited, owned };
    }

    /// <summary>策略：本级声明 + 逐级收紧后的有效策略（含来源）。</summary>
    [HttpGet("pools/{id}/policy"), Scope(Scopes.PoolRead)]
    public async Task<object> GetPolicy(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var p = await pools.GetActiveAsync(id);
        return new { Own = PolicyService.Parse(p.PolicyJson), Effective = await policy.GetEffectiveAsync(id) };
    }

    /// <summary>保存本级策略（只能收紧；授权类字段仅平台管理员可改）。</summary>
    [HttpPut("pools/{id}/policy"), Scope(Scopes.PoolManage)]
    public async Task<object> PutPolicy(string id, PoolPolicy req)
    {
        await engine.RequirePoolManageAsync(Me, id);
        var p = await pools.GetActiveAsync(id);
        await policy.ValidateAsync(Me, p.ParentPoolId, id, req);
        // 默认共享空间必须存在且 Pool 已获授权，避免写入无意义配置
        if (req.DefaultSharedSpaceIds != null)
        {
            var avail = await engine.GetPoolSharedSpacesAsync(id);
            var bad = req.DefaultSharedSpaceIds.Where(s => !avail.TryGetValue(s, out var a) || a.Denied).ToList();
            if (bad.Count > 0) throw ApiException.BadRequest("默认共享空间必须是本 Pool 已获授权的共享空间");
        }
        p.PolicyJson = System.Text.Json.JsonSerializer.Serialize(req, PolicyService.Json);
        p.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await audit.LogAsync(Me, "pool.policy.update", "Pool", id, id, detail: p.PolicyJson);
        return new { Own = req, Effective = await policy.GetEffectiveAsync(id) };
    }

    /// <summary>有效配置（模型 / Skill / MCP，含来源与继承链）。</summary>
    [HttpGet("pools/{id}/effective-config"), Scope(Scopes.PoolRead)]
    public async Task<object> EffectiveConfig(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        return await config.ResolveAsync(id, null);
    }

    // ─────────── Runtime ───────────

    /// <summary>Pool 的 Runtime 状态（不暴露内部端点）。</summary>
    [HttpGet("pools/{id}/runtime"), Scope(Scopes.RuntimeRead)]
    public async Task<object> Runtime(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var pool = await pools.GetActiveAsync(id);
        var rt = await runtime.GetOrCreateAsync(pool);
        return RuntimeView(rt, pool, 0);
    }

    /// <summary>健康检查。</summary>
    [HttpGet("pools/{id}/runtime/health"), Scope(Scopes.RuntimeRead)]
    public async Task<object> Health(string id)
    {
        await engine.RequirePoolViewAsync(Me, id);
        var (rt, h) = await runtime.HealthAsync(await pools.GetActiveAsync(id));
        return new { h.Healthy, h.Version, h.Message, h.ActiveRuns, Status = rt.Status.ToString() };
    }

    /// <summary>启动 Runtime。</summary>
    [HttpPost("pools/{id}/runtime/start"), Scope(Scopes.RuntimeManage)]
    public Task<object> Start(string id) => Control(id, "start", p => runtime.StartAsync(p));
    /// <summary>停止 Runtime。</summary>
    [HttpPost("pools/{id}/runtime/stop"), Scope(Scopes.RuntimeManage)]
    public Task<object> Stop(string id) => Control(id, "stop", p => runtime.StopAsync(p));
    /// <summary>重启 Runtime。</summary>
    [HttpPost("pools/{id}/runtime/restart"), Scope(Scopes.RuntimeManage)]
    public Task<object> Restart(string id) => Control(id, "restart", p => runtime.RestartAsync(p));
    /// <summary>设置 / 取消维护模式。</summary>
    [HttpPost("pools/{id}/runtime/maintenance"), Scope(Scopes.RuntimeManage)]
    public Task<object> Maintenance(string id, MaintenanceRequest req) => Control(id, req.Enabled ? "maintenance-on" : "maintenance-off", p => runtime.SetMaintenanceAsync(p, req.Enabled));

    /// <summary>Runtime 控制通用流程：管理权校验 → 执行 → 审计。</summary>
    /// <param name="id">Pool ID。</param>
    /// <param name="action">动作名。</param>
    /// <param name="op">执行体。</param>
    private async Task<object> Control(string id, string action, Func<Pool, Task<RuntimeInstance>> op)
    {
        await engine.RequirePoolManageAsync(Me, id);
        var pool = await pools.GetActiveAsync(id);
        var rt = await op(pool);
        await audit.LogAsync(Me, "runtime." + action, "Runtime", rt.Id, id, detail: $"{action} → {rt.Status}");
        return RuntimeView(rt, pool, 0);
    }

    /// <summary>Runtime 视图（隐藏内部端点等细节）。</summary>
    /// <param name="rt">记录。</param>
    /// <param name="pool">Pool。</param>
    /// <param name="_">保留。</param>
    private static object RuntimeView(RuntimeInstance rt, Pool pool, int _) => new { rt.Id, PoolId = pool.Id, PoolName = pool.Name, Status = rt.Status.ToString(), rt.Provider, rt.Version, rt.NodeId, rt.StartedAt, rt.LastActiveAt, rt.LastError };
}
