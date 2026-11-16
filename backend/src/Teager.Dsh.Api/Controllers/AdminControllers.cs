using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Api.Controllers;

/// <summary>创建用户请求。</summary>
/// <param name="Username">登录名。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Email">邮箱。</param>
/// <param name="Password">初始密码。</param>
/// <param name="Role">平台角色。</param>
/// <param name="IsServiceAccount">是否服务账号。</param>
public record CreateUserRequest(string Username, string? DisplayName, string? Email, string? Password, PlatformRole? Role, bool? IsServiceAccount);
/// <summary>更新用户请求。</summary>
/// <param name="DisplayName">显示名。</param>
/// <param name="Email">邮箱。</param>
/// <param name="Role">平台角色。</param>
/// <param name="Status">状态。</param>
public record UpdateUserRequest(string? DisplayName, string? Email, PlatformRole? Role, UserStatus? Status);
/// <summary>重置密码请求。</summary>
/// <param name="NewPassword">新密码。</param>
public record ResetPasswordRequest(string NewPassword);
public record AssignWorkspaceRequest(string WorkspaceId);
/// <summary>运行节点请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Host">主机描述。</param>
/// <param name="Status">状态。</param>
/// <param name="MaxRuntimes">Runtime 上限。</param>
public record NodeRequest(string? Name, string? Host, string? Status, int? MaxRuntimes);

/// <summary>用户管理（平台管理员），以及成员选择器用的轻量检索。</summary>
public class UsersController(ActorAccessor accessor, UserService users, AuditService audit, PersonalWorkspaceService personal) : ApiBase(accessor)
{
    /// <summary>用户视图。</summary>
    /// <param name="u">用户。</param>
    private static object View(User u) => new { u.Id, u.Username, u.DisplayName, u.Email, Role = u.Role.ToString(), Status = u.Status.ToString(), u.IsServiceAccount, u.DefaultWorkspaceId, u.LastLoginAt, u.CreatedAt };

    /// <summary>用户列表（仅管理员）。</summary>
    [HttpGet("users")]
    public async Task<object> List([FromQuery] string? q)
    {
        RequireAdmin();
        return (await users.ListAsync(q)).Select(View);
    }

    /// <summary>成员选择器检索（任何登录用户，仅返回有限字段）。</summary>
    [HttpGet("users/lookup")]
    public async Task<object> Lookup([FromQuery] string? q) { RequireAdmin(); return await users.LookupAsync(q); }

    /// <summary>创建用户 / 服务账号（仅管理员）。</summary>
    [HttpPost("users")]
    public async Task<object> Create(CreateUserRequest req)
    {
        RequireAdmin();
        var u = await users.CreateAsync(Me, req.Username, req.DisplayName ?? req.Username, req.Email, req.Password, req.Role ?? PlatformRole.User, req.IsServiceAccount ?? false);
        await audit.LogAsync(Me, "user.create", "User", u.Id, detail: $"创建{(u.IsServiceAccount ? "服务账号" : "用户")} {u.Username}，角色 {u.Role}");
        return View(u);
    }

    /// <summary>更新用户（仅管理员）。</summary>
    [HttpPatch("users/{id}")]
    public async Task<object> Update(string id, UpdateUserRequest req)
    {
        RequireAdmin();
        var u = await users.UpdateAsync(Me, id, req.DisplayName, req.Email, req.Role, req.Status);
        await audit.LogAsync(Me, "user.update", "User", id, detail: $"{u.Username}：角色={u.Role}，状态={u.Status}");
        return View(u);
    }

    /// <summary>重置密码（仅管理员）。</summary>
    [HttpPost("users/{id}/reset-password")]
    public async Task<IActionResult> Reset(string id, ResetPasswordRequest req)
    {
        RequireAdmin();
        await users.ResetPasswordAsync(Me, id, req.NewPassword);
        await audit.LogAsync(Me, "user.password.reset", "User", id);
        return NoContent();
    }

    [HttpPut("users/{id}/workspace")]
    public async Task<IActionResult> AssignWorkspace(string id, AssignWorkspaceRequest req)
    {
        RequireAdmin(); await personal.AssignAsync(Me, id, req.WorkspaceId);
        await audit.LogAsync(Me, "user.workspace.assign", "User", id, workspaceId: req.WorkspaceId);
        return NoContent();
    }

    /// <summary>要求平台管理员，且必须是 Web 登录态（Token 不允许做用户管理）。</summary>
    private void RequireAdmin()
    {
        if (!Me.IsPlatformAdmin) throw ApiException.Forbidden("需要平台管理员权限");
        if (Me.Via != "web") throw ApiException.Forbidden("用户管理仅支持 Web 登录态");
    }
}

