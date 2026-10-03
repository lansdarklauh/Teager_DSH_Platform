using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Tests;

/// <summary>Pool 树结构（闭包表、移动、删除）、策略收紧、配置继承的测试。</summary>
public class PoolAndConfigTests
{
    /// <summary>取某 Pool 的祖先（按闭包表）。</summary>
    private static async Task<List<string>> AncestorsOf(TestEnv env, Pool p) =>
        await env.Db.PoolClosures.AsNoTracking().Where(c => c.DescendantPoolId == p.Id && c.Depth > 0).OrderBy(c => c.Depth).Select(c => c.AncestorPoolId).ToListAsync();

    /// <summary>创建子 Pool 时闭包表正确，且层级超过上限被拒绝（方案第 9、12 节）。</summary>
    [Fact]
    public async Task Closure_And_MaxDepth()
    {
        await using var env = await TestEnv.CreateAsync();
        var p1 = await env.PoolAsync("L1"); var p2 = await env.PoolAsync("L2", p1); var p3 = await env.PoolAsync("L3", p2); var p4 = await env.PoolAsync("L4", p3); var p5 = await env.PoolAsync("L5", p4);
        Assert.Equal(new[] { p4.Id, p3.Id, p2.Id, p1.Id }, await AncestorsOf(env, p5));
        var ex = await Assert.ThrowsAsync<ApiException>(() => env.PoolAsync("L6", p5));
        Assert.Contains("层级", ex.Message);
    }

    /// <summary>移动 Pool：闭包表重建、无环、深度更新、影响预览（方案第 72 节）。</summary>
    [Fact]
    public async Task Move_Rebuilds_Closure_And_Detects_Cycle()
    {
        await using var env = await TestEnv.CreateAsync();
        var c = await env.PoolAsync("C"); var d = await env.PoolAsync("D");
        var a = await env.PoolAsync("A", c); var a1 = await env.PoolAsync("A1", a);
        var svc = env.Get<PoolService>();
        // 环：把 C 移到自己的后代 A1 下
        await Assert.ThrowsAsync<ApiException>(() => svc.MoveAsync(env.Admin, c.Id, a1.Id));
        // 预览：A 从 C 移到 D，失去 C，获得 D，影响 A、A1 两个 Pool
        var user = await env.UserAsync("u"); await env.JoinAsync(a1, user);
        var space = await env.SpaceAsync("集团资料", c, c, SharePermission.RO, true);
        var preview = await svc.PreviewMoveAsync(env.Admin, a.Id, d.Id);
        Assert.Equal(new[] { "C" }, preview.LostAncestors); Assert.Equal(new[] { "D" }, preview.GainedAncestors);
        Assert.Contains("集团资料", preview.LostSharedSpaces); Assert.Equal(2, preview.AffectedPools); Assert.True(preview.AffectedUsers >= 1);
        // 移动前 u 可访问集团资料；移动后失去
        Assert.Equal(SharePermission.RO, (await env.Engine.GetUserSpaceAccessAsync(user, space)).Permission);
        await svc.MoveAsync(env.Admin, a.Id, d.Id);
        Assert.Equal(new[] { d.Id }, await AncestorsOf(env, a));
        Assert.Equal(new[] { a.Id, d.Id }, await AncestorsOf(env, a1));
        env.Db.ChangeTracker.Clear();
        Assert.Equal(3, (await env.Db.Pools.FirstAsync(p => p.Id == a1.Id)).Depth);
        Assert.Null((await env.Engine.GetUserSpaceAccessAsync(user, space)).Permission);
    }

