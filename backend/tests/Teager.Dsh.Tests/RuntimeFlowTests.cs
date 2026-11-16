using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Tests;

/// <summary>会话 / Agent 运行流程测试（使用 Mock Provider）：消息落库、事件写文件、用量、取消、并发保护、共享空间读写审计与 RO 违规检测。</summary>
public class RuntimeFlowTests
{
    /// <summary>等待某次运行结束（轮询数据库，避免测试依赖具体调度时机）。</summary>
    private static async Task<AgentRun> WaitRunAsync(TestEnv env, string runId, int seconds = 30)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            await using var ctx = await env.Root.GetRequiredService<IDbContextFactory<DshDbContext>>().CreateDbContextAsync();
            var r = await ctx.AgentRuns.AsNoTracking().FirstAsync(x => x.Id == runId);
            if (r.Status != RunStatus.Running && await ctx.AuditLogs.AnyAsync(a => a.Action == "session.run" && a.ResourceId != null)) return r;
            await Task.Delay(100);
        }
        throw new TimeoutException("运行未在预期时间内结束");
    }

    /// <summary>基础流程：创建会话 → 发送消息 → 收到回复 → 事件写入文件而非数据库 → 用量与 Runtime 状态更新。</summary>
    [Fact]
    public async Task Send_Message_Full_Flow()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("P"); var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "w", null);
        var sessions = env.Get<SessionService>();
        var s = await sessions.CreateAsync(env.Admin, ws.Id, null, null);
        var r = await sessions.SendAsync(env.Admin, s.Id, "你好，介绍一下自己", null);
        var run = await WaitRunAsync(env, r.RunId);
        Assert.Equal(RunStatus.Completed, run.Status);
        env.Db.ChangeTracker.Clear();
        var msgs = await env.Db.SessionMessages.Where(m => m.SessionId == s.Id).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, msgs.Count); Assert.Equal("user", msgs[0].Role); Assert.Equal("assistant", msgs[1].Role); Assert.Contains("模拟回复", msgs[1].Content);
        var session = await env.Db.Sessions.FirstAsync(x => x.Id == s.Id);
        Assert.Equal("你好，介绍一下自己", session.Title); Assert.NotNull(session.RuntimeSessionId); Assert.True(session.LastEventSeq > 5);
        // 事件详情在文件日志里（含 run_start / text / run_end），数据库没有事件明细表
        var paths = env.Get<Teager.Dsh.Infrastructure.Storage.StoragePaths>();
        var lines = await File.ReadAllLinesAsync(paths.RunEventLog(s.Id, session.CreatedAt));
        Assert.Contains(lines, l => l.Contains("\"type\":\"run_start\"")); Assert.Contains(lines, l => l.Contains("\"type\":\"run_end\"")); Assert.Contains(lines, l => l.Contains("\"type\":\"text\""));
        Assert.True(await env.Db.UsageLogs.AnyAsync(u => u.SessionId == s.Id && u.Success));
        var rt = await env.Db.Runtimes.FirstAsync(x => x.PoolId == pool.Id);
        Assert.Contains(rt.Status, new[] { RuntimeStatus.IDLE, RuntimeStatus.RUNNING });
        // 第二条消息复用同一个 Runtime 会话
        var r2 = await sessions.SendAsync(env.Admin, s.Id, "继续", null); await WaitRunAsync(env, r2.RunId);
        env.Db.ChangeTracker.Clear();
        Assert.Equal(session.RuntimeSessionId, (await env.Db.Sessions.FirstAsync(x => x.Id == s.Id)).RuntimeSessionId);
        // 事件历史可按 seq 续读
        var (_, evts) = await sessions.EventsAsync(env.Admin, s.Id, session.LastEventSeq, null);
        Assert.NotEmpty(evts);
    }

    /// <summary>同一会话同时只允许一个运行；取消后状态为 Cancelled。</summary>
    [Fact]
    public async Task Busy_Session_Rejects_And_Cancel_Works()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("P"); var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "w", null);
        var sessions = env.Get<SessionService>();
        var s = await sessions.CreateAsync(env.Admin, ws.Id, null, null);
        var r = await sessions.SendAsync(env.Admin, s.Id, "/sleep 30", null);
        var ex = await Assert.ThrowsAsync<ApiException>(() => sessions.SendAsync(env.Admin, s.Id, "第二条", null));
        Assert.Equal(409, ex.Status);
        await Task.Delay(500);
        Assert.True(await sessions.CancelAsync(env.Admin, s.Id));
        var run = await WaitRunAsync(env, r.RunId);
        Assert.Equal(RunStatus.Cancelled, run.Status);
    }

    /// <summary>Viewer 不能发送消息；无权用户看不到会话。</summary>
    [Fact]
    public async Task Viewer_Cannot_Send_And_Outsider_Cannot_See()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("P"); var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "w", null);
        var sessions = env.Get<SessionService>();
        var s = await sessions.CreateAsync(env.Admin, ws.Id, null, null);
        var viewer = await env.UserAsync("v"); var stranger = await env.UserAsync("x");
        await env.Get<WorkspaceService>().UpsertMemberAsync(env.Admin, ws.Id, viewer.UserId, WorkspaceRole.Viewer, GrantEffect.Allow);
        await Assert.ThrowsAsync<ApiException>(() => sessions.SendAsync(viewer, s.Id, "hi", null));
        var legacy = await Assert.ThrowsAsync<ApiException>(() => sessions.MessagesAsync(viewer, s.Id, 0, 10));
        Assert.Equal(404, legacy.Status);
        var ex = await Assert.ThrowsAsync<ApiException>(() => sessions.MessagesAsync(stranger, s.Id, 0, 10));
        Assert.Equal(404, ex.Status);
    }

    /// <summary>共享空间：RW 变更记录观察审计；RO 写入在发生前被阻止。</summary>
    [Fact]
    public async Task SharedSpace_RW_Changes_Observed_And_RO_Write_Prevented()
    {
        await using var env = await TestEnv.CreateAsync();
        var c = await env.PoolAsync("集团"); var a = await env.PoolAsync("编辑部", c);
        var user = await env.UserAsync("a"); await env.JoinAsync(a, user);
        var c1 = await env.SpaceAsync("集团公共", c, c, SharePermission.RO, true);
        var a1 = await env.SpaceAsync("编辑内部", a, a, SharePermission.RW, false);
        var wsSvc = env.Get<WorkspaceService>();
        var ws = await wsSvc.CreateAsync(env.Admin, a.Id, "w", null);
        await env.Get<PersonalWorkspaceService>().AssignAsync(env.Admin, user.UserId, ws.Id);
        await env.Get<SharedSpaceService>().UpsertGrantAsync(env.Admin, a1.Id, GrantSubjectType.USER, user.UserId, SharePermission.RW, GrantEffect.Allow, false);
        await wsSvc.AddMountAsync(env.Admin, ws.Id, c1.Id, "group", null);
        await wsSvc.AddMountAsync(env.Admin, ws.Id, a1.Id, "editorial", null);
        var sessions = env.Get<SessionService>();
        var s = await sessions.CreateAsync(user, ws.Id, null, null);

        var r1 = await sessions.SendAsync(user, s.Id, "/write shared/editorial/note.txt 编辑部笔记", null);
        Assert.Equal(RunStatus.Completed, (await WaitRunAsync(env, r1.RunId)).Status);
        var paths = env.Get<Teager.Dsh.Infrastructure.Storage.StoragePaths>();
        Assert.True(File.Exists(Path.Combine(paths.SharedSpace(a1.Id), "note.txt")));   // 真实写进了共享空间
        var r2 = await sessions.SendAsync(user, s.Id, "/write shared/group/hack.txt 越权写入", null);
        await WaitRunAsync(env, r2.RunId);
        await using var ctx = await env.Root.GetRequiredService<IDbContextFactory<DshDbContext>>().CreateDbContextAsync();
        Assert.True(await ctx.AuditLogs.AnyAsync(x => x.Action == "sharedspace.observed-change" && x.ResourceId == a1.Id && x.Result == "observed"));
        Assert.False(File.Exists(Path.Combine(paths.SharedSpace(c1.Id), "hack.txt")));
        var events = env.Get<SessionEventStore>().Read(s.Id, s.CreatedAt, 0, r2.RunId);
        Assert.Contains(events, e => e["type"]?.GetValue<string>() == "tool_result" && e["status"]?.GetValue<string>() == "error");

    }

    /// <summary>Runtime 生命周期：启动 / 停止 / 重启 / 维护模式拒绝新任务。</summary>
    [Fact]
    public async Task Runtime_Lifecycle()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("P"); var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "w", null);
        var rm = env.Get<RuntimeManager>();
        Assert.Equal(RuntimeStatus.STOPPED, (await rm.GetOrCreateAsync(pool)).Status);
        Assert.Equal(RuntimeStatus.IDLE, (await rm.StartAsync(pool)).Status);
        Assert.Equal(RuntimeStatus.STOPPED, (await rm.StopAsync(pool)).Status);
        Assert.Equal(RuntimeStatus.IDLE, (await rm.RestartAsync(pool)).Status);
        await rm.SetMaintenanceAsync(pool, true);
        var sessions = env.Get<SessionService>();
        var s = await sessions.CreateAsync(env.Admin, ws.Id, null, null);
        await Assert.ThrowsAsync<ApiException>(() => sessions.SendAsync(env.Admin, s.Id, "hi", null));
        await rm.SetMaintenanceAsync(pool, false);
        var r = await sessions.SendAsync(env.Admin, s.Id, "hi", null);
        Assert.Equal(RunStatus.Completed, (await WaitRunAsync(env, r.RunId)).Status);   // 停止状态下访问会自动启动
    }

    /// <summary>删除 Workspace 三种模式：仅清理运行上下文 / 归档 / 永久删除（方案第 74 节）。</summary>
    [Fact]
    public async Task Workspace_Delete_Modes()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("P"); var wsSvc = env.Get<WorkspaceService>();
        var ws = await wsSvc.CreateAsync(env.Admin, pool.Id, "w", null);
        var paths = env.Get<Teager.Dsh.Infrastructure.Storage.StoragePaths>();
        await File.WriteAllTextAsync(Path.Combine(paths.WorkspaceFiles(ws.Id), "keep.txt"), "x");
        var s = await env.Get<SessionService>().CreateAsync(env.Admin, ws.Id, null, null);
        await wsSvc.DeleteAsync(env.Admin, ws.Id, "runtime_context");
        env.Db.ChangeTracker.Clear();
        Assert.False(await env.Db.Sessions.AnyAsync(x => x.Id == s.Id)); Assert.True(File.Exists(Path.Combine(paths.WorkspaceFiles(ws.Id), "keep.txt")));
        await wsSvc.DeleteAsync(env.Admin, ws.Id, "archive");
        env.Db.ChangeTracker.Clear();
        var archived = await env.Db.Workspaces.FirstAsync(w => w.Id == ws.Id);
        Assert.Equal(WorkspaceStatus.Archived, archived.Status); Assert.True(archived.PurgeAfter > DateTime.Now.AddDays(29));
        await wsSvc.RestoreAsync(env.Admin, ws.Id);
        await wsSvc.DeleteAsync(env.Admin, ws.Id, "permanent");
        env.Db.ChangeTracker.Clear();
        Assert.False(await env.Db.Workspaces.AnyAsync(w => w.Id == ws.Id)); Assert.False(Directory.Exists(paths.WorkspaceRoot(ws.Id)));
    }

    /// <summary>快照与克隆：快照可恢复；克隆复制文件与挂载。</summary>
    [Fact]
    public async Task Snapshot_And_Clone()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("P"); var wsSvc = env.Get<WorkspaceService>();
        var ws = await wsSvc.CreateAsync(env.Admin, pool.Id, "w", null);
        var paths = env.Get<Teager.Dsh.Infrastructure.Storage.StoragePaths>();
        var f = Path.Combine(paths.WorkspaceFiles(ws.Id), "a.txt");
        await File.WriteAllTextAsync(f, "v1");
        var snap = await wsSvc.CreateSnapshotAsync(env.Admin, ws.Id, "s1", null);
        await File.WriteAllTextAsync(f, "v2");
        var clone = await wsSvc.CloneAsync(env.Admin, ws.Id, null);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(paths.WorkspaceFiles(clone.Id), "a.txt")));
        await wsSvc.RestoreSnapshotAsync(env.Admin, ws.Id, snap.Id);
        Assert.Equal("v1", await File.ReadAllTextAsync(f));
    }
}
