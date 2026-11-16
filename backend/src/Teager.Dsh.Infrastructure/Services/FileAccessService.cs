using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>一次文件操作对应的存储区域（已完成权限校验）。</summary>
/// <param name="Root">区域根目录（绝对路径）。</param>
/// <param name="IsShared">是否共享空间区域。</param>
/// <param name="Workspace">所属 Workspace（共享空间直接访问时为空）。</param>
/// <param name="Space">共享空间（私有文件区域为空）。</param>
/// <param name="Writable">是否可写。</param>
/// <param name="Label">展示用名称。</param>
public record FileArea(string Root, bool IsShared, Workspace? Workspace, SharedSpace? Space, bool Writable, string Label);

/// <summary>文件访问服务：把“Workspace 私有文件 / Workspace 内挂载的共享空间 / 直接访问共享空间”统一解析为受限存储区域，并记录共享空间审计。</summary>
public class FileAccessService(DshDbContext db, PermissionEngine engine, StoragePaths paths, WorkspaceService workspaces, SharedSpaceService spaces, AuditService audit, FileVersionStore versions, Microsoft.Extensions.Options.IOptions<Teager.Dsh.Infrastructure.Options.DshOptions> options)
{
    /// <summary>解析 Workspace 内的存储区域：area 为 <c>files</c>（私有文件）或 <c>shared/{挂载名}</c>（共享空间）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="area">区域标识。</param>
    /// <param name="write">是否需要写权限。</param>
    /// <returns>存储区域。</returns>
    public async Task<FileArea> ResolveWorkspaceAreaAsync(Actor actor, string workspaceId, string? area, bool write)
    {
        area = string.IsNullOrWhiteSpace(area) ? "files" : area.Trim().Trim('/');
        if (area == "files")
        {
            var (ws, wa) = await engine.RequireWorkspaceAsync(actor, workspaceId, write ? WorkspaceRole.Member : WorkspaceRole.Viewer);
            if (write && ws.Status != WorkspaceStatus.Active) throw ApiException.Conflict("Workspace 已归档，不能修改文件");
            Directory.CreateDirectory(paths.WorkspaceFiles(ws.Id));
            return new FileArea(paths.WorkspaceFiles(ws.Id), false, ws, null, wa.Role >= WorkspaceRole.Member, "私有文件");
        }
        if (!area.StartsWith("shared/", StringComparison.Ordinal)) throw ApiException.BadRequest("area 只能是 files 或 shared/{挂载名}");
        var mountName = area["shared/".Length..];
        var (w, acc) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Viewer);
        var mount = await db.WorkspaceSharedSpaceMounts.AsNoTracking().FirstOrDefaultAsync(m => m.WorkspaceId == w.Id && m.MountName == mountName && m.Enabled) ?? throw ApiException.NotFound("共享空间挂载不存在或未启用");
        var space = await db.SharedSpaces.FirstOrDefaultAsync(s => s.Id == mount.SharedSpaceId && s.Status == SharedSpaceStatus.Active) ?? throw ApiException.NotFound("共享空间不存在");
        var eff = await engine.GetWorkspaceSpaceAccessAsync(actor, w, acc, space, mount);
        if (eff.Permission == null) throw ApiException.Forbidden("当前没有该共享空间的访问权限：" + string.Join("；", eff.Notes));
        var canWrite = eff.Permission >= SharePermission.RW && w.Status == WorkspaceStatus.Active;
        if (write && !canWrite) throw ApiException.Forbidden($"共享空间「{space.Name}」在当前 Workspace 内为只读（{eff.Permission}）");
        Directory.CreateDirectory(paths.SharedSpace(space.Id));
        return new FileArea(paths.SharedSpace(space.Id), true, w, space, canWrite, $"共享空间：{space.Name}");
    }

    /// <summary>解析直接访问共享空间的存储区域（不经过 Workspace）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="spaceId">共享空间 ID。</param>
    /// <param name="write">是否需要写权限。</param>
    /// <returns>存储区域。</returns>
    public async Task<FileArea> ResolveSharedAreaAsync(Actor actor, string spaceId, bool write)
    {
        var (space, acc) = await spaces.RequireAsync(actor, spaceId, write ? SharePermission.RW : SharePermission.RO);
        if (write && space.Status != SharedSpaceStatus.Active) throw ApiException.Conflict("共享空间已归档，不能修改");
        Directory.CreateDirectory(paths.SharedSpace(space.Id));
        return new FileArea(paths.SharedSpace(space.Id), true, null, space, acc.Permission >= SharePermission.RW, $"共享空间：{space.Name}");
    }

    /// <summary>历史版本的区域键：共享空间用其自身 ID（无论从哪条路径访问版本都一致），私有文件用 Workspace ID。</summary>
    /// <param name="a">存储区域。</param>
    private static string AreaKey(FileArea a) => a.IsShared ? a.Space!.Id : a.Workspace!.Id;

    /// <summary>覆盖 / 删除文件前保存旧内容为历史版本。</summary>
    /// <param name="a">存储区域。</param>
    /// <param name="path">相对路径。</param>
    public void VersionBefore(FileArea a, string path) => versions.Snapshot(AreaKey(a), a.Root, path);

    /// <summary>列出文件的历史版本。</summary>
    /// <param name="a">存储区域。</param>
    /// <param name="path">相对路径。</param>
    public List<FileVersion> Versions(FileArea a, string path) => versions.List(AreaKey(a), path);

    /// <summary>读取某历史版本的文本。</summary>
    /// <param name="a">存储区域。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="version">版本号。</param>
    public Task<string> ReadVersionAsync(FileArea a, string path, string version) => versions.ReadAsync(AreaKey(a), path, version);

    /// <summary>恢复到某历史版本，并记录审计。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="a">存储区域。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="version">版本号。</param>
    public async Task RestoreVersionAsync(Actor actor, FileArea a, string path, string version)
    {
        await EnsureQuotaAsync(a, versions.Size(AreaKey(a), path, version), path);
        versions.Restore(AreaKey(a), a.Root, path, version);
        await RecordAsync(actor, a, "write", path, "恢复到历史版本 " + version);
    }

    /// <summary>写入前检查配额（私有文件按 Workspace 策略，共享空间按其配额）。</summary>
    /// <param name="area">存储区域。</param>
    /// <param name="addBytes">新增字节数。</param>
    /// <param name="replacePath">Path being replaced, if any.</param>
    public async Task EnsureQuotaAsync(FileArea area, long addBytes, string? replacePath = null)
    {
        if (replacePath != null)
        {
            var target = FileStore.Resolve(area.Root, replacePath);
            if (File.Exists(target)) addBytes = Math.Max(0, addBytes - new FileInfo(target).Length);
            if (options.Value.Sandbox.Enabled && !File.Exists(target) && !Directory.Exists(target))
            {
                var count = StorageQuota.CountEntries(area.Root);
                if (!area.IsShared && area.Workspace != null) count += StorageQuota.CountEntries(Path.Combine(paths.WorkspaceRoot(area.Workspace.Id), "home"));
                var newEntries = 1;
                for (var parent = Path.GetDirectoryName(target); parent != null && parent != area.Root && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)) newEntries++;
                if (count + newEntries > options.Value.Sandbox.MaxFiles) throw ApiException.Quota("个人环境的文件数已达上限");
            }
        }
        if (area.IsShared && area.Space != null) await spaces.EnsureQuotaAsync(area.Space, addBytes);
        else if (area.Workspace != null) await workspaces.EnsureStorageQuotaAsync(area.Workspace, addBytes);
        if (options.Value.Sandbox.Enabled)
        {
            var used = FileStore.DirSize(area.Root);
            var id = AreaKey(area);
            if (!area.IsShared && area.Workspace != null) used += FileStore.DirSize(Path.Combine(paths.WorkspaceRoot(id), "home"));
            used += StorageQuota.OrphanBytes(paths, id);
            if (used + addBytes > (long)options.Value.Sandbox.MaxStorageMb * 1024 * 1024)
                throw ApiException.Quota("个人隔离环境的存储配额已满");
        }
    }

    /// <summary>记录共享空间审计（READ / WRITE / DELETE / RENAME / MOVE）；私有文件区域仅记录写与删除。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="area">存储区域。</param>
    /// <param name="op">操作：read / write / delete / rename / move / mkdir。</param>
    /// <param name="path">相对路径。</param>
    /// <param name="extra">附加说明（如目标路径）。</param>
    public async Task RecordAsync(Actor actor, FileArea area, string op, string path, string? extra = null)
    {
        var detail = $"{area.Label}：{path}" + (extra != null ? " → " + extra : "");
        if (area.IsShared && area.Space != null)
        {
            var poolId = area.Workspace?.PoolId ?? area.Space.OwnerPoolId;
            await audit.LogAsync(actor, "sharedspace." + op, "SharedSpace", area.Space.Id, poolId, area.Workspace?.Id, detail);
        }
        else if (area.Workspace != null && op != "read")
            await audit.LogAsync(actor, "workspace.file." + op, "Workspace", area.Workspace.Id, area.Workspace.PoolId, area.Workspace.Id, detail);
    }
}
