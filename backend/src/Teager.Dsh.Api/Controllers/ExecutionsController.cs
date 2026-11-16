using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Runtime;

namespace Teager.Dsh.Api.Controllers;

public record CommandRequest(string Command, int[]? Ports = null);
public record PreviewRequest(int Port);

public class ExecutionsController(ActorAccessor accessor, DshDbContext db, PermissionEngine permissions, DockerSandbox sandbox) : ApiBase(accessor)
{
    [HttpGet("workspaces/{id}/environment"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> Environment_(string id)
    {
        await permissions.RequireWorkspaceAsync(Me, id, WorkspaceRole.Viewer);
        try { sandbox.RequireEnabled(); await sandbox.CliAsync(["image", "inspect", "--format", "{{.Id}}", sandbox.Image], HttpContext.RequestAborted); return new { Ready = true, Message = "隔离环境已配置，网络仅开放管理员指定的 HTTPS 依赖源；依赖安装在个人目录", FilesDirectory = "/workspace/files", HomeDirectory = "/home/user" }; }
        catch (ApiException ex) { return new { Ready = false, ex.Message, FilesDirectory = "/workspace/files", HomeDirectory = "/home/user" }; }
    }

    [HttpPost("workspaces/{id}/commands"), Scope(Scopes.WorkspaceExecute)]
    public async Task<object> Start(string id, CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 16000) throw ApiException.BadRequest("命令长度必须为 1—16000 字符");
        return await sandbox.StartAsync(Me, id, ["/bin/bash", "-lc", request.Command], ct: HttpContext.RequestAborted, ports: request.Ports);
    }

    [HttpGet("workspaces/{id}/executions"), Scope(Scopes.WorkspaceRead)]
    public async Task<object> List(string id)
    {
        await permissions.RequireWorkspaceAsync(Me, id, WorkspaceRole.Viewer);
        return await Query(id).OrderByDescending(e => e.CreatedAt).Take(50).ToListAsync();
    }

    [HttpGet("workspaces/{id}/executions/{executionId}/logs"), Scope(Scopes.WorkspaceExecute)]
    public async Task<object> Logs(string id, string executionId)
    {
        await permissions.RequireWorkspaceAsync(Me, id, WorkspaceRole.Viewer);
        var execution = await Query(id).FirstOrDefaultAsync(e => e.Id == executionId) ?? throw ApiException.NotFound();
        return new { Output = await sandbox.LogsAsync(execution, HttpContext.RequestAborted), execution.Status, execution.ExitCode };
    }

    [HttpDelete("workspaces/{id}/executions/{executionId}"), Scope(Scopes.WorkspaceExecute)]
    public async Task<IActionResult> Stop(string id, string executionId)
    {
        await permissions.RequireWorkspaceAsync(Me, id, WorkspaceRole.Member);
        if (!await Query(id).AnyAsync(e => e.Id == executionId)) throw ApiException.NotFound();
        await sandbox.StopAsync(executionId, HttpContext.RequestAborted); return NoContent();
    }

    private IQueryable<WorkspaceExecution> Query(string workspaceId)
        => db.Set<WorkspaceExecution>().Where(e => e.WorkspaceId == workspaceId && (Me.IsPlatformAdmin || e.UserId == Me.UserId) && (Me.TokenId == null || e.TokenId == Me.TokenId));

    [HttpPost("workspaces/{id}/executions/{executionId}/preview"), Scope(Scopes.WorkspaceExecute)]
    public async Task<object> Preview(string id, string executionId, PreviewRequest request)
    {
        await permissions.RequireWorkspaceAsync(Me, id, WorkspaceRole.Member);
        var execution = await Query(id).FirstOrDefaultAsync(e => e.Id == executionId) ?? throw ApiException.NotFound();
        return sandbox.IssuePreview(Me, execution, request.Port, $"{Request.Scheme}://{Request.Host}");
    }
}
