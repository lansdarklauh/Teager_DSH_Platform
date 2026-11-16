using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Api.Controllers;

/// <summary>创建共享空间请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Description">描述。</param>
/// <param name="OwnerPoolId">所属 Pool。</param>
/// <param name="QuotaMb">配额 MB。</param>
/// <param name="MountName">默认挂载名。</param>
public record CreateSpaceRequest(string Name, string? Description, string? OwnerPoolId, int? QuotaMb, string? MountName);
/// <summary>更新共享空间请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Description">描述。</param>
/// <param name="QuotaMb">配额 MB。</param>
/// <param name="MountName">默认挂载名。</param>
public record UpdateSpaceRequest(string? Name, string? Description, int? QuotaMb, string? MountName);
/// <summary>授权请求。</summary>
/// <param name="SubjectType">主体类型 POOL / USER。</param>
/// <param name="SubjectId">主体 ID。</param>
/// <param name="Permission">权限 RO / RW / MANAGE。</param>
/// <param name="Effect">Allow / Deny。</param>
/// <param name="InheritToChildren">是否继承给子 Pool。</param>
public record GrantRequest(GrantSubjectType SubjectType, string SubjectId, SharePermission Permission, GrantEffect? Effect, bool? InheritToChildren);

/// <summary>共享空间：独立一级资源，支持授权给 Pool / 用户（RO / RW / MANAGE，含向下继承与显式拒绝），并提供文件管理与权限解释。</summary>
public class SharedSpacesController(ActorAccessor accessor, SharedSpaceService spaces, PermissionEngine engine, AuditService audit, FileAccessService files, DshDbContext db, Microsoft.Extensions.Options.IOptions<Teager.Dsh.Infrastructure.Options.DshOptions> opt) : ApiBase(accessor)
{
    /// <summary>我可访问的共享空间。</summary>
    [HttpGet("shared-spaces"), Scope(Scopes.ShareRead)]
    public async Task<object> List([FromQuery] string? ownerPoolId, [FromQuery] bool includeArchived = false) => await spaces.ListAsync(Me, ownerPoolId, includeArchived);

    /// <summary>创建共享空间。</summary>
    [HttpPost("shared-spaces"), Scope(Scopes.ShareManage)]
    public async Task<object> Create(CreateSpaceRequest req)
    {
        var s = await spaces.CreateAsync(Me, req.Name, req.Description, string.IsNullOrEmpty(req.OwnerPoolId) ? null : req.OwnerPoolId, req.QuotaMb ?? 0, req.MountName);
        await audit.LogAsync(Me, "sharedspace.create", "SharedSpace", s.Id, s.OwnerPoolId, detail: $"创建共享空间「{s.Name}」");
        return new { s.Id, s.Name, s.MountName };
    }

    /// <summary>共享空间详情。</summary>
    [HttpGet("shared-spaces/{id}"), Scope(Scopes.ShareRead)]
    public async Task<object> Get(string id)
    {
        var (s, a) = await spaces.RequireAsync(Me, id, SharePermission.RO);
        var graph = await engine.GetGraphAsync();
        var visibleWorkspaces = await new WorkspaceListHelper(db, engine).AccessibleWorkspaceIdsAsync(Me);
        var mounts = await db.WorkspaceSharedSpaceMounts.AsNoTracking().Where(m => m.SharedSpaceId == id && visibleWorkspaces.Contains(m.WorkspaceId)).CountAsync();
        return new
        {
            s.Id, s.Name, s.Description, s.MountName, s.OwnerPoolId, OwnerPoolName = s.OwnerPoolId == null ? "组织级" : graph.NameOf(s.OwnerPoolId), s.QuotaMb, s.StorageBytes, Status = s.Status.ToString(), s.CreatedAt,
            My = new { Permission = a.Permission?.ToString(), a.Source, ViaPool = a.ViaPoolId == null ? null : graph.NameOf(a.ViaPoolId), a.Path, a.Notes }, Mounts = mounts
        };
    }

    /// <summary>更新共享空间。</summary>
    [HttpPatch("shared-spaces/{id}"), Scope(Scopes.ShareManage)]
    public async Task<object> Update(string id, UpdateSpaceRequest req)
    {
        var s = await spaces.UpdateAsync(Me, id, req.Name, req.Description, req.QuotaMb, req.MountName);
        await audit.LogAsync(Me, "sharedspace.update", "SharedSpace", id, s.OwnerPoolId, detail: $"更新共享空间「{s.Name}」");
        return new { s.Id, s.Name };
    }

