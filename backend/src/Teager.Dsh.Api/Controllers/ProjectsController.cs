using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Storage;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Api.Controllers;

public record ProjectRequest(string Name, string? Description);

public class ProjectsController(ActorAccessor accessor, DshDbContext db, PermissionEngine engine, StoragePaths paths, FileAccessService files) : ApiBase(accessor)
{
    [HttpGet("workspaces/{id}/projects"), Scope(Scopes.FilesRead)]
    public async Task<object> List(string id)
    {
        await engine.RequireWorkspaceAsync(Me, id, WorkspaceRole.Viewer);
        return await db.Set<WorkspaceProject>().Where(p => p.WorkspaceId == id).OrderBy(p => p.Name).ToListAsync();
    }

    [HttpPost("workspaces/{id}/projects"), Scope(Scopes.FilesWrite)]
    public async Task<object> Create(string id, ProjectRequest request)
    {
        await engine.RequireWorkspaceAsync(Me, id, WorkspaceRole.Member);
        var name = Name(request.Name);
        if (await db.Set<WorkspaceProject>().AnyAsync(p => p.WorkspaceId == id && p.Name == name)) throw ApiException.Conflict("同名项目已存在");
        var project = new WorkspaceProject { Id = IdGen.New("prj"), WorkspaceId = id, Name = name, Description = request.Description };
        project.Directory = "projects/" + project.Id;
        var area = await files.ResolveWorkspaceAreaAsync(Me, id, "files", true);
        await files.EnsureQuotaAsync(area, 0, project.Directory);
        FileStore.Mkdir(paths.WorkspaceFiles(id), project.Directory);
        db.Add(project); await db.SaveChangesAsync();
        return project;
    }

    [HttpPatch("workspaces/{id}/projects/{projectId}"), Scope(Scopes.FilesWrite)]
    public async Task<object> Update(string id, string projectId, ProjectRequest request)
    {
        await engine.RequireWorkspaceAsync(Me, id, WorkspaceRole.Member);
        var project = await db.Set<WorkspaceProject>().FirstOrDefaultAsync(p => p.WorkspaceId == id && p.Id == projectId) ?? throw ApiException.NotFound();
        var name = Name(request.Name);
        if (await db.Set<WorkspaceProject>().AnyAsync(p => p.WorkspaceId == id && p.Id != projectId && p.Name == name)) throw ApiException.Conflict("同名项目已存在");
        project.Name = name; project.Description = request.Description;
        await db.SaveChangesAsync(); return project;
    }

    [HttpDelete("workspaces/{id}/projects/{projectId}"), Scope(Scopes.FilesWrite)]
    public async Task<IActionResult> Remove(string id, string projectId)
    {
        await engine.RequireWorkspaceAsync(Me, id, WorkspaceRole.Member);
        var project = await db.Set<WorkspaceProject>().FirstOrDefaultAsync(p => p.WorkspaceId == id && p.Id == projectId) ?? throw ApiException.NotFound();
        // Remove the catalogue entry; files remain available in the file panel.
        db.Remove(project); await db.SaveChangesAsync(); return NoContent();
    }

    private static string Name(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 100) throw ApiException.BadRequest("项目名称长度必须为 1—100 字符");
        return name;
    }
}
