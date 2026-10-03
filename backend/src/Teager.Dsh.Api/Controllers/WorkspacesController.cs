using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Api.Controllers;

/// <summary>创建 Workspace 请求。</summary>
/// <param name="PoolId">所属 Pool。</param>
/// <param name="Name">名称。</param>
/// <param name="Description">描述。</param>
/// <param name="InheritPoolMembers">是否继承 Pool 成员权限。</param>
public record CreateWorkspaceRequest(string PoolId, string Name, string? Description, bool? InheritPoolMembers);
/// <summary>更新 Workspace 请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Description">描述。</param>
/// <param name="InheritPoolMembers">是否继承 Pool 成员权限。</param>
public record UpdateWorkspaceRequest(string? Name, string? Description, bool? InheritPoolMembers);
/// <summary>Workspace 成员请求。</summary>
/// <param name="UserId">用户 ID。</param>
/// <param name="Role">角色。</param>
/// <param name="Effect">Allow / Deny。</param>
public record WorkspaceMemberRequest(string UserId, WorkspaceRole Role, GrantEffect? Effect);
/// <summary>挂载请求。</summary>
/// <param name="SharedSpaceId">共享空间 ID。</param>
/// <param name="MountName">挂载名。</param>
/// <param name="PermissionOverride">权限覆盖。</param>
public record MountRequest(string SharedSpaceId, string? MountName, SharePermission? PermissionOverride);
/// <summary>挂载更新请求。</summary>
/// <param name="Enabled">是否启用。</param>
/// <param name="PermissionOverride">权限覆盖。</param>
/// <param name="ClearOverride">是否清除覆盖。</param>
public record MountUpdateRequest(bool? Enabled, SharePermission? PermissionOverride, bool? ClearOverride);
/// <summary>快照请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Description">备注。</param>
public record SnapshotRequest(string? Name, string? Description);
/// <summary>克隆请求。</summary>
/// <param name="Name">新名称。</param>
public record CloneRequest(string? Name);
/// <summary>文件路径请求。</summary>
/// <param name="Path">相对路径。</param>
public record PathRequest(string Path);
/// <summary>文件移动请求。</summary>
/// <param name="From">源路径。</param>
/// <param name="To">目标路径。</param>
public record MoveFileRequest(string From, string To);
/// <summary>恢复历史版本请求。</summary>
/// <param name="Path">相对路径。</param>
/// <param name="Version">版本号。</param>
public record RestoreVersionRequest(string Path, string Version);
/// <summary>文件内容写入请求。</summary>
/// <param name="Path">相对路径。</param>
/// <param name="Content">文本内容。</param>
public record WriteFileRequest(string Path, string Content);