    /// <summary>删除共享空间：默认归档（数据保留）；permanent=true 且无挂载时永久删除。</summary>
    [HttpDelete("shared-spaces/{id}"), Scope(Scopes.ShareManage)]
    public async Task<IActionResult> Delete(string id, [FromQuery] bool permanent = false)
    {
        var s = await db.SharedSpaces.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("共享空间不存在或无权访问");
        await spaces.DeleteAsync(Me, id, permanent);
        await audit.LogAsync(Me, "sharedspace.delete", "SharedSpace", id, s.OwnerPoolId, detail: $"{(permanent ? "永久删除" : "归档")}共享空间「{s.Name}」");
        return NoContent();
    }

    // ─────────── 授权 ───────────

    [HttpPost("shared-spaces/{id}/restore"), Scope(Scopes.ShareManage)]
    public async Task<IActionResult> Restore(string id)
    {
        await spaces.RestoreAsync(Me, id);
        await audit.LogAsync(Me, "sharedspace.restore", "SharedSpace", id);
        return NoContent();
    }

    /// <summary>授权列表。</summary>
    [HttpGet("shared-spaces/{id}/grants"), Scope(Scopes.ShareRead)]
    public async Task<object> Grants(string id)
    {
        await spaces.RequireAsync(Me, id, SharePermission.MANAGE);
        return await spaces.ListGrantsAsync(id);
    }

    /// <summary>新增 / 更新授权。</summary>
    [HttpPost("shared-spaces/{id}/grants"), Scope(Scopes.ShareManage)]
    public async Task<object> AddGrant(string id, GrantRequest req)
    {
        var g = await spaces.UpsertGrantAsync(Me, id, req.SubjectType, req.SubjectId, req.Permission, req.Effect ?? GrantEffect.Allow, req.InheritToChildren ?? true);
        var space = await db.SharedSpaces.AsNoTracking().FirstAsync(s => s.Id == id);
        await audit.LogAsync(Me, "sharedspace.grant.add", "SharedSpace", id, space.OwnerPoolId, detail: $"{req.SubjectType} {req.SubjectId} → {(req.Effect == GrantEffect.Deny ? "拒绝" : req.Permission.ToString())}，向下继承：{g.InheritToChildren}");
        return new { g.Id };
    }

    /// <summary>删除授权。</summary>
    [HttpDelete("shared-spaces/{id}/grants/{grantId}"), Scope(Scopes.ShareManage)]
    public async Task<IActionResult> DeleteGrant(string id, string grantId)
    {
        var g = await spaces.DeleteGrantAsync(Me, id, grantId);
        var space = await db.SharedSpaces.AsNoTracking().FirstAsync(s => s.Id == id);
        await audit.LogAsync(Me, "sharedspace.grant.remove", "SharedSpace", id, space.OwnerPoolId, detail: $"撤销授权 {g.SubjectType} {g.SubjectId}（{g.Permission}）");
        return NoContent();
    }

    /// <summary>已挂载该共享空间的 Workspace 列表（仅管理者可见）。</summary>
    [HttpGet("shared-spaces/{id}/mounts"), Scope(Scopes.ShareManage)]
    public async Task<object> Mounts(string id)
    {
        await spaces.RequireAsync(Me, id, SharePermission.MANAGE);
        var graph = await engine.GetGraphAsync();
        var rows = await (from m in db.WorkspaceSharedSpaceMounts.AsNoTracking() where m.SharedSpaceId == id
                          join w in db.Workspaces.AsNoTracking() on m.WorkspaceId equals w.Id
                          select new { m.Id, WorkspaceId = w.Id, WorkspaceName = w.Name, w.PoolId, m.MountName, m.Enabled, Override = m.PermissionOverride }).ToListAsync();
        return rows.Select(r => new { r.Id, r.WorkspaceId, r.WorkspaceName, PoolName = graph.NameOf(r.PoolId), r.MountName, r.Enabled, Override = r.Override?.ToString() });
    }

    /// <summary>权限来源解释：我的有效权限、来源与逐步说明。</summary>
    [HttpGet("shared-spaces/{id}/permissions/explain"), Scope(Scopes.ShareRead)]
    public async Task<object> Explain(string id)
    {
        var (s, a) = await spaces.RequireAsync(Me, id, SharePermission.RO);
        var graph = await engine.GetGraphAsync();
        return new { s.Id, s.Name, Permission = a.Permission?.ToString(), a.Source, ViaPool = a.ViaPoolId == null ? null : graph.NameOf(a.ViaPoolId), a.Path, a.Notes };
    }

    // ─────────── 文件 ───────────

