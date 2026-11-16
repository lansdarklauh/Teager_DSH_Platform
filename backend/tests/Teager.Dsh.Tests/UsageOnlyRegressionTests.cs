using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;
using Xunit;

namespace Teager.Dsh.Tests;

public class UsageOnlyRegressionTests
{
    [Fact]
    public async Task Session_And_Runtime_Context_Deletion_Preserve_Personal_Files()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("cleanup");
        var workspaces = env.Get<WorkspaceService>();
        var ws = await workspaces.CreateAsync(env.Admin, pool.Id, "w", null);
        var sessions = env.Get<SessionService>();
        var paths = env.Get<StoragePaths>();
        var file = Path.Combine(paths.WorkspaceFiles(ws.Id), "personal.txt");
        await File.WriteAllTextAsync(file, "keep personal files");
        var session = await sessions.CreateAsync(env.Admin, ws.Id, "first", null);
        var log = paths.RunEventLog(session.Id, session.CreatedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllTextAsync(log, "event log");
        await sessions.DeleteAsync(env.Admin, session.Id);
        Assert.False(File.Exists(log));
        Assert.False(await env.Db.Sessions.AnyAsync(s => s.Id == session.Id));
        var second = await sessions.CreateAsync(env.Admin, ws.Id, "second", null);
        await workspaces.DeleteAsync(env.Admin, ws.Id, "runtime_context");
        Assert.False(await env.Db.Sessions.AnyAsync(s => s.Id == second.Id));
        Assert.True(await env.Db.Workspaces.AnyAsync(w => w.Id == ws.Id));
        Assert.Equal("keep personal files", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Deletion_Rollback_Restores_Files_And_Commit_Removes_Them()
    {
        await using var env = await TestEnv.CreateAsync();
        var paths = env.Get<StoragePaths>();
        var directory = Path.Combine(paths.Data, "cleanup-fixture");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "important.txt");
        await File.WriteAllTextAsync(file, "keep on rollback");
        using (var trash = new FileTrash(paths, "workspace", "fixture"))
        {
            trash.Stage(directory);
            Assert.False(Directory.Exists(directory));
        }
        Assert.Equal("keep on rollback", await File.ReadAllTextAsync(file));
        using (var trash = new FileTrash(paths, "workspace", "fixture")) { trash.Stage(directory); trash.Commit(); }
        Assert.False(Directory.Exists(directory));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(paths.Data, ".trash")));
    }

    [Fact]
    public async Task Workspace_Quota_Is_Atomic_Across_Concurrent_Creators()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("quota");
        pool.PolicyJson = "{\"maxWorkspaces\":1}";
        await env.Db.SaveChangesAsync();
        async Task<int> Create(string name)
        {
            await using var scope = env.Root.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<WorkspaceService>().CreateAsync(env.Admin, pool.Id, name, null); return 200; }
            catch (ApiException ex) { return ex.Status; }
        }
        var result = await Task.WhenAll(Create("one"), Create("two"));
        Assert.Equal(new[] { 200, 429 }, result.Order().ToArray());
        Assert.Equal(1, await env.Db.Workspaces.CountAsync(w => w.PoolId == pool.Id));
    }

    [Fact]
    public async Task Personal_Provisioning_Concurrent_Requests_Return_One_Workspace()
    {
        await using var env = await TestEnv.CreateAsync();
        var user = await env.UserAsync("parallel");
        async Task<string?> Ensure()
        {
            await using var scope = env.Root.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<PersonalWorkspaceService>().EnsureAsync(user.UserId))?.Id;
        }
        var result = await Task.WhenAll(Ensure(), Ensure());
        Assert.NotNull(result[0]); Assert.Equal(result[0], result[1]);
        Assert.Equal(1, await env.Db.Workspaces.CountAsync(w => w.AssignedUserId == user.UserId));
    }

    [Fact]
    public async Task Restricted_Admin_Token_Cannot_Manage_Pool_Or_Create_Outside_Workspace()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("own"); var other = await env.PoolAsync("other");
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "own", null);
        var actor = new Actor { UserId = env.Admin.UserId, IsPlatformAdmin = true, Via = "token", AllowedWorkspaces = [ws.Id], Scopes = [Scopes.PoolRead, Scopes.PoolManage, Scopes.WorkspaceWrite] };
        Assert.False((await env.Engine.GetPoolAccessAsync(actor, other.Id)).CanView);
        Assert.False((await env.Engine.GetPoolAccessAsync(actor, pool.Id)).CanManage);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => env.Get<WorkspaceService>().CreateAsync(actor, pool.Id, "escape", null))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => env.Get<ConfigAccess>().RequireViewAsync(actor, ConfigScope.Platform, ""))).Status);
    }

    [Fact]
    public async Task Service_Account_Needs_Explicit_Workspace_Membership()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("service");
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "target", null);
        var user = await env.UserAsync("automation");
        var actor = new Actor { UserId = user.UserId, IsServiceAccount = true, Via = "token", AllowedWorkspaces = [ws.Id] };
        Assert.Null((await env.Engine.GetWorkspaceAccessAsync(actor, ws)).Role);
        await env.Get<WorkspaceService>().UpsertMemberAsync(env.Admin, ws.Id, user.UserId, WorkspaceRole.Owner, GrantEffect.Allow);
        Assert.Equal(WorkspaceRole.Member, (await env.Engine.GetWorkspaceAccessAsync(actor, ws)).Role);
    }

    [Fact]
    public async Task Corrupt_Snapshot_Preserves_Current_Files()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("restore"); var service = env.Get<WorkspaceService>();
        var ws = await service.CreateAsync(env.Admin, pool.Id, "w", null);
        var paths = env.Get<StoragePaths>(); var file = Path.Combine(paths.WorkspaceFiles(ws.Id), "important.txt");
        await File.WriteAllTextAsync(file, "must survive");
        var snapshot = await service.CreateSnapshotAsync(env.Admin, ws.Id, "backup", null);
        await File.WriteAllTextAsync(paths.SnapshotFile(snapshot.Id), "not a zip file");
        await Assert.ThrowsAnyAsync<Exception>(() => service.RestoreSnapshotAsync(env.Admin, ws.Id, snapshot.Id));
        Assert.Equal("must survive", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Single_History_Version_Can_Be_Restored_And_Undone()
    {
        await using var env = await TestEnv.CreateAsync();
        env.Get<IOptions<DshOptions>>().Value.Platform.FileVersionsKeep = 1;
        var pool = await env.PoolAsync("history");
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "w", null);
        var root = env.Get<StoragePaths>().WorkspaceFiles(ws.Id); var file = Path.Combine(root, "file.txt");
        var versions = env.Get<FileVersionStore>();
        await File.WriteAllTextAsync(file, "original"); versions.Snapshot(ws.Id, root, "file.txt");
        var original = Assert.Single(versions.List(ws.Id, "file.txt")).Version;
        await File.WriteAllTextAsync(file, "current"); await Task.Delay(5);
        versions.Restore(ws.Id, root, "file.txt", original);
        Assert.Equal("original", await File.ReadAllTextAsync(file));
        var undo = Assert.Single(versions.List(ws.Id, "file.txt")).Version;
        await Task.Delay(5); versions.Restore(ws.Id, root, "file.txt", undo);
        Assert.Equal("current", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Active_Application_Prevents_Workspace_Deletion_And_Snapshot_Restore()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("lifecycle"); var service = env.Get<WorkspaceService>();
        var ws = await service.CreateAsync(env.Admin, pool.Id, "w", null);
        var snapshot = await service.CreateSnapshotAsync(env.Admin, ws.Id, "backup", null);
        env.Db.Add(new WorkspaceExecution { Id = "execution-test", WorkspaceId = ws.Id, UserId = env.Admin.UserId, Status = "Running" });
        await env.Db.SaveChangesAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.DeleteAsync(env.Admin, ws.Id, "permanent"))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RestoreSnapshotAsync(env.Admin, ws.Id, snapshot.Id))).Status);
        Assert.True(await env.Db.Workspaces.AnyAsync(w => w.Id == ws.Id));
    }
}
