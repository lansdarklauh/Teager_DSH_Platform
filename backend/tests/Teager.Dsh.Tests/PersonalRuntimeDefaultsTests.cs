using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;
using Xunit;

namespace Teager.Dsh.Tests;

public class PersonalRuntimeDefaultsTests
{
    [Fact]
    public async Task Default_Provider_Updates_Existing_And_New_Personal_Users_Without_Losing_Data()
    {
        await using var env = await TestEnv.CreateAsync();
        var personal = env.Get<PersonalWorkspaceService>();
        var alice = await env.UserAsync("alice");
        var workspace = (await personal.EnsureAsync(alice.UserId))!;
        var session = await env.Get<SessionService>().CreateAsync(alice, workspace.Id, "keep history", null);
        session.RuntimeSessionId = "mock-old-context";
        env.Db.SessionMessages.Add(new SessionMessage { SessionId = session.Id, Role = "user", Content = "keep message" });
        env.Db.ConfigItems.Add(new ConfigItem { Id = "configured-model", Scope = ConfigScope.Platform, Kind = ConfigKind.Model, Key = "existing-model", ParamsJson = "{\"isDefault\":true}" });
        env.Db.Runtimes.Add(new RuntimeInstance { Id = "personal-runtime", PoolId = workspace.PoolId, Provider = "Mock", NodeId = "local", Status = RuntimeStatus.IDLE });
        await env.Db.SaveChangesAsync();
        var file = Path.Combine(env.Get<StoragePaths>().WorkspaceFiles(workspace.Id), "keep.txt");
        await File.WriteAllTextAsync(file, "keep personal file");
        env.Get<IOptions<DshOptions>>().Value.Runtime.DefaultProvider = "DeepSeekHarness";

        Assert.Equal(1, await personal.SynchronizePoolProviderAsync());
        Assert.Equal(0, await personal.SynchronizePoolProviderAsync());
        Assert.Equal(workspace.Id, (await personal.EnsureAsync(alice.UserId))!.Id);
        var bob = await env.UserAsync("bob");
        var second = (await personal.EnsureAsync(bob.UserId))!;
        Assert.NotEqual(workspace.Id, second.Id);
        Assert.Equal(workspace.PoolId, second.PoolId);
        Assert.Equal("DeepSeekHarness", (await env.Db.Pools.SingleAsync(p => p.Id == workspace.PoolId)).RuntimeProvider);
        Assert.Equal("DeepSeekHarness", (await env.Db.Runtimes.SingleAsync()).Provider);
        Assert.Equal(RuntimeStatus.STOPPED, (await env.Db.Runtimes.SingleAsync()).Status);
        Assert.Null(session.RuntimeSessionId);
        Assert.Equal("keep message", (await env.Db.SessionMessages.SingleAsync()).Content);
        Assert.Equal("{\"isDefault\":true}", (await env.Db.ConfigItems.SingleAsync()).ParamsJson);
        Assert.Equal("keep personal file", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Synchronization_Is_Optional_And_Preserves_Custom_Pools_And_Terminal_Denials()
    {
        await using var env = await TestEnv.CreateAsync();
        var custom = await env.PoolAsync("explicit mock pool");
        var user = await env.UserAsync("user");
        var personal = env.Get<PersonalWorkspaceService>();
        var workspace = (await personal.EnsureAsync(user.UserId))!;
        var pool = await env.Db.Pools.SingleAsync(p => p.Id == workspace.PoolId);
        pool.PolicyJson = "{\"allowTerminal\":false}";
        await env.Db.SaveChangesAsync();
        var options = env.Get<IOptions<DshOptions>>().Value;
        options.Runtime.DefaultProvider = "DeepSeekHarness";
        options.Runtime.SynchronizePersonalPoolProvider = false;
        Assert.Equal(0, await personal.SynchronizePoolProviderAsync());
        Assert.Equal("Mock", pool.RuntimeProvider);
        options.Runtime.SynchronizePersonalPoolProvider = true;
        Assert.Equal(1, await personal.SynchronizePoolProviderAsync());
        Assert.Equal("Mock", custom.RuntimeProvider);
        Assert.False((await env.Get<PolicyService>().GetEffectiveAsync(pool.Id)).AllowTerminal);
    }
}