    /// <summary>删除 Pool：不级联删除；子 Pool 可上移；Workspace 归档；共享空间保留（方案第 73 节）。</summary>
    [Fact]
    public async Task Delete_Pool_Does_Not_Cascade()
    {
        await using var env = await TestEnv.CreateAsync();
        var c = await env.PoolAsync("C"); var a = await env.PoolAsync("A", c); var a1 = await env.PoolAsync("A1", a);
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, a.Id, "ws", null);
        var space = await env.SpaceAsync("s", a, a, SharePermission.RW, false);
        await Assert.ThrowsAsync<ApiException>(() => env.Get<PoolService>().DeleteAsync(env.Admin, a.Id, "", "Archive", null)); // 必须选择子 Pool 处理方式
        await env.Get<PoolService>().DeleteAsync(env.Admin, a.Id, "ToParent", "Archive", null);
        env.Db.ChangeTracker.Clear();
        Assert.Equal(PoolStatus.Archived, (await env.Db.Pools.FirstAsync(p => p.Id == a.Id)).Status);
        Assert.Equal(c.Id, (await env.Db.Pools.FirstAsync(p => p.Id == a1.Id)).ParentPoolId);
        Assert.Equal(new[] { c.Id }, await AncestorsOf(env, a1));
        Assert.Equal(WorkspaceStatus.Archived, (await env.Db.Workspaces.FirstAsync(w => w.Id == ws.Id)).Status);
        Assert.NotNull(await env.Db.SharedSpaces.FirstOrDefaultAsync(s => s.Id == space.Id && s.Status == SharedSpaceStatus.Active)); // 共享空间保留
    }

    /// <summary>策略只能收紧：子级不能放宽父级限制；授权类字段仅平台管理员可设置（方案第 31 节）。</summary>
    [Fact]
    public async Task Policy_Can_Only_Tighten()
    {
        await using var env = await TestEnv.CreateAsync();
        var c = await env.PoolAsync("C"); var a = await env.PoolAsync("A", c);
        var pol = env.Get<PolicyService>();
        await pol.ValidateAsync(env.Admin, null, c.Id, new PoolPolicy { MaxWorkspaces = 5, AllowNetwork = false, SandboxMode = "read-only" });
        c.PolicyJson = System.Text.Json.JsonSerializer.Serialize(new PoolPolicy { MaxWorkspaces = 5, AllowNetwork = false, SandboxMode = "workspace-write" }, PolicyService.Json);
        await env.Db.SaveChangesAsync(); env.Cache.Invalidate();
        await Assert.ThrowsAsync<ApiException>(() => pol.ValidateAsync(env.Admin, c.Id, a.Id, new PoolPolicy { MaxWorkspaces = 10 }));       // 放宽数量
        await Assert.ThrowsAsync<ApiException>(() => pol.ValidateAsync(env.Admin, c.Id, a.Id, new PoolPolicy { AllowNetwork = true }));       // 放开网络
        await Assert.ThrowsAsync<ApiException>(() => pol.ValidateAsync(env.Admin, c.Id, a.Id, new PoolPolicy { SandboxMode = "danger-full-access" }));
        await pol.ValidateAsync(env.Admin, c.Id, a.Id, new PoolPolicy { MaxWorkspaces = 3, SandboxMode = "read-only" });                      // 收紧 OK
        var poolAdmin = await env.UserAsync("pa"); await env.JoinAsync(a, poolAdmin, PoolRole.Admin);
        await Assert.ThrowsAsync<ApiException>(() => pol.ValidateAsync(poolAdmin, c.Id, a.Id, new PoolPolicy { AllowOwnMcp = true }));        // 非管理员不能授权扩大 MCP
        var eff = await pol.GetEffectiveAsync(a.Id);
        Assert.False(eff.AllowNetwork); Assert.Equal(5, eff.MaxWorkspaces); Assert.Equal("C", eff.Sources["allowNetwork"]);
    }

    /// <summary>Workspace 数量配额生效。</summary>
    [Fact]
    public async Task Workspace_Quota_Enforced()
    {
        await using var env = await TestEnv.CreateAsync();
        var p = await env.PoolAsync("P");
        p.PolicyJson = System.Text.Json.JsonSerializer.Serialize(new PoolPolicy { MaxWorkspaces = 1 }, PolicyService.Json);
        await env.Db.SaveChangesAsync();
        var wsSvc = env.Get<WorkspaceService>();
        await wsSvc.CreateAsync(env.Admin, p.Id, "w1", null);
        var ex = await Assert.ThrowsAsync<ApiException>(() => wsSvc.CreateAsync(env.Admin, p.Id, "w2", null));
        Assert.Equal(429, ex.Status);
    }

    /// <summary>配置继承：父级默认值向下继承；子级可停用；子级不能重新启用被停用项；非管理员不能自行新增模型（方案第 30、31、62 节）。</summary>
    [Fact]
    public async Task Config_Inheritance_Rules()
    {
        await using var env = await TestEnv.CreateAsync();
        var c = await env.PoolAsync("C"); var a = await env.PoolAsync("A", c);
        var cfg = env.Get<ConfigService>();
        // 集团层定义两个模型
        await cfg.UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Pool, c.Id, new ConfigUpsert("deepseek-flash", "DeepSeek Flash", true, "{\"provider\":\"deepseek-official\",\"isDefault\":true}", null));
        await cfg.UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Pool, c.Id, new ConfigUpsert("gpt-x", "GPT", true, "{\"provider\":\"openai\",\"baseUrl\":\"http://x/v1\"}", null));
        var pa = await env.UserAsync("pa"); await env.JoinAsync(a, pa, PoolRole.Admin);

        var eff = await cfg.ResolveAsync(a.Id, null);
        Assert.Equal(2, eff.Count(e => e.Kind == ConfigKind.Model && e.Enabled));
        // Pool A 管理员停用 gpt-x（收紧）
        await cfg.UpsertAsync(pa, ConfigKind.Model, ConfigScope.Pool, a.Id, new ConfigUpsert("gpt-x", null, false, null, null));
        eff = await cfg.ResolveAsync(a.Id, null);
        Assert.False(eff.First(e => e.Key == "gpt-x").Enabled);
        Assert.Equal("A", eff.First(e => e.Key == "gpt-x").DisabledAt);
        // 父级 C 的有效配置不受影响
        Assert.True((await cfg.ResolveAsync(c.Id, null)).First(e => e.Key == "gpt-x").Enabled);
        // Workspace 层不能重新启用被 Pool A 停用的项
        var ws = await env.Get<WorkspaceService>().CreateAsync(pa, a.Id, "w", null);
        await Assert.ThrowsAsync<ApiException>(() => cfg.UpsertAsync(pa, ConfigKind.Model, ConfigScope.Workspace, ws.Id, new ConfigUpsert("gpt-x", null, true, null, null)));
        // 非管理员不能新增全新模型 / MCP（扩大能力面），Skill 默认允许
        await Assert.ThrowsAsync<ApiException>(() => cfg.UpsertAsync(pa, ConfigKind.Model, ConfigScope.Pool, a.Id, new ConfigUpsert("my-model", null, true, "{\"provider\":\"x\"}", null)));
        await Assert.ThrowsAsync<ApiException>(() => cfg.UpsertAsync(pa, ConfigKind.Mcp, ConfigScope.Pool, a.Id, new ConfigUpsert("evil", null, true, "{\"transport\":\"stdio\",\"command\":\"rm\"}", null)));
        await cfg.UpsertAsync(pa, ConfigKind.Skill, ConfigScope.Pool, a.Id, new ConfigUpsert("weekly-report", "周报", true, null, "# 周报写作规范"));
        Assert.Contains((await cfg.ResolveAsync(a.Id, ws.Id)), e => e.Kind == ConfigKind.Skill && e.Key == "weekly-report" && e.Enabled);
        // 平台管理员显式授权后，Pool 管理员可新增 MCP
        env.Db.ChangeTracker.Clear();
        var pool = await env.Db.Pools.FirstAsync(p => p.Id == a.Id);
        pool.PolicyJson = System.Text.Json.JsonSerializer.Serialize(new PoolPolicy { AllowOwnMcp = true }, PolicyService.Json);
        await env.Db.SaveChangesAsync();
        await cfg.UpsertAsync(pa, ConfigKind.Mcp, ConfigScope.Pool, a.Id, new ConfigUpsert("docs", null, true, "{\"transport\":\"streamable-http\",\"url\":\"http://docs/mcp\"}", null));
        // 模型默认选择：C 声明 deepseek-flash 为默认
        Assert.True((await cfg.ResolveAsync(a.Id, null)).First(e => e.Key == "deepseek-flash").IsDefault);
    }

    /// <summary>密钥：加密存储、接口不返回明文、按 Workspace &gt; Pool &gt; 组织优先级注入（方案第 61 节）。</summary>
    [Fact]
    public async Task Secrets_Encrypted_And_Prioritized()
    {
        await using var env = await TestEnv.CreateAsync();
        var p = await env.PoolAsync("P"); var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, p.Id, "w", null);
        var sec = env.Get<SecretService>();
        await sec.UpsertAsync(env.Admin, ConfigScope.Platform, "", "API_KEY", "org-value", null);
        await sec.UpsertAsync(env.Admin, ConfigScope.Pool, p.Id, "API_KEY", "pool-value", null);
        await sec.UpsertAsync(env.Admin, ConfigScope.Pool, p.Id, "ONLY_POOL", "x", null);
        Assert.Equal("pool-value", (await sec.ResolveEnvAsync(p.Id, ws.Id))["API_KEY"]);
        await sec.UpsertAsync(env.Admin, ConfigScope.Workspace, ws.Id, "API_KEY", "ws-value", null);
        var env2 = await sec.ResolveEnvAsync(p.Id, ws.Id);
        Assert.Equal("ws-value", env2["API_KEY"]); Assert.Equal("x", env2["ONLY_POOL"]);
        var raw = await env.Db.Secrets.AsNoTracking().Where(s => s.Name == "API_KEY").Select(s => s.ValueEncrypted).ToListAsync();
        Assert.DoesNotContain(raw, r => r.Contains("value")); // 库里是密文
        Assert.DoesNotContain("ws-value", System.Text.Json.JsonSerializer.Serialize(await sec.ListAsync(env.Admin, ConfigScope.Workspace, ws.Id)));
        // 普通成员不能管理密钥
        var u = await env.UserAsync("m"); await env.JoinAsync(p, u);
        await Assert.ThrowsAsync<ApiException>(() => sec.UpsertAsync(u, ConfigScope.Pool, p.Id, "K", "v", null));
    }

    /// <summary>文件存储：路径穿越、符号链接逃逸、zip-slip 均被拒绝。</summary>
    [Fact]
    public void FileStore_Blocks_Traversal_And_Symlinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh_fs_" + Guid.NewGuid().ToString("N")[..8]);
        var outside = Path.Combine(Path.GetTempPath(), "dsh_fs_out_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            Assert.Throws<ApiException>(() => FileStore.Resolve(root, "../secret.txt"));
            Assert.Throws<ApiException>(() => FileStore.Resolve(root, "a/../../x"));
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
                Assert.Throws<ApiException>(() => FileStore.Resolve(root, "link/secret.txt"));
                Assert.DoesNotContain(FileStore.List(root, ""), e => e.Name == "link"); // 链接不可见
            }
            catch (IOException) { /* 无权限创建符号链接的环境跳过该断言 */ }
            catch (UnauthorizedAccessException) { }
            FileStore.Mkdir(root, "docs"); FileStore.WriteAsync(root, "docs/a.txt", new MemoryStream("hi"u8.ToArray()), 1024).GetAwaiter().GetResult();
            Assert.Equal("hi", FileStore.ReadTextAsync(root, "docs/a.txt").GetAwaiter().GetResult());
            Assert.Throws<ApiException>(() => FileStore.WriteAsync(root, "big.bin", new MemoryStream(new byte[2048]), 1024).GetAwaiter().GetResult()); // 超限
            Assert.False(File.Exists(Path.Combine(root, "big.bin")));
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    /// <summary>YAML 输出：MCP 的 ${SECRET} 变为 !!js 环境变量引用，不落密钥值。</summary>
    [Fact]
    public void Yaml_Emits_Js_Env_Reference()
    {
        var rows = new List<object> { new Dictionary<string, object?> { ["insert"] = new List<object> { new Dictionary<string, object?> { ["id"] = "mcp-x", ["config"] = new Dictionary<string, object?> { ["env"] = new Dictionary<string, object?> { ["TOKEN"] = Yaml.EnvRef("${GITHUB_TOKEN}"), ["PLAIN"] = Yaml.EnvRef("abc") }, ["args"] = new List<object?> { "-y", "srv" } } } } } };
        var text = Yaml.Emit(rows);
        Assert.Contains("\"TOKEN\": !!js \"process.env.GITHUB_TOKEN\"", text);
        Assert.Contains("\"PLAIN\": \"abc\"", text);
        Assert.Contains("- \"-y\"", text);
    }
}
