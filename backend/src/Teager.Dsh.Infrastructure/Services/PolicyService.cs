using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>Pool 自身声明的策略（字段为空表示不覆盖，沿用上级）。</summary>
public class PoolPolicy
{
    /// <summary>是否允许网络访问（只能收紧）。</summary>
    public bool? AllowNetwork { get; set; }
    /// <summary>是否允许 Workspace 终端（只能收紧）。</summary>
    public bool? AllowTerminal { get; set; }
    /// <summary>沙箱模式 read-only / workspace-write / danger-full-access（只能收紧）。</summary>
    public string? SandboxMode { get; set; }
    /// <summary>最多 Workspace 数（只能收紧）。</summary>
    public int? MaxWorkspaces { get; set; }
    /// <summary>单 Workspace 私有文件配额 MB（只能收紧）。</summary>
    public int? MaxStorageMbPerWorkspace { get; set; }
    /// <summary>并发运行数（只能收紧）。</summary>
    public int? MaxConcurrentRuns { get; set; }
    /// <summary>每日消息数（只能收紧）。</summary>
    public int? MaxMessagesPerDay { get; set; }
    /// <summary>每日 token 数（只能收紧）。</summary>
    public long? MaxTokensPerDay { get; set; }
    /// <summary>单次运行最长分钟数（只能收紧）。</summary>
    public int? MaxRunMinutes { get; set; }
    /// <summary>是否允许本 Pool / 其 Workspace 自行新增模型（需平台管理员授权，不继承）。</summary>
    public bool? AllowOwnModels { get; set; }
    /// <summary>是否允许自行新增 MCP（需平台管理员授权，不继承）。</summary>
    public bool? AllowOwnMcp { get; set; }
    /// <summary>是否允许自行新增 Skill（默认允许，不继承）。</summary>
    public bool? AllowOwnSkills { get; set; }
    /// <summary>默认共享空间：新建 Workspace 时自动挂载（沿祖先链累加）。</summary>
    public List<string>? DefaultSharedSpaceIds { get; set; }
}

/// <summary>逐级合并后的有效策略。</summary>
/// <param name="AllowNetwork">允许网络。</param>
/// <param name="AllowTerminal">允许终端。</param>
/// <param name="SandboxMode">沙箱模式。</param>
/// <param name="MaxWorkspaces">Workspace 数上限（0 不限）。</param>
/// <param name="MaxStorageMbPerWorkspace">单 Workspace 配额 MB（0 不限）。</param>
/// <param name="MaxConcurrentRuns">并发运行数（0 不限）。</param>
/// <param name="MaxMessagesPerDay">每日消息数（0 不限）。</param>
/// <param name="MaxTokensPerDay">每日 token（0 不限）。</param>
/// <param name="MaxRunMinutes">单次运行分钟数（0 不限）。</param>
/// <param name="AllowOwnModels">本 Pool 可否新增模型。</param>
/// <param name="AllowOwnMcp">本 Pool 可否新增 MCP。</param>
/// <param name="AllowOwnSkills">本 Pool 可否新增 Skill。</param>
/// <param name="DefaultSharedSpaceIds">默认共享空间。</param>
/// <param name="Sources">各字段的来源（Platform 或 Pool 名称），供“策略来源可解释”展示。</param>
public record EffectivePolicy(bool AllowNetwork, bool AllowTerminal, string SandboxMode, int MaxWorkspaces, int MaxStorageMbPerWorkspace,
    int MaxConcurrentRuns, int MaxMessagesPerDay, long MaxTokensPerDay, int MaxRunMinutes, bool AllowOwnModels, bool AllowOwnMcp, bool AllowOwnSkills,
    List<string> DefaultSharedSpaceIds, Dictionary<string, string> Sources);

/// <summary>策略服务：解析 Pool 策略链、计算有效策略、校验“只能收紧”、执行配额检查。</summary>
public class PolicyService(DshDbContext db, IOptions<DshOptions> opt, PermissionEngine engine)
{
    /// <summary>JSON 序列化选项（驼峰）。</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly string[] SandboxOrder = ["read-only", "workspace-write", "danger-full-access"];

