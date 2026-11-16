using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;
using Xunit;

namespace Teager.Dsh.Tests;

public class ProjectReferenceTests
{
    private static async Task<WorkspaceProject> Project(TestEnv env, string workspaceId, string name)
    {
        var project = new WorkspaceProject { Id = IdGen.New("prj"), WorkspaceId = workspaceId, Name = name, Description = "中文项目说明" };
        project.Directory = "projects/" + project.Id;
        FileStore.Mkdir(env.Get<StoragePaths>().WorkspaceFiles(workspaceId), project.Directory);
        env.Db.Add(project); await env.Db.SaveChangesAsync();
        return project;
    }

    [Fact]
    public async Task Referenced_Project_Reaches_Agent_And_Survives_History_Duplication_And_Export()
    {
        await using var env = await TestEnv.CreateAsync();
        var user = await env.UserAsync("alice");
        var workspace = (await env.Get<PersonalWorkspaceService>().EnsureAsync(user.UserId))!;
        var project = await Project(env, workspace.Id, "新闻项目");
        var service = env.Get<SessionService>();
        var session = await service.CreateAsync(user, workspace.Id, null, null);
        var sent = await service.SendAsync(user, session.Id, "分析这个项目", null, "project-request", [project.Id]);
        for (var attempt = 0; attempt < 150; attempt++)
        {
            await using var db = await env.Root.GetRequiredService<IDbContextFactory<DshDbContext>>().CreateDbContextAsync();
            if (await db.AgentRuns.AnyAsync(r => r.Id == sent.RunId && r.Status == RunStatus.Completed)
                && await db.SessionMessages.AnyAsync(m => m.RunId == sent.RunId && m.Role == "assistant")) break;
            await Task.Delay(100);
        }
        var messages = await service.MessagesAsync(user, session.Id, 0, 500);
        var original = Assert.Single(messages, m => m.Role == "user");
        Assert.Equal("分析这个项目", original.Content);
        Assert.Equal(project.Id, Assert.Single(ProjectReferences.Read(original.ProjectReferencesJson)).Id);
        Assert.Contains(project.Directory, Assert.Single(messages, m => m.Role == "assistant").Content);
        var events = (await service.EventsAsync(user, session.Id, 0, sent.RunId)).Events;
        Assert.Contains(project.Id, events.First(e => e["type"]!.GetValue<string>() == "run_start")["projects"]!.ToJsonString());
        var copy = await service.DuplicateAsync(user, session.Id);
        Assert.Equal(original.ProjectReferencesJson, (await service.MessagesAsync(user, copy.Id, 0, 500)).First().ProjectReferencesJson);
        Assert.Contains(project.Id, (await service.ExportAsync(user, session.Id, "json")).Text);
        Assert.Contains(project.Directory, (await service.ExportAsync(user, session.Id, "md")).Text);
        project.Name = "重命名后"; await env.Db.SaveChangesAsync();
        Assert.Equal("新闻项目", Assert.Single(ProjectReferences.Read(original.ProjectReferencesJson)).Name);
        var retried = await service.SendAsync(user, session.Id, "分析这个项目", null, "project-request", [project.Id]);
        Assert.Equal(sent.RunId, retried.RunId);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SendAsync(user, session.Id, "分析这个项目", null, "project-request", []))).Status);
    }

    [Fact]
    public async Task Invalid_References_Cannot_Create_Messages_Or_Run_In_Other_Workspaces()
    {
        await using var env = await TestEnv.CreateAsync();
        var alice = await env.UserAsync("alice"); var bob = await env.UserAsync("bob");
        var personal = env.Get<PersonalWorkspaceService>();
        var own = (await personal.EnsureAsync(alice.UserId))!; var other = (await personal.EnsureAsync(bob.UserId))!;
        var project = await Project(env, own.Id, "own"); var foreign = await Project(env, other.Id, "private");
        var service = env.Get<SessionService>();
        var session = await service.CreateAsync(alice, own.Id, null, null);
        foreach (var ids in new[] { new[] { foreign.Id }, new[] { "prj_missing" } })
            Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.SendAsync(alice, session.Id, "分析", null, projectIds: ids))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SendAsync(alice, session.Id, "分析", null, projectIds: [project.Id, project.Id]))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SendAsync(alice, session.Id, "分析", null, projectIds: Enumerable.Range(0, 9).Select(n => "prj_" + n).ToArray()))).Status);
        // A legacy token that lacks file-read scope cannot resolve project metadata.
        var token = new ApiToken { Id = IdGen.New("tok"), OwnerUserId = alice.UserId, Name = "legacy", TokenHash = AuthService.Hash("legacy-project-test"), ScopesJson = "[\"session:execute\"]" };
        env.Db.ApiTokens.Add(token); await env.Db.SaveChangesAsync();
        var limited = new Actor { UserId = alice.UserId, Via = "token", TokenId = token.Id };
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.SendAsync(limited, session.Id, "分析", null, projectIds: [project.Id]))).Status);
        Directory.Delete(Path.Combine(env.Get<StoragePaths>().WorkspaceFiles(own.Id), project.Directory.Replace('/', Path.DirectorySeparatorChar)), true);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.SendAsync(alice, session.Id, "分析", null, projectIds: [project.Id]))).Status);
        Assert.Empty(await env.Db.AgentRuns.Where(r => r.SessionId == session.Id).ToListAsync());
        Assert.Empty(await env.Db.SessionMessages.Where(m => m.SessionId == session.Id).ToListAsync());
    }
}
