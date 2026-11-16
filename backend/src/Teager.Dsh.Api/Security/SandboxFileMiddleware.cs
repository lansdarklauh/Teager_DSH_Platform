using Teager.Dsh.Infrastructure.Runtime;

namespace Teager.Dsh.Api.Security;

/// <summary>Commands cannot swap a path between a trusted file API's validation and access.</summary>
public class SandboxFileMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DockerSandbox sandbox)
    {
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Any(s => s is "files" or "projects" or "snapshots" or "clone" or "restore" or "environment") ||
            context.Request.Method == "DELETE" && segments.Length == 4 && segments[2] is "workspaces" or "shared-spaces" or "pools")
        {
            await using var lease = await sandbox.AcquireFileLeaseAsync(context.RequestAborted);
            await next(context);
        }
        else await next(context);
    }
}