    /// <summary>列出目录。</summary>
    [HttpGet("shared-spaces/{id}/files"), Scope(Scopes.ShareRead)]
    public async Task<object> ListFiles(string id, [FromQuery] string? path)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, false);
        return new { a.Label, a.Writable, Path = FileStore.Normalize(path), Entries = FileStore.List(a.Root, path) };
    }

    /// <summary>读取文本内容。</summary>
    [HttpGet("shared-spaces/{id}/files/content"), Scope(Scopes.ShareRead)]
    public async Task<object> Read(string id, [FromQuery] string path)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, false);
        var text = await FileStore.ReadTextAsync(a.Root, path);
        await files.RecordAsync(Me, a, "read", path);
        return new { Path = FileStore.Normalize(path), Content = text };
    }

    /// <summary>写入文本内容。</summary>
    [HttpPut("shared-spaces/{id}/files/content"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> Write(string id, WriteFileRequest req)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, true);
        var bytes = Encoding.UTF8.GetBytes(req.Content ?? "");
        await files.EnsureQuotaAsync(a, bytes.Length, req.Path);
        files.VersionBefore(a, req.Path);
        await FileStore.WriteAsync(a.Root, req.Path, new MemoryStream(bytes), (long)opt.Value.Platform.MaxUploadMb * 1024 * 1024);
        await files.RecordAsync(Me, a, "write", req.Path);
        return NoContent();
    }

    /// <summary>上传文件。</summary>
    [HttpPost("shared-spaces/{id}/files/upload"), Scope(Scopes.ShareWrite)]
    public async Task<object> Upload(string id, [FromQuery] string? path)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, true);
        var saved = new List<string>();
        foreach (var f in Request.Form.Files)
        {
            var name = Path.GetFileName(f.FileName);
            if (string.IsNullOrEmpty(name)) continue;
            var rel = FileStore.Normalize(path); rel = rel.Length == 0 ? name : rel + "/" + name;
            await files.EnsureQuotaAsync(a, f.Length, rel);
            files.VersionBefore(a, rel);
            await using var s = f.OpenReadStream();
            await FileStore.WriteAsync(a.Root, rel, s, (long)opt.Value.Platform.MaxUploadMb * 1024 * 1024);
            await files.RecordAsync(Me, a, "write", rel, $"上传 {f.Length} 字节");
            saved.Add(rel);
        }
        return new { Saved = saved };
    }

    /// <summary>创建目录。</summary>
    [HttpPost("shared-spaces/{id}/files/mkdir"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> Mkdir(string id, PathRequest req)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, true);
        await files.EnsureQuotaAsync(a, 0, req.Path);
        FileStore.Mkdir(a.Root, req.Path);
        await files.RecordAsync(Me, a, "mkdir", req.Path);
        return NoContent();
    }

    /// <summary>删除文件或目录。</summary>
    [HttpDelete("shared-spaces/{id}/files"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> DeleteFile(string id, [FromQuery] string path)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, true);
        files.VersionBefore(a, path);
        FileStore.Delete(a.Root, path);
        await files.RecordAsync(Me, a, "delete", path);
        return NoContent();
    }

    /// <summary>重命名 / 移动。</summary>
    [HttpPost("shared-spaces/{id}/files/move"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> Move(string id, MoveFileRequest req)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, true);
        FileStore.Move(a.Root, req.From, req.To);
        var sameDir = Path.GetDirectoryName(FileStore.Normalize(req.From)) == Path.GetDirectoryName(FileStore.Normalize(req.To));
        await files.RecordAsync(Me, a, sameDir ? "rename" : "move", req.From, req.To);
        return NoContent();
    }

    /// <summary>文件的历史版本列表。</summary>
    [HttpGet("shared-spaces/{id}/files/versions"), Scope(Scopes.ShareRead)]
    public async Task<object> FileVersions(string id, [FromQuery] string path)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, false);
        return files.Versions(a, path);
    }

    /// <summary>读取某历史版本的文本内容。</summary>
    [HttpGet("shared-spaces/{id}/files/versions/content"), Scope(Scopes.ShareRead)]
    public async Task<object> FileVersionContent(string id, [FromQuery] string path, [FromQuery] string version)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, false);
        return new { Path = FileStore.Normalize(path), Version = version, Content = await files.ReadVersionAsync(a, path, version) };
    }

    /// <summary>恢复到某历史版本（恢复前会保存当前内容，可再次撤销）。</summary>
    [HttpPost("shared-spaces/{id}/files/versions/restore"), Scope(Scopes.ShareWrite)]
    public async Task<IActionResult> RestoreFileVersion(string id, RestoreVersionRequest req)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, true);
        await files.RestoreVersionAsync(Me, a, req.Path, req.Version);
        return NoContent();
    }

    /// <summary>下载文件。</summary>
    [HttpGet("shared-spaces/{id}/files/download"), Scope(Scopes.ShareRead)]
    public async Task<IActionResult> Download(string id, [FromQuery] string path)
    {
        var a = await files.ResolveSharedAreaAsync(Me, id, false);
        var stream = FileStore.OpenRead(a.Root, path);
        await files.RecordAsync(Me, a, "read", path, "下载");
        return File(stream, "application/octet-stream", Path.GetFileName(FileStore.Normalize(path)));
    }
}