    /// <summary>解析 Pool 自身策略 JSON。</summary>
    /// <param name="json">JSON 文本。</param>
    /// <returns>策略对象（空则全部为空）。</returns>
    public static PoolPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PoolPolicy();
        try { return JsonSerializer.Deserialize<PoolPolicy>(json, Json) ?? new PoolPolicy(); } catch { return new PoolPolicy(); }
    }

    /// <summary>计算 Pool 的有效策略（平台默认 → 祖先 → 本级，逐级收紧）。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <returns>有效策略。</returns>
    public async Task<EffectivePolicy> GetEffectiveAsync(string poolId)
    {
        var graph = await engine.GetGraphAsync();
        var chainIds = graph.AncestorsOf(poolId).Reverse().Append(poolId).ToList(); // 根 → 本级
        var pools = await db.Pools.AsNoTracking().Where(p => chainIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var d = opt.Value.Policy;

        bool net = d.AllowNetwork, term = d.AllowTerminal && opt.Value.Runtime.TerminalEnabled;
        var sandbox = d.SandboxMode; int maxWs = d.MaxWorkspaces, maxStore = d.MaxStorageMbPerWorkspace, maxConc = d.MaxConcurrentRuns, maxMsg = d.MaxMessagesPerDay, maxRun = d.MaxRunMinutes;
        long maxTok = d.MaxTokensPerDay;
        var src = new Dictionary<string, string>
        {
            ["allowNetwork"] = "平台默认", ["allowTerminal"] = opt.Value.Runtime.TerminalEnabled ? "平台默认" : "平台已关闭终端", ["sandboxMode"] = "平台默认",
            ["maxWorkspaces"] = "平台默认", ["maxStorageMbPerWorkspace"] = "平台默认", ["maxConcurrentRuns"] = "平台默认",
            ["maxMessagesPerDay"] = "平台默认", ["maxTokensPerDay"] = "平台默认", ["maxRunMinutes"] = "平台默认"
        };
        var defaults = new List<string>();
        PoolPolicy own = new();

        foreach (var id in chainIds)
        {
            if (!pools.TryGetValue(id, out var pool)) continue;
            var p = Parse(pool.PolicyJson);
            if (id == poolId) own = p;
            var name = pool.Name;
            if (p.AllowNetwork == false && net) { net = false; src["allowNetwork"] = name; }
            if (p.AllowTerminal == false && term) { term = false; src["allowTerminal"] = name; }
            if (p.SandboxMode != null && Array.IndexOf(SandboxOrder, p.SandboxMode) is var pi and >= 0 && pi < Array.IndexOf(SandboxOrder, sandbox)) { sandbox = p.SandboxMode; src["sandboxMode"] = name; }
            Tighten(ref maxWs, p.MaxWorkspaces, src, "maxWorkspaces", name);
            Tighten(ref maxStore, p.MaxStorageMbPerWorkspace, src, "maxStorageMbPerWorkspace", name);
            Tighten(ref maxConc, p.MaxConcurrentRuns, src, "maxConcurrentRuns", name);
            Tighten(ref maxMsg, p.MaxMessagesPerDay, src, "maxMessagesPerDay", name);
            Tighten(ref maxRun, p.MaxRunMinutes, src, "maxRunMinutes", name);
            if (p.MaxTokensPerDay is > 0 && (maxTok == 0 || p.MaxTokensPerDay < maxTok)) { maxTok = p.MaxTokensPerDay.Value; src["maxTokensPerDay"] = name; }
            if (p.DefaultSharedSpaceIds != null) defaults.AddRange(p.DefaultSharedSpaceIds.Where(x => !defaults.Contains(x)));
        }
        // “自行新增”类权限不继承，只看本级；默认：模型 / MCP 关闭（扩大安全面需管理员显式授权），Skill 开启
        return new EffectivePolicy(net, term, sandbox, maxWs, maxStore, maxConc, maxMsg, maxTok, maxRun,
            own.AllowOwnModels ?? false, own.AllowOwnMcp ?? false, own.AllowOwnSkills ?? true, defaults, src);
    }

    /// <summary>数值型策略收紧：仅当新值为正且小于当前值（或当前不限）时生效。</summary>
    /// <param name="cur">当前值（0 表示不限）。</param>
    /// <param name="v">本级声明值。</param>
    /// <param name="src">来源字典。</param>
    /// <param name="key">字段名。</param>
    /// <param name="name">来源 Pool 名称。</param>
    private static void Tighten(ref int cur, int? v, Dictionary<string, string> src, string key, string name)
    {
        if (v is > 0 && (cur == 0 || v < cur)) { cur = v.Value; src[key] = name; }
    }

    /// <summary>校验待保存的 Pool 策略没有放宽上级策略；“自行新增”类授权只有平台管理员可设置。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="parentPoolId">父 Pool（无则为平台默认）。</param>
    /// <param name="selfPoolId">当前 Pool。</param>
    /// <param name="p">待保存策略。</param>
    public async Task ValidateAsync(Actor actor, string? parentPoolId, string selfPoolId, PoolPolicy p)
    {
        var parent = parentPoolId != null ? await GetEffectiveAsync(parentPoolId) : await PlatformEffectiveAsync();
        if (p.AllowNetwork == true && !parent.AllowNetwork) throw ApiException.BadRequest("上级已禁止网络访问，子级不能放宽");
        if (p.AllowTerminal == true && !parent.AllowTerminal) throw ApiException.BadRequest("上级已禁止终端，子级不能放宽");
        if (p.SandboxMode != null)
        {
            var i = Array.IndexOf(SandboxOrder, p.SandboxMode);
            if (i < 0) throw ApiException.BadRequest("沙箱模式取值：read-only / workspace-write / danger-full-access");
            if (i > Array.IndexOf(SandboxOrder, parent.SandboxMode)) throw ApiException.BadRequest("沙箱模式不能比上级更宽松");
        }
        void Check(string label, long? v, long parentV)
        {
            if (v is < 0) throw ApiException.BadRequest($"{label} 不能为负数");
            if (v is > 0 && parentV > 0 && v > parentV) throw ApiException.BadRequest($"{label} 不能超过上级限制（{parentV}）");
            if (v == 0 && parentV > 0) throw ApiException.BadRequest($"{label} 上级有限制（{parentV}），子级不能设为不限");
        }
        Check("Workspace 数上限", p.MaxWorkspaces, parent.MaxWorkspaces);
        Check("单 Workspace 存储配额", p.MaxStorageMbPerWorkspace, parent.MaxStorageMbPerWorkspace);
        Check("并发运行数", p.MaxConcurrentRuns, parent.MaxConcurrentRuns);
        Check("每日消息数", p.MaxMessagesPerDay, parent.MaxMessagesPerDay);
        Check("每日 token 数", p.MaxTokensPerDay, parent.MaxTokensPerDay);
        Check("单次运行分钟数", p.MaxRunMinutes, parent.MaxRunMinutes);
        if (!actor.IsPlatformAdmin)
        {
            var cur = Parse((await db.Pools.AsNoTracking().FirstOrDefaultAsync(x => x.Id == selfPoolId))?.PolicyJson);
            if (p.AllowOwnModels != cur.AllowOwnModels || p.AllowOwnMcp != cur.AllowOwnMcp || p.AllowOwnSkills != cur.AllowOwnSkills)
                throw ApiException.Forbidden("扩大模型 / MCP / Skill 的自定义权限需要平台管理员显式授权");
        }
    }

    /// <summary>平台默认策略（无 Pool 上下文，用于根 Pool 校验）。</summary>
    private Task<EffectivePolicy> PlatformEffectiveAsync()
    {
        var d = opt.Value.Policy;
        var src = new Dictionary<string, string>();
        return Task.FromResult(new EffectivePolicy(d.AllowNetwork, d.AllowTerminal && opt.Value.Runtime.TerminalEnabled, d.SandboxMode, d.MaxWorkspaces, d.MaxStorageMbPerWorkspace,
            d.MaxConcurrentRuns, d.MaxMessagesPerDay, d.MaxTokensPerDay, d.MaxRunMinutes, false, false, true, [], src));
    }

    /// <summary>创建 Workspace 前检查数量配额。</summary>
    /// <param name="poolId">Pool ID。</param>
    public async Task EnsureCanCreateWorkspaceAsync(string poolId)
    {
        var eff = await GetEffectiveAsync(poolId);
        if (eff.MaxWorkspaces <= 0) return;
        var count = await db.Workspaces.CountAsync(w => w.PoolId == poolId && w.Status == WorkspaceStatus.Active);
        if (count >= eff.MaxWorkspaces) throw ApiException.Quota($"该 Pool 的 Workspace 数量已达上限（{eff.MaxWorkspaces}）");
    }

    /// <summary>发送消息前检查每日消息数 / token 配额。</summary>
    /// <param name="poolId">Pool ID。</param>
    public async Task EnsureRunQuotaAsync(string poolId)
    {
        var eff = await GetEffectiveAsync(poolId);
        var today = DateTime.Today;
        if (eff.MaxMessagesPerDay > 0)
        {
            var n = await db.AgentRuns.CountAsync(r => r.PoolId == poolId && r.StartedAt >= today);
            if (n >= eff.MaxMessagesPerDay) throw ApiException.Quota($"该 Pool 今日消息数已达上限（{eff.MaxMessagesPerDay}）");
        }
        if (eff.MaxTokensPerDay > 0)
        {
            var t = await db.UsageLogs.Where(u => u.PoolId == poolId && u.Time >= today).SumAsync(u => (long?)(u.InputTokens + u.OutputTokens)) ?? 0;
            if (t >= eff.MaxTokensPerDay) throw ApiException.Quota($"该 Pool 今日 token 用量已达上限（{eff.MaxTokensPerDay}）");
        }
    }
}
