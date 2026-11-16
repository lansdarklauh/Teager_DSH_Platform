using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Runtime;

public record PreviewLease(Actor Actor, string ExecutionId, string WorkspaceId, int ApplicationPort, string Host, string PlatformOrigin, DateTime ExpiresAt);
public record PreviewTarget(int IngressPort, string Secret, int ApplicationPort, string PlatformOrigin, DateTime ExpiresAt);

public partial class DockerSandbox
{
    private readonly ConcurrentDictionary<string, (int Port, string Secret)> ingress = new();
    private readonly ConcurrentDictionary<string, PreviewLease> previews = new(StringComparer.OrdinalIgnoreCase);

    public object IssuePreview(Actor actor, WorkspaceExecution execution, int port, string platformOrigin)
    {
        if (execution.Status != "Running" || !ingress.ContainsKey(execution.Id)) throw ApiException.Conflict("应用未运行或没有预览代理");
        if (!(JsonSerializer.Deserialize<int[]>(execution.PortsJson) ?? []).Contains(port)) throw ApiException.Forbidden("该端口未在应用启动时声明");
        var template = Settings.PreviewBaseUrl;
        if (template == null || !template.Contains("{execution}")) throw new ApiException(503, "preview_unavailable", "请管理员配置带 {execution} 的独立预览域名模板");
        var label = "p" + execution.Id[^8..] + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (!Uri.TryCreate(template.Replace("{execution}", label), UriKind.Absolute, out var target) || target.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(target.UserInfo) || target.AbsolutePath != "/" || target.Query != "" || target.Fragment != "") throw ApiException.BadRequest("预览模板必须为独立 HTTP(S) 域名的根地址");
        var platform = new Uri(platformOrigin);
        if (target.Host == platform.Host || !target.Host.Contains(label + ".", StringComparison.Ordinal)) throw ApiException.Forbidden("应用预览必须使用每次授权独立的随机域名，不能与平台同源");
        if (target.Scheme == "http" && !target.Host.EndsWith(".localhost", StringComparison.Ordinal)) throw ApiException.Forbidden("生产预览必须使用 HTTPS");
        foreach (var old in previews.Where(p => p.Value.ExpiresAt <= DateTime.Now).ToList()) previews.TryRemove(old.Key, out _);
        var expires = new[] { DateTime.Now.AddMinutes(10), execution.ExpiresAt }.Min();
        previews[target.Authority] = new PreviewLease(actor, execution.Id, execution.WorkspaceId, port, target.Authority, platformOrigin, expires);
        return new { Url = target.ToString(), ExpiresAt = expires };
    }

    public bool IsPreviewHost(string authority) => previews.ContainsKey(authority);
    public async Task<PreviewTarget> AuthorizePreviewAsync(string authority, CancellationToken ct)
    {
        if (!previews.TryGetValue(authority, out var lease) || lease.ExpiresAt <= DateTime.Now) throw ApiException.NotFound("预览授权已过期");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DshDbContext>();
        var actor = await ExecutionAuthorization.RefreshAsync(db, lease.Actor, ct);
        actor.RequireScope(Scopes.WorkspaceExecute);
        await scope.ServiceProvider.GetRequiredService<PermissionEngine>().RequireWorkspaceAsync(actor, lease.WorkspaceId, WorkspaceRole.Member);
        if (!await db.Set<WorkspaceExecution>().AnyAsync(e => e.Id == lease.ExecutionId && e.Status == "Running", ct) || !ingress.TryGetValue(lease.ExecutionId, out var target)) throw ApiException.NotFound("应用已停止");
        return new PreviewTarget(target.Port, target.Secret, lease.ApplicationPort, lease.PlatformOrigin, lease.ExpiresAt);
    }
}
