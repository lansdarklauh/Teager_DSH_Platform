using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Tests;

public class ModelExecutionLimitsTests
{
    [Fact]
    public async Task Limits_Save_Inherit_Validate_And_Only_Admin_Can_Change_Them()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("limits");
        var config = env.Get<ConfigService>();
        await config.UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Platform, "", new("native", null, true, "{\"model\":\"native\",\"taskBudget\":0,\"contextWindow\":0}", null));
        var inherited = (await config.ResolveAsync(pool.Id, null)).Single(m => m.Key == "native");
        Assert.Equal(new ModelExecutionLimits(), ModelExecutionLimits.FromJson(inherited.ParamsJson));
        await config.UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Pool, pool.Id, new("native", null, true, "{\"taskBudget\":3000,\"contextWindow\":65536}", null));
        inherited = (await config.ResolveAsync(pool.Id, null)).Single(m => m.Key == "native");
        Assert.Equal(new ModelExecutionLimits(3000, 65536), ModelExecutionLimits.FromJson(inherited.ParamsJson));
        foreach (var invalid in new[] { "{\"taskBudget\":-1}", "{\"taskBudget\":1.5}", "{\"taskBudget\":\"10\"}", "{\"contextWindow\":true}", "{\"contextWindow\":-1}", "{\"contextWindow\":2147483648}" })
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => config.UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Platform, "", new("invalid", null, true, invalid, null)))).Status);
        var user = await env.UserAsync("member");
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => config.UpsertAsync(user, ConfigKind.Model, ConfigScope.Platform, "", new("native", null, true, "{}", null)))).Status);
        Assert.Equal(2, await env.Db.ConfigItems.CountAsync());
    }

    [Fact]
    public async Task Selected_Model_Budget_Is_Reserved_And_Zero_Does_Not_Bypass_Daily_Quota()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("daily");
        pool.PolicyJson = JsonSerializer.Serialize(new PoolPolicy { MaxTokensPerDay = 10000 }, PolicyService.Json);
        await env.Db.SaveChangesAsync();
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "work", null);
        var config = env.Get<ConfigService>();
        await config.UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Platform, "", new("bounded", null, true, "{\"taskBudget\":7000,\"isDefault\":true}", null));
        await config.UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Platform, "", new("native", null, true, "{\"taskBudget\":0}", null));
        var sessions = env.Get<SessionService>();
        var first = await sessions.CreateAsync(env.Admin, ws.Id, null, null);
        var sent = await sessions.SendAsync(env.Admin, first.Id, "hello", null);
        Assert.Equal(7000, (await WaitAsync(env, sent.RunId)).ReservedTokens);
        var second = await sessions.CreateAsync(env.Admin, ws.Id, null, "native");
        sent = await sessions.SendAsync(env.Admin, second.Id, "hello", null);
        Assert.Equal(3000, (await WaitAsync(env, sent.RunId)).ReservedTokens);
        var third = await sessions.CreateAsync(env.Admin, ws.Id, null, "native");
        Assert.Equal(429, (await Assert.ThrowsAsync<ApiException>(() => sessions.SendAsync(env.Admin, third.Id, "hello", null))).Status);
    }

    [Fact]
    public async Task Explicit_Context_Is_Materialized_And_Zero_Task_Budget_Has_No_Extra_Cap()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("context");
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "work", null);
        await env.Get<ConfigService>().UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Platform, "", new("custom", null, true, "{\"provider\":\"custom\",\"baseUrl\":\"https://fixture.example/v1\",\"contextWindow\":65536,\"taskBudget\":0}", null));
        var (_, access) = await env.Engine.RequireWorkspaceAsync(env.Admin, ws.Id, WorkspaceRole.Member);
        var prep = await env.Get<RuntimeMaterializer>().PrepareAsync(env.Admin, ws, access, "limits-test", null, "hello", null, pool);
        Assert.Contains("\"contextWindow\": 65536", await File.ReadAllTextAsync(prep.Request.PatchFile!));
        var sessions = env.Get<SessionService>();
        var session = await sessions.CreateAsync(env.Admin, ws.Id, null, null);
        var sent = await sessions.SendAsync(env.Admin, session.Id, "hello", null);
        Assert.Equal(0, (await WaitAsync(env, sent.RunId)).ReservedTokens);
    }

    private static async Task<AgentRun> WaitAsync(TestEnv env, string id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var context = await env.Root.GetRequiredService<IDbContextFactory<DshDbContext>>().CreateDbContextAsync();
            var run = await context.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == id);
            if (run.Status != RunStatus.Running && await context.AuditLogs.CountAsync(a => a.Action == "session.run" && a.ResourceId == run.SessionId) > 0)
            {
                Assert.Equal(RunStatus.Completed, run.Status);
                return run;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Model limits test did not complete");
    }
}
