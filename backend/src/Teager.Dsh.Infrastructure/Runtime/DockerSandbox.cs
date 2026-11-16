using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>Trusted supervisor; user commands never execute on the API host.</summary>
public partial class DockerSandbox(IServiceScopeFactory scopes, IOptions<DshOptions> options, StoragePaths paths, PermissionCache permissions, ILogger<DockerSandbox> logger) : BackgroundService
{
    private readonly SemaphoreSlim creation = new(1);
    private readonly ConcurrentDictionary<string, (Actor Actor, long Version)> active = new();
    private readonly ConcurrentDictionary<string, List<(string SpaceId, string MountName, SharePermission Permission)>> mounted = new();
    private SandboxOptions Settings => options.Value.Sandbox;
    public string Image => Settings.Image;

    public void RequireEnabled()
    {
        if (!Settings.Enabled) throw new ApiException(503, "environment_unavailable", "个人隔离环境未就绪，请管理员配置 Sandbox；不会回退到宿主命令执行");
        if (Settings.MemoryMb < 64 || Settings.Cpus <= 0 || Settings.MaxProcesses < 8 || Settings.MaxStorageMb <= 0 || Settings.MaxExecutions < 1 || Settings.MaxMinutes < 1 || Settings.MaxFiles < 1)
            throw new ApiException(503, "environment_unavailable", "隔离环境必须设置有效的有限资源上限");
    }