/// <summary>Workspace 管理：CRUD、成员、共享空间挂载、快照 / 克隆、文件、权限解释、终端。</summary>
public class WorkspacesController(ActorAccessor accessor, PermissionEngine engine, WorkspaceService workspaces, AuditService audit, DshDbContext db, PolicyService policy,
    ConfigService config, FileAccessService files, RuntimeManager runtime, RuntimeMaterializer materializer, StoragePaths paths, Microsoft.Extensions.Options.IOptions<Teager.Dsh.Infrastructure.Options.DshOptions> opt, ILogger<WorkspacesController> logger) : ApiBase(accessor)
{
    /// <summary>Workspace 列表视图。</summary>
    /// <param name="v">带访问结果的 Workspace。</param>
    /// <param name="graph">Pool 图。</param>
    public static object ToView(WorkspaceView v, PoolGraph graph) => new
    {
        v.Workspace.Id, v.Workspace.PoolId, PoolName = graph.NameOf(v.Workspace.PoolId), v.Workspace.Name, v.Workspace.Description, Status = v.Workspace.Status.ToString(),
        v.Workspace.InheritPoolMembers, v.Workspace.StorageBytes, v.Workspace.LastActiveAt, v.Workspace.CreatedAt, v.Workspace.ArchivedAt, v.Workspace.PurgeAfter,
        MyRole = v.Access.Role?.ToString(), Source = v.Access.Source, ViaPool = v.Access.ViaPoolId == null ? null : graph.NameOf(v.Access.ViaPoolId), v.Access.Path, v.CanManage
    };

    /// <summary>我可访问的 Workspace 列表。</summary>
    [HttpGet("workspaces"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> List([FromQuery] string? poolId, [FromQuery] bool includeArchived = false, [FromQuery] bool includeDescendants = false)
    {
        var graph = await engine.GetGraphAsync();
        return (await workspaces.ListAsync(Me, poolId, includeArchived, includeDescendants)).Select(v => ToView(v, graph));
    }

    /// <summary>创建 Workspace。</summary>
    [HttpPost("workspaces"), Scope(Scopes.WorkspaceWrite)]
    public async Task<object> Create(CreateWorkspaceRequest req)
    {
        var ws = await workspaces.CreateAsync(Me, req.PoolId, req.Name, req.Description, req.InheritPoolMembers ?? true);
        await audit.LogAsync(Me, "workspace.create", "Workspace", ws.Id, ws.PoolId, ws.Id, $"创建 Workspace「{ws.Name}」");
        return new { ws.Id, ws.Name, ws.PoolId };
    }

    /// <summary>Workspace 详情。</summary>
    [HttpGet("workspaces/{id}"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> Get(string id)
    {
        var (ws, wa) = await engine.RequireWorkspaceAsync(Me, id, null);
        var graph = await engine.GetGraphAsync();
        var canManage = await engine.CanManageWorkspaceAsync(Me, ws, wa);
        var view = ToView(new WorkspaceView(ws, wa, canManage), graph);
        var eff = await policy.GetEffectiveAsync(ws.PoolId);
        var sessions = await db.Sessions.CountAsync(s => s.WorkspaceId == id && s.Status == SessionStatus.Active);
        var snaps = await db.WorkspaceSnapshots.CountAsync(s => s.WorkspaceId == id);
        var mounts = await db.WorkspaceSharedSpaceMounts.CountAsync(m => m.WorkspaceId == id);
        var rt = await db.Runtimes.AsNoTracking().FirstOrDefaultAsync(r => r.PoolId == ws.PoolId);
        var pool = await db.Pools.AsNoTracking().FirstAsync(p => p.Id == ws.PoolId);
        return new { View = view, Sessions = sessions, Snapshots = snaps, Mounts = mounts, Policy = eff, Runtime = rt == null ? null : new { Status = rt.Status.ToString(), rt.Provider }, pool.RuntimeProvider, TerminalAllowed = eff.AllowTerminal };
    }

    /// <summary>更新 Workspace。</summary>
    [HttpPatch("workspaces/{id}"), Scope(Scopes.WorkspaceWrite)]
    public async Task<object> Update(string id, UpdateWorkspaceRequest req)
    {
        var ws = await workspaces.UpdateAsync(Me, id, req.Name, req.Description, req.InheritPoolMembers);
        await audit.LogAsync(Me, "workspace.update", "Workspace", id, ws.PoolId, id, $"更新 Workspace「{ws.Name}」");
        return new { ws.Id, ws.Name };
    }

    /// <summary>删除 Workspace：mode = runtime_context / archive（默认）/ permanent。</summary>
    [HttpDelete("workspaces/{id}"), Scope(Scopes.WorkspaceManage)]
    public async Task<IActionResult> Delete(string id, [FromQuery] string mode = "archive")
    {
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id) ?? throw ApiException.NotFound("Workspace 不存在或无权访问");
        await workspaces.DeleteAsync(Me, id, mode);
        await audit.LogAsync(Me, "workspace.delete", "Workspace", id, ws.PoolId, id, $"删除 Workspace「{ws.Name}」，模式：{mode}");
        return NoContent();
    }

    /// <summary>恢复已归档的 Workspace。</summary>
    [HttpPost("workspaces/{id}/restore"), Scope(Scopes.WorkspaceManage)]
    public async Task<IActionResult> Restore(string id)
    {
        await workspaces.RestoreAsync(Me, id);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.restore", "Workspace", id, ws.PoolId, id);
        return NoContent();
    }

    /// <summary>克隆 Workspace（复制文件与挂载配置）。</summary>
    [HttpPost("workspaces/{id}/clone"), Scope(Scopes.WorkspaceWrite)]
    public async Task<object> Clone(string id, CloneRequest req)
    {
        var c = await workspaces.CloneAsync(Me, id, req.Name);
        await audit.LogAsync(Me, "workspace.clone", "Workspace", c.Id, c.PoolId, c.Id, $"从 {id} 克隆为「{c.Name}」");
        return new { c.Id, c.Name };
    }

    // ─────────── 成员 ───────────

    /// <summary>显式成员 / 显式拒绝列表，以及我的访问来源解释。</summary>
    [HttpGet("workspaces/{id}/members"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> Members(string id)
    {
        var (ws, wa) = await engine.RequireWorkspaceAsync(Me, id, null);
        return new { Members = await workspaces.ListMembersAsync(id), InheritPoolMembers = ws.InheritPoolMembers, MyAccess = new { Role = wa.Role?.ToString(), wa.Source, wa.Path } };
    }

    /// <summary>添加 / 更新显式成员或显式拒绝。</summary>
    [HttpPost("workspaces/{id}/members"), Scope(Scopes.WorkspaceManage)]
    public async Task<IActionResult> AddMember(string id, WorkspaceMemberRequest req)
    {
        await workspaces.UpsertMemberAsync(Me, id, req.UserId, req.Role, req.Effect ?? GrantEffect.Allow);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.member.set", "Workspace", id, ws.PoolId, id, $"用户 {req.UserId} → {(req.Effect == GrantEffect.Deny ? "显式拒绝" : req.Role.ToString())}");
        return NoContent();
    }

    /// <summary>移除显式成员。</summary>
    [HttpDelete("workspaces/{id}/members/{userId}"), Scope(Scopes.WorkspaceManage)]
    public async Task<IActionResult> RemoveMember(string id, string userId)
    {
        await workspaces.RemoveMemberAsync(Me, id, userId);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.member.remove", "Workspace", id, ws.PoolId, id, $"移除用户 {userId}");
        return NoContent();
    }

    // ─────────── 共享空间挂载 ───────────

    /// <summary>已配置的挂载（含有效权限与来源）与可挂载的共享空间候选。</summary>
    [HttpGet("workspaces/{id}/mounts"), Scope(Scopes.ShareRead)]
    public async Task<object> Mounts(string id)
    {
        var (ws, wa) = await engine.RequireWorkspaceAsync(Me, id, null);
        var (mounts, available) = await workspaces.ListMountsAsync(Me, ws, wa);
        return new { mounts, available };
    }

    /// <summary>挂载共享空间。</summary>
    [HttpPost("workspaces/{id}/mounts"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> AddMount(string id, MountRequest req)
    {
        await workspaces.AddMountAsync(Me, id, req.SharedSpaceId, req.MountName, req.PermissionOverride);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.mount.add", "SharedSpace", req.SharedSpaceId, ws.PoolId, id, $"挂载共享空间 {req.SharedSpaceId}，覆盖：{req.PermissionOverride?.ToString() ?? "无"}");
        return NoContent();
    }

    /// <summary>更新挂载（启用 / 停用 / 权限覆盖）。</summary>
    [HttpPatch("workspaces/{id}/mounts/{mountId}"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> UpdateMount(string id, string mountId, MountUpdateRequest req)
    {
        await workspaces.UpdateMountAsync(Me, id, mountId, req.Enabled, req.PermissionOverride, req.ClearOverride ?? false);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.mount.update", "Workspace", id, ws.PoolId, id, $"挂载 {mountId}：启用={req.Enabled}，覆盖={req.PermissionOverride?.ToString() ?? (req.ClearOverride == true ? "清除" : "不变")}");
        return NoContent();
    }

    /// <summary>取消挂载。</summary>
    [HttpDelete("workspaces/{id}/mounts/{mountId}"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> RemoveMount(string id, string mountId)
    {
        await workspaces.RemoveMountAsync(Me, id, mountId);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.mount.remove", "Workspace", id, ws.PoolId, id, $"取消挂载 {mountId}");
        return NoContent();
    }

    // ─────────── 权限解释 / 配置 / 用量 ───────────

    /// <summary>权限来源解释：我为何能访问该 Workspace；指定 sharedSpaceId 时同时解释共享空间权限的来源与继承路径。</summary>
    [HttpGet("workspaces/{id}/permissions/explain"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> Explain(string id, [FromQuery] string? sharedSpaceId)
    {
        var (ws, wa) = await engine.RequireWorkspaceAsync(Me, id, null);
        var graph = await engine.GetGraphAsync();
        object? space = null;
        if (sharedSpaceId != null)
        {
            var s = await db.SharedSpaces.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sharedSpaceId) ?? throw ApiException.NotFound("共享空间不存在");
            var mount = await db.WorkspaceSharedSpaceMounts.AsNoTracking().FirstOrDefaultAsync(m => m.WorkspaceId == id && m.SharedSpaceId == sharedSpaceId);
            var a = await engine.GetWorkspaceSpaceAccessAsync(Me, ws, wa, s, mount);
            space = new { s.Id, s.Name, Permission = a.Permission?.ToString(), a.Source, ViaPool = a.ViaPoolId == null ? null : graph.NameOf(a.ViaPoolId), a.Path, a.Notes, Mounted = mount != null && mount.Enabled };
        }
        return new { Workspace = new { ws.Id, ws.Name, Pool = graph.NameOf(ws.PoolId) }, Access = new { Role = wa.Role?.ToString(), wa.Denied, wa.Source, ViaPool = wa.ViaPoolId == null ? null : graph.NameOf(wa.ViaPoolId), wa.Path }, SharedSpace = space };
    }

    /// <summary>有效配置（模型 / Skill / MCP 逐级合并结果）。</summary>
    [HttpGet("workspaces/{id}/effective-config"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> EffectiveConfig(string id)
    {
        var (ws, _) = await engine.RequireWorkspaceAsync(Me, id, null);
        return await config.ResolveAsync(ws.PoolId, ws.Id);
    }

    // ─────────── 快照 ───────────

    /// <summary>快照列表。</summary>
    [HttpGet("workspaces/{id}/snapshots"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> Snapshots(string id)
    {
        await engine.RequireWorkspaceAsync(Me, id, WorkspaceRole.Viewer);
        return await db.WorkspaceSnapshots.AsNoTracking().Where(s => s.WorkspaceId == id).OrderByDescending(s => s.CreatedAt).Select(s => new { s.Id, s.Name, s.Description, s.SizeBytes, s.CreatedAt, s.CreatedBy }).ToListAsync();
    }

    /// <summary>创建快照。</summary>
    [HttpPost("workspaces/{id}/snapshots"), Scope(Scopes.WorkspaceWrite)]
    public async Task<object> CreateSnapshot(string id, SnapshotRequest req)
    {
        var s = await workspaces.CreateSnapshotAsync(Me, id, req.Name, req.Description);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.snapshot.create", "Workspace", id, ws.PoolId, id, $"创建快照「{s.Name}」（{s.SizeBytes} 字节）");
        return new { s.Id, s.Name, s.SizeBytes };
    }

    /// <summary>从快照恢复（覆盖私有文件）。</summary>
    [HttpPost("workspaces/{id}/snapshots/{snapshotId}/restore"), Scope(Scopes.WorkspaceManage)]
    public async Task<IActionResult> RestoreSnapshot(string id, string snapshotId)
    {
        await workspaces.RestoreSnapshotAsync(Me, id, snapshotId);
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id);
        await audit.LogAsync(Me, "workspace.snapshot.restore", "Workspace", id, ws.PoolId, id, $"从快照 {snapshotId} 恢复");
        return NoContent();
    }

    /// <summary>删除快照。</summary>
    [HttpDelete("workspaces/{id}/snapshots/{snapshotId}"), Scope(Scopes.WorkspaceManage)]
    public async Task<IActionResult> DeleteSnapshot(string id, string snapshotId)
    {
        await workspaces.DeleteSnapshotAsync(Me, id, snapshotId);
        return NoContent();
    }

    /// <summary>下载快照 zip。</summary>
    [HttpGet("workspaces/{id}/snapshots/{snapshotId}/download"), Scope(Scopes.FilesRead)]
    public async Task<IActionResult> DownloadSnapshot(string id, string snapshotId)
    {
        await engine.RequireWorkspaceAsync(Me, id, WorkspaceRole.Member);
        var snap = await db.WorkspaceSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == snapshotId && s.WorkspaceId == id) ?? throw ApiException.NotFound("快照不存在");
        var f = paths.SnapshotFile(snap.Id);
        if (!System.IO.File.Exists(f)) throw ApiException.NotFound("快照文件已丢失");
        return PhysicalFile(f, "application/zip", snap.Name + ".zip");
    }

    // ─────────── 文件 ───────────

    /// <summary>列出目录：area = files（私有）或 shared/{挂载名}。</summary>
    [HttpGet("workspaces/{id}/files"), Scope(Scopes.FilesRead)]
    public async Task<object> ListFiles(string id, [FromQuery] string? area, [FromQuery] string? path)
    {
        var a = await FileScope(id, area, false);
        return new { Area = area ?? "files", a.Label, a.Writable, Path = FileStore.Normalize(path), Entries = FileStore.List(a.Root, path) };
    }

    /// <summary>读取文本文件内容（在线预览 / 编辑）。</summary>
    [HttpGet("workspaces/{id}/files/content"), Scope(Scopes.FilesRead)]
    public async Task<object> ReadFile(string id, [FromQuery] string? area, [FromQuery] string path)
    {
        var a = await FileScope(id, area, false);
        var text = await FileStore.ReadTextAsync(a.Root, path);
        await files.RecordAsync(Me, a, "read", path);
        return new { Path = FileStore.Normalize(path), Content = text };
    }

    /// <summary>写入（覆盖）文本文件。</summary>
    [HttpPut("workspaces/{id}/files/content"), Scope(Scopes.FilesWrite)]
    public async Task<IActionResult> WriteFile(string id, [FromQuery] string? area, WriteFileRequest req)
    {
        var a = await FileScope(id, area, true);
        var bytes = Encoding.UTF8.GetBytes(req.Content ?? "");
        await files.EnsureQuotaAsync(a, bytes.Length);
        files.VersionBefore(a, req.Path);
        await FileStore.WriteAsync(a.Root, req.Path, new MemoryStream(bytes), MaxUploadBytes);
        await files.RecordAsync(Me, a, "write", req.Path);
        return NoContent();
    }

    /// <summary>上传文件（multipart，可多文件）。</summary>
    [HttpPost("workspaces/{id}/files/upload"), Scope(Scopes.FilesWrite)]
    public async Task<object> Upload(string id, [FromQuery] string? area, [FromQuery] string? path)
    {
        var a = await FileScope(id, area, true);
        var saved = new List<string>();
        foreach (var f in Request.Form.Files)
        {
            var name = Path.GetFileName(f.FileName);
            if (string.IsNullOrEmpty(name)) continue;
            var rel = FileStore.Normalize(path);
            rel = rel.Length == 0 ? name : rel + "/" + name;
            await files.EnsureQuotaAsync(a, f.Length);
            files.VersionBefore(a, rel);
            await using var s = f.OpenReadStream();
            await FileStore.WriteAsync(a.Root, rel, s, MaxUploadBytes);
            await files.RecordAsync(Me, a, "write", rel, $"上传 {f.Length} 字节");
            saved.Add(rel);
        }
        return new { Saved = saved };
    }

    /// <summary>创建目录。</summary>
    [HttpPost("workspaces/{id}/files/mkdir"), Scope(Scopes.FilesWrite)]
    public async Task<IActionResult> Mkdir(string id, [FromQuery] string? area, PathRequest req)
    {
        var a = await FileScope(id, area, true);
        FileStore.Mkdir(a.Root, req.Path);
        await files.RecordAsync(Me, a, "mkdir", req.Path);
        return NoContent();
    }

    /// <summary>删除文件或目录。</summary>
    [HttpDelete("workspaces/{id}/files"), Scope(Scopes.FilesWrite)]
    public async Task<IActionResult> DeleteFile(string id, [FromQuery] string? area, [FromQuery] string path)
    {
        var a = await FileScope(id, area, true);
        files.VersionBefore(a, path);
        FileStore.Delete(a.Root, path);
        await files.RecordAsync(Me, a, "delete", path);
        return NoContent();
    }

    /// <summary>重命名 / 移动。</summary>
    [HttpPost("workspaces/{id}/files/move"), Scope(Scopes.FilesWrite)]
    public async Task<IActionResult> MoveFile(string id, [FromQuery] string? area, MoveFileRequest req)
    {
        var a = await FileScope(id, area, true);
        FileStore.Move(a.Root, req.From, req.To);
        var sameDir = Path.GetDirectoryName(FileStore.Normalize(req.From)) == Path.GetDirectoryName(FileStore.Normalize(req.To));
        await files.RecordAsync(Me, a, sameDir ? "rename" : "move", req.From, req.To);
        return NoContent();
    }

    /// <summary>下载文件。</summary>
    [HttpGet("workspaces/{id}/files/download"), Scope(Scopes.FilesRead)]
    public async Task<IActionResult> Download(string id, [FromQuery] string? area, [FromQuery] string path)
    {
        var a = await FileScope(id, area, false);
        var stream = FileStore.OpenRead(a.Root, path);
        await files.RecordAsync(Me, a, "read", path, "下载");
        return File(stream, "application/octet-stream", Path.GetFileName(FileStore.Normalize(path)));
    }

    /// <summary>文件的历史版本列表。</summary>
    [HttpGet("workspaces/{id}/files/versions"), Scope(Scopes.FilesRead)]
    public async Task<object> FileVersions(string id, [FromQuery] string? area, [FromQuery] string path)
    {
        var a = await FileScope(id, area, false);
        return files.Versions(a, path);
    }

    /// <summary>读取某历史版本的文本内容。</summary>
    [HttpGet("workspaces/{id}/files/versions/content"), Scope(Scopes.FilesRead)]
    public async Task<object> FileVersionContent(string id, [FromQuery] string? area, [FromQuery] string path, [FromQuery] string version)
    {
        var a = await FileScope(id, area, false);
        return new { Path = FileStore.Normalize(path), Version = version, Content = await files.ReadVersionAsync(a, path, version) };
    }

    /// <summary>恢复到某历史版本（恢复前会保存当前内容，可再次撤销）。</summary>
    [HttpPost("workspaces/{id}/files/versions/restore"), Scope(Scopes.FilesWrite)]
    public async Task<IActionResult> RestoreFileVersion(string id, [FromQuery] string? area, RestoreVersionRequest req)
    {
        var a = await FileScope(id, area, true);
        await files.RestoreVersionAsync(Me, a, req.Path, req.Version);
        return NoContent();
    }

    /// <summary>解析文件区域并叠加 Token Scope（共享空间区域额外要求 share:*）。</summary>
    /// <param name="id">Workspace ID。</param>
    /// <param name="area">区域。</param>
    /// <param name="write">是否写入。</param>
    private async Task<FileArea> FileScope(string id, string? area, bool write)
    {
        if (area != null && area.StartsWith("shared/")) Me.RequireScope(write ? Scopes.ShareWrite : Scopes.ShareRead);
        return await files.ResolveWorkspaceAreaAsync(Me, id, area, write);
    }

    /// <summary>单文件上传大小上限（字节）。</summary>
    private long MaxUploadBytes => (long)opt.Value.Platform.MaxUploadMb * 1024 * 1024;

    // ─────────── 终端（WebSocket）───────────

    /// <summary>
    /// Workspace 终端：WebSocket 双向通道，文本帧即为一行输入 / 输出片段。
    /// 受平台开关（Dsh__Runtime__TerminalEnabled）与 Pool 策略（allowTerminal）双重控制，需要 Member 及以上角色，并全程审计。
    /// </summary>
    [HttpGet("workspaces/{id}/terminal"), Scope(Scopes.WorkspaceExecute)]
    public async Task Terminal(string id)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest) throw ApiException.BadRequest("需要 WebSocket 连接");
        var (ws, wa) = await engine.RequireWorkspaceAsync(Me, id, WorkspaceRole.Member);
        var eff = await policy.GetEffectiveAsync(ws.PoolId);
        if (!eff.AllowTerminal) throw ApiException.Forbidden("终端已被平台或 Pool 策略禁用");
        // 防跨站 WebSocket 劫持：Origin 必须与当前 Host 一致
        var origin = Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            var sameHost = Uri.TryCreate(origin, UriKind.Absolute, out var ou) && string.Equals(ou.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase);
            if (!sameHost && !opt.Value.Platform.CorsOrigins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)) throw ApiException.Forbidden("非法的来源");
        }

        var pool = await db.Pools.AsNoTracking().FirstAsync(p => p.Id == ws.PoolId);
        var mounts = await materializer.EnsureLayoutAsync(Me, ws, wa);
        var before = mounts.ToDictionary(m => m.MountName, m => FileStore.Manifest(m.Directory));
        var env = ProcessUtil.BuildEnv(opt.Value.Runtime.EnvPassthrough, new Dictionary<string, string> { ["HOME"] = paths.WorkspaceRoot(id), ["USERPROFILE"] = paths.WorkspaceRoot(id) });
        await audit.LogAsync(Me, "terminal.open", "Workspace", id, ws.PoolId, id, "打开终端");

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await using var term = await runtime.Provider(pool.RuntimeProvider).OpenTerminalAsync(new TerminalRequest(paths.WorkspaceFiles(id), env));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        var pump = Task.Run(async () =>
        {
            try { await foreach (var chunk in term.ReadOutputAsync(cts.Token)) await socket.SendAsync(Encoding.UTF8.GetBytes(chunk), WebSocketMessageType.Text, true, cts.Token); }
            catch (Exception) { /* 连接关闭 */ }
            finally { cts.Cancel(); }
        });
        var buf = new byte[8192];
        try
        {
            while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Text) await term.WriteAsync(Encoding.UTF8.GetString(buf, 0, r.Count).TrimEnd('\r', '\n'));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        cts.Cancel();
        try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        await pump.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { });

        // 终端无法按文件粒度限制共享空间读写，退出后对比文件清单，对 RO 挂载的改动记违规审计
        foreach (var m in mounts)
        {
            try
            {
                var after = FileStore.Manifest(m.Directory); var b = before[m.MountName];
                var changed = after.Where(kv => !b.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).Concat(b.Keys.Except(after.Keys)).ToList();
                if (changed.Count == 0) continue;
                await audit.LogAsync(Me, m.Permission == SharePermission.RO ? "sharedspace.ro_violation" : "sharedspace.write", "SharedSpace", m.SharedSpaceId, ws.PoolId, id,
                    $"终端会话期间共享空间「{m.SpaceName}」发生 {changed.Count} 处变更；示例：{string.Join(", ", changed.Take(5))}", m.Permission == SharePermission.RO ? "denied" : "success");
            }
            catch (Exception ex) { logger.LogWarning(ex, "终端变更检测失败"); }
        }
        await audit.LogAsync(Me, "terminal.close", "Workspace", id, ws.PoolId, id, "关闭终端");
    }
}
