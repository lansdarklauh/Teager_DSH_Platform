using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Teager.Dsh.Infrastructure.Runtime;

public partial class DockerSandbox
{
    /// <summary>Freeze user processes while keeping trusted FUSE/brokers alive during host file operations.</summary>
    public async Task<IAsyncDisposable> AcquireFileLeaseAsync(CancellationToken ct = default)
    {
        await creation.WaitAsync(ct);
        var paused = new List<string>();
        try
        {
            if (Settings.Enabled)
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<Data.DshDbContext>();
                foreach (var container in await db.Set<Domain.WorkspaceExecution>().Where(e => e.Status == "Running").Select(e => e.ContainerName).ToListAsync(ct))
                {
                    var state = await CliAsync(["inspect", "--format", "{{.State.Running}} {{.State.Paused}}", container], ct);
                    if (state.Trim() == "true false")
                    {
                        try { await CliAsync(["exec", "--user", "0", container, "python3", "/opt/sandbox/freeze.py", "stop"], ct); paused.Add(container); }
                        catch
                        {
                            var current = await CliAsync(["inspect", "--format", "{{.State.Running}}", container], ct);
                            if (current.Trim() == "true") throw;
                        }
                    }
                }
                var running = (await db.Set<Domain.WorkspaceExecution>().Where(e => e.Status == "Running").Select(e => e.Id).ToListAsync(ct)).ToHashSet();
                var directory = System.IO.Path.Combine(paths.Data, "quota-locks");
                if (System.IO.Directory.Exists(directory)) foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
                {
                    var records = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(file, ct)) as JsonObject;
                    if (records == null) continue;
                    foreach (var key in records.Select(r => r.Key).ToList())
                        if (!running.Contains(key.Split(':')[0])) records.Remove(key);
                    await System.IO.File.WriteAllTextAsync(file, records.ToJsonString(), ct);
                }
            }
            return new FileLease(this, paused);
        }
        catch { await ReleaseFileLeaseAsync(paused); throw; }
    }

    private async Task ReleaseFileLeaseAsync(List<string> paused)
    {
        try
        {
            foreach (var container in paused)
                try { await CliAsync(["exec", "--user", "0", container, "python3", "/opt/sandbox/freeze.py", "resume"]); }
                catch (Exception ex) { logger.LogWarning(ex, "无法恢复隔离任务 {Container}", container); }
        }
        finally { creation.Release(); }
    }

    private sealed class FileLease(DockerSandbox sandbox, List<string> paused) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(sandbox.ReleaseFileLeaseAsync(paused));
    }
}