/// <summary>平台管理：所有 Runtime、运行节点、系统信息（仅平台管理员）。</summary>
public class AdminController(ActorAccessor accessor, DshDbContext db, PermissionEngine engine, IOptions<DshOptions> opt, StoragePaths paths, RuntimeState state,
    IEnumerable<IRuntimeProvider> providers, AuditService audit) : ApiBase(accessor)
{
    /// <summary>平台管理员校验。</summary>
    private void RequireAdmin() { if (!Me.IsPlatformAdmin || Me.Via != "web") throw ApiException.Forbidden("需要平台管理员网页登录"); }

    /// <summary>所有 Pool 的 Runtime 状态。</summary>
    [HttpGet("runtimes"), Scope(Scopes.RuntimeRead)]
    public async Task<object> Runtimes()
    {
        RequireAdmin();
        var graph = await engine.GetGraphAsync();
        var rows = await db.Runtimes.AsNoTracking().ToListAsync();
        var pools = await db.Pools.AsNoTracking().Where(p => p.Status == PoolStatus.Active).ToListAsync();
        return pools.Select(p =>
        {
            var r = rows.FirstOrDefault(x => x.PoolId == p.Id);
            return new { PoolId = p.Id, PoolName = graph.NameOf(p.Id), p.RuntimeProvider, Status = r?.Status.ToString() ?? "STOPPED", r?.Version, r?.NodeId, r?.StartedAt, r?.LastActiveAt, r?.LastError, ActiveRuns = state.ActiveCount(p.Id) };
        }).OrderBy(x => x.PoolName);
    }

    /// <summary>运行节点列表。</summary>
    [HttpGet("runtime-nodes"), Scope(Scopes.RuntimeRead)]
    public async Task<object> Nodes()
    {
        RequireAdmin();
        var rt = await db.Runtimes.AsNoTracking().GroupBy(r => r.NodeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return (await db.RuntimeNodes.AsNoTracking().ToListAsync()).Select(n => new { n.Id, n.Name, n.Host, n.Status, n.MaxRuntimes, n.CreatedAt, Runtimes = rt.GetValueOrDefault(n.Id) });
    }

    /// <summary>登记运行节点（第一版仅登记信息，调度器属于后续阶段）。</summary>
    [HttpPost("runtime-nodes"), Scope(Scopes.RuntimeManage)]
    public async Task<object> CreateNode(NodeRequest req)
    {
        RequireAdmin();
        if (string.IsNullOrWhiteSpace(req.Name)) throw ApiException.BadRequest("节点名称不能为空");
        var n = new RuntimeNode { Id = IdGen.New("node"), Name = req.Name.Trim(), Host = req.Host, MaxRuntimes = req.MaxRuntimes ?? 100 };
        db.RuntimeNodes.Add(n);
        await db.SaveChangesAsync();
        await audit.LogAsync(Me, "runtime.node.create", "RuntimeNode", n.Id, detail: $"登记节点「{n.Name}」");
        return new { n.Id, n.Name };
    }

    /// <summary>更新节点（状态：Online / Offline / Draining）。</summary>
    [HttpPatch("runtime-nodes/{id}"), Scope(Scopes.RuntimeManage)]
    public async Task<object> UpdateNode(string id, NodeRequest req)
    {
        RequireAdmin();
        var n = await db.RuntimeNodes.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("节点不存在");
        if (req.Name != null) n.Name = req.Name.Trim();
        if (req.Host != null) n.Host = req.Host;
        if (req.MaxRuntimes != null) n.MaxRuntimes = req.MaxRuntimes.Value;
        if (req.Status != null) { if (req.Status is not ("Online" or "Offline" or "Draining")) throw ApiException.BadRequest("状态仅支持 Online / Offline / Draining"); n.Status = req.Status; }
        await db.SaveChangesAsync();
        await audit.LogAsync(Me, "runtime.node.update", "RuntimeNode", id, detail: $"节点「{n.Name}」状态 {n.Status}");
        return new { n.Id, n.Name, n.Status };
    }

    /// <summary>系统信息：目录布局、磁盘、Provider、统计（不含任何密钥）。</summary>
    [HttpGet("system/info")]
    public async Task<object> Info()
    {
        RequireAdmin();
        static object Disk(string path)
        {
            try { var d = new DriveInfo(Path.GetPathRoot(path)!); return new { Path = path, TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace }; }
            catch { return new { Path = path, TotalBytes = 0L, FreeBytes = 0L }; }
        }
        var health = new List<object>();
        foreach (var p in providers) health.Add(new { p.Name });
        return new
        {
            Version = typeof(AdminController).Assembly.GetName().Version?.ToString(), Now = DateTime.Now, TimeZone = TimeZoneInfo.Local.DisplayName,
            Directories = new { Data = Disk(paths.Data), Logs = Disk(paths.Logs), Cache = Disk(paths.Cache) },
            Providers = health, DefaultProvider = opt.Value.Runtime.DefaultProvider, DshExecutable = opt.Value.Runtime.DshExecutable, TerminalEnabled = opt.Value.Runtime.TerminalEnabled,
            Limits = new { opt.Value.Platform.MaxPoolDepth, opt.Value.Platform.ArchiveRetentionDays, opt.Value.Platform.AuditRetentionDays, opt.Value.Platform.MaxUploadMb, opt.Value.Runtime.MaxConcurrentRunsPerPool },
            Counts = new
            {
                Users = await db.Users.CountAsync(), Pools = await db.Pools.CountAsync(p => p.Status == PoolStatus.Active), Workspaces = await db.Workspaces.CountAsync(w => w.Status == WorkspaceStatus.Active),
                SharedSpaces = await db.SharedSpaces.CountAsync(s => s.Status == SharedSpaceStatus.Active), Sessions = await db.Sessions.CountAsync(), RunningRuns = state.Runs.Count
            }
        };
    }
}