    public async Task<WorkspaceExecution> StartAsync(Actor actor, string workspaceId, string[] command, bool terminal = false, CancellationToken ct = default, IsolatedAgentSpec? agent = null, int[]? ports = null)
    {
        RequireEnabled(); actor.RequireScope(Scopes.FilesRead); actor.RequireScope(Scopes.FilesWrite);
        ports = (ports ?? []).Distinct().ToArray();
        if (ports.Length > 5 || ports.Any(p => p is < 1024 or > 65535 || p is 8765 or 8766 or 8767)) throw ApiException.BadRequest("最多声明 5 个应用端口（1024—65535），8765—8767 为平台保留端口");
        await using (await AcquireFileLeaseAsync(ct)) { }
        await creation.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DshDbContext>();
            var engine = scope.ServiceProvider.GetRequiredService<PermissionEngine>();
            actor = await ExecutionAuthorization.RefreshAsync(db, actor, ct);
            var (ws, wa) = await engine.RequireWorkspaceAsync(actor, workspaceId, WorkspaceRole.Member);
            var policy = await scope.ServiceProvider.GetRequiredService<PolicyService>().GetEffectiveAsync(ws.PoolId);
            if (terminal && !policy.AllowTerminal) throw ApiException.Forbidden("终端被管理员策略禁用");
            if (await db.Set<WorkspaceExecution>().CountAsync(e => e.WorkspaceId == workspaceId && e.Status == "Running", ct) >= Settings.MaxExecutions) throw ApiException.Quota("个人环境的并发执行数已达上限");
            var execution = new WorkspaceExecution { Id = IdGen.New("exec"), WorkspaceId = ws.Id, UserId = actor.UserId, TokenId = actor.TokenId, Kind = terminal ? "terminal" : "command", ExpiresAt = DateTime.Now.AddMinutes(Settings.MaxMinutes) };
            execution.ContainerName = "dsh-usage-" + execution.Id;
            execution.PortsJson = JsonSerializer.Serialize(ports);
            var minutes = policy.MaxRunMinutes > 0 ? Math.Min(policy.MaxRunMinutes, Settings.MaxMinutes) : Settings.MaxMinutes;
            execution.ExpiresAt = DateTime.Now.AddMinutes(minutes);
            if (agent != null) { execution.Kind = "agent"; execution.ExpiresAt = new[] { execution.ExpiresAt, DateTime.Now.Add(agent.Timeout) }.Min(); }
            var control = Path.Combine(paths.Data, "execution-control", execution.Id);
            Directory.CreateDirectory(control);
            var home = Path.Combine(paths.WorkspaceRoot(ws.Id), "home"); Directory.CreateDirectory(home);
            Directory.CreateDirectory(paths.WorkspaceFiles(ws.Id));
            var locks = Path.Combine(paths.Data, "quota-locks"); Directory.CreateDirectory(locks);
            var mounts = new List<object>();
            var authorizations = new List<(string, string, SharePermission)>();
            var args = new List<string> { "create", "--name", execution.ContainerName, "--label", "teager.managed=usage-only", "--read-only", "--network", "none", "--cap-drop", "ALL", "--cap-add", "SYS_ADMIN", "--cap-add", "SETUID", "--cap-add", "SETGID", "--cap-add", "SYS_CHROOT", "--security-opt", "no-new-privileges", "--security-opt", "apparmor=unconfined", "--device", "/dev/fuse", "--pids-limit", Settings.MaxProcesses.ToString(), "--memory", Settings.MemoryMb + "m", "--memory-swap", Settings.MemoryMb + "m", "--cpus", Settings.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture), "--tmpfs", "/sandbox:rw,size=16m,mode=755", "--log-opt", "max-size=1m", "--log-opt", "max-file=1" };
            args[args.IndexOf("none")] = "bridge";
            args.AddRange(["--cap-add", "NET_ADMIN", "--cap-add", "KILL"]);
            var previewSecret = AuthService.RandomToken();
            if (ports.Length > 0) args.AddRange(["--publish", "127.0.0.1::8766"]);
            if (terminal) args.Add("--interactive");
            if (agent != null)
            {
                var managed = Path.Combine(control, "managed"); Directory.CreateDirectory(managed);
                foreach (var skill in agent.Skills)
                {
                    if (skill.Key.Contains('/') || skill.Key.Contains('\\') || skill.Key is "." or "..") throw ApiException.BadRequest("非法 Skill 名称");
                    var directory = Path.Combine(managed, "skills", skill.Key); Directory.CreateDirectory(directory);
                    var body = skill.Value.TrimStart().StartsWith("---") ? skill.Value : $"---\nname: {skill.Key}\ndescription: 平台授权技能\n---\n\n{skill.Value}";
                    await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), body, new UTF8Encoding(false), ct);
                }
                await File.WriteAllTextAsync(Path.Combine(managed, "agent.json"), agent.Patch, new UTF8Encoding(false), ct);
                await File.WriteAllTextAsync(Path.Combine(control, "prompt.txt"), agent.Prompt, new UTF8Encoding(false), ct);
            }
            Bind(args, paths.WorkspaceFiles(ws.Id), "/input/files"); Bind(args, home, "/input/home"); Bind(args, locks, "/locks"); Bind(args, control, "/control", true);
            var mountRows = await db.WorkspaceSharedSpaceMounts.Where(m => m.WorkspaceId == ws.Id && m.Enabled).ToListAsync(ct);
            var index = 0;
            foreach (var row in mountRows)
            {
                var share = await db.SharedSpaces.FirstOrDefaultAsync(s => s.Id == row.SharedSpaceId, ct);
                if (share == null) continue;
                var access = await engine.GetWorkspaceSpaceAccessAsync(actor, ws, wa, share, row);
                if (access.Permission == null) continue;
                var writable = access.Permission >= SharePermission.RW && !(agent != null && policy.SandboxMode == "read-only");
                authorizations.Add((share.Id, row.MountName, writable ? SharePermission.RW : SharePermission.RO));
                var source = paths.SharedSpace(share.Id); Directory.CreateDirectory(source);
                Bind(args, source, "/input/shared/" + index++, !writable);
                mounts.Add(new { name = row.MountName, id = share.Id, writable, storageBytes = (long)(share.QuotaMb > 0 ? Math.Min(share.QuotaMb, Settings.MaxStorageMb) : Settings.MaxStorageMb) * 1024 * 1024 });
            }
            var storageMb = policy.MaxStorageMbPerWorkspace > 0 ? Math.Min(policy.MaxStorageMbPerWorkspace, Settings.MaxStorageMb) : Settings.MaxStorageMb;
            var modelProxy = agent == null ? null : new { baseUrl = agent.BaseUrl, apiKey = agent.ApiKey, model = agent.Model, maxBudget = agent.Budget, catalogProvider = agent.CatalogProvider,
                allowFakeIp = Uri.TryCreate(agent.BaseUrl, UriKind.Absolute, out var modelUri) && Settings.ModelFakeIpHosts.Contains(modelUri.Host, StringComparer.OrdinalIgnoreCase) };
            var preview = ports.Length == 0 ? null : new { secret = previewSecret, ports };
            var networkHosts = policy.AllowNetwork ? Settings.NetworkAllowedHosts : [];
            await File.WriteAllTextAsync(Path.Combine(control, "job.json"), JsonSerializer.Serialize(new { executionId = execution.Id, workspaceId = ws.Id, command, terminal, readOnlyFiles = agent != null && policy.SandboxMode == "read-only", storageBytes = (long)storageMb * 1024 * 1024, maxFiles = Settings.MaxFiles, mounts, modelProxy, preview, networkHosts }), new UTF8Encoding(false), ct);
            args.Add(Settings.Image);
            try
            {
                await CliAsync(args, ct);
                db.Add(execution); await db.SaveChangesAsync(ct);
                active[execution.Id] = (actor, permissions.Version);
                mounted[execution.Id] = authorizations;
                await CliAsync(["start", execution.ContainerName], ct);
                if (ports.Length > 0)
                {
                    var endpoint = await CliAsync(["port", execution.ContainerName, "8766/tcp"], ct);
                    if (!System.Net.IPEndPoint.TryParse(endpoint.Trim(), out var published) || !System.Net.IPAddress.IsLoopback(published.Address)) throw ApiException.Conflict("预览代理必须仅绑定宿主回环地址");
                    ingress[execution.Id] = (published.Port, previewSecret);
                }
            }
            catch
            {
                active.TryRemove(execution.Id, out _);
                mounted.TryRemove(execution.Id, out _);
                try { await CliAsync(["rm", "--force", execution.ContainerName]); } catch { }
                execution.Status = "Failed";
                if (db.Entry(execution).State != EntityState.Detached) await db.SaveChangesAsync(CancellationToken.None);
                Directory.Delete(control, true);
                throw;
            }
            return execution;
        }
        finally { creation.Release(); }
    }

    private void Bind(List<string> args, string source, string target, bool readOnly = false)
    {
        source = Path.GetFullPath(source);
        if (Settings.HostDataDir != null)
        {
            var relative = Path.GetRelativePath(paths.Data, source);
            if (relative.StartsWith("..")) throw ApiException.Forbidden();
            source = Settings.HostDataDir.TrimEnd('/', '\\') + "/" + relative.Replace('\\', '/');
        }
        if (source.Contains(',')) throw ApiException.BadRequest("数据目录不能包含逗号");
        args.Add("--mount"); args.Add($"type=bind,source={source},target={target}" + (readOnly ? ",readonly" : ""));
    }

    public Process Attach(WorkspaceExecution execution)
    {
        var info = new ProcessStartInfo(Settings.DockerExecutable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true };
        foreach (var argument in new[] { "attach", execution.ContainerName }) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("无法连接隔离终端");
    }

    public async Task<string> LogsAsync(WorkspaceExecution execution, CancellationToken ct = default) => await CliAsync(["logs", "--tail", "1000", execution.ContainerName], ct);

    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        await creation.WaitAsync(ct);
        try { await StopCoreAsync(id, ct); }
        finally { creation.Release(); }
    }

    private async Task StopCoreAsync(string id, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DshDbContext>();
        var row = await db.Set<WorkspaceExecution>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row == null || row.Status != "Running") return;
        try { await CliAsync(["stop", "--time", "2", row.ContainerName], ct); }
        catch { await CliAsync(["kill", row.ContainerName], CancellationToken.None); }
        row.Status = "Stopped"; await db.SaveChangesAsync(ct); active.TryRemove(id, out _);
        mounted.TryRemove(id, out _);
        CleanControl(row.Id);
    }

    private void CleanControl(string id)
    {
        ingress.TryRemove(id, out _);
        var root = Path.GetFullPath(Path.Combine(paths.Data, "execution-control"));
        var directory = Path.GetFullPath(Path.Combine(root, id));
        if (Path.GetDirectoryName(directory) != root) throw new InvalidOperationException("非法执行目录");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    public async Task<string> CliAsync(IEnumerable<string> arguments, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(Settings.DockerExecutable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new ApiException(503, "environment_unavailable", "Docker 不可用");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var cancel = timeout.Token.Register(() => ProcessUtil.KillTree(process));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new ApiException(503, "environment_unavailable", "隔离环境操作失败：" + error[..Math.Min(error.Length, 500)]);
        return output + error;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Settings.Enabled)
                {
                    await creation.WaitAsync(stoppingToken);
                    try
                    {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<DshDbContext>();
                    foreach (var row in await db.Set<WorkspaceExecution>().Where(e => e.Status == "Running").ToListAsync(stoppingToken))
                    {
                        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == row.UserId, stoppingToken);
                        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == row.WorkspaceId, stoppingToken);
                        var token = row.TokenId == null ? null : await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == row.TokenId, stoppingToken);
                        var revoked = user == null || user.Status != UserStatus.Active || ws == null || ws.Status != WorkspaceStatus.Active || user.Role != PlatformRole.Admin && !user.IsServiceAccount && ws.AssignedUserId != row.UserId || row.TokenId != null && (token == null || token.RevokedAt != null || token.ExpiresAt <= DateTime.Now);
                        if (!revoked && active.TryGetValue(row.Id, out var existing))
                        {
                            try
                            {
                                var refreshed = await ExecutionAuthorization.RefreshAsync(db, existing.Actor, stoppingToken);
                                refreshed.RequireScope(Scopes.FilesRead); refreshed.RequireScope(Scopes.FilesWrite);
                                var engine = scope.ServiceProvider.GetRequiredService<PermissionEngine>();
                                var (currentWorkspace, currentAccess) = await engine.RequireWorkspaceAsync(refreshed, row.WorkspaceId, WorkspaceRole.Member);
                                if (existing.Version != permissions.Version)
                                {
                                    foreach (var old in mounted.GetValueOrDefault(row.Id) ?? [])
                                    {
                                        var share = await db.SharedSpaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == old.SpaceId, stoppingToken);
                                        var mount = await db.WorkspaceSharedSpaceMounts.AsNoTracking().FirstOrDefaultAsync(m => m.WorkspaceId == row.WorkspaceId && m.SharedSpaceId == old.SpaceId && m.MountName == old.MountName && m.Enabled, stoppingToken);
                                        if (share == null || mount == null || (await engine.GetWorkspaceSpaceAccessAsync(refreshed, currentWorkspace, currentAccess, share, mount)).Permission < old.Permission) { revoked = true; break; }
                                        if ((await engine.GetWorkspaceSpaceAccessAsync(refreshed, currentWorkspace, currentAccess, share, mount)).Permission == null) { revoked = true; break; }
                                    }
                                    active[row.Id] = (refreshed, permissions.Version);
                                }
                            }
                            catch (ApiException) { revoked = true; }
                        }
                        if (revoked || row.ExpiresAt <= DateTime.Now || !active.ContainsKey(row.Id)) { await StopCoreAsync(row.Id, stoppingToken); continue; }
                        var result = await CliAsync(["inspect", "--format", "{{.State.Running}} {{.State.ExitCode}}", row.ContainerName], stoppingToken);
                        if (result.Trim().StartsWith("false")) { row.Status = "Exited"; row.ExitCode = int.TryParse(result.Trim().Split(' ').Last(), out var code) ? code : null; active.TryRemove(row.Id, out _); mounted.TryRemove(row.Id, out _); CleanControl(row.Id); }
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    foreach (var old in await db.Set<WorkspaceExecution>().Where(e => e.Status != "Running" && e.CreatedAt < DateTime.Now.AddDays(-7)).ToListAsync(stoppingToken))
                    {
                        try { await CliAsync(["rm", old.ContainerName], stoppingToken); } catch { }
                        db.Remove(old);
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    }
                    finally { creation.Release(); }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "隔离进程状态检查失败"); }
            await Task.Delay(1000, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var id in active.Keys) await StopAsync(id, cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
