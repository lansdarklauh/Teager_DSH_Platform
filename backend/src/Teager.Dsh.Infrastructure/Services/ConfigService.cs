using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>配置 / 密钥层级的访问校验：平台层仅管理员；Pool 层需 Pool 管理权；Workspace 层需 Workspace 管理权。</summary>
public class ConfigAccess(PermissionEngine engine)
{
    /// <summary>要求对某层级有管理权。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">层级对象 ID。</param>
    public async Task RequireManageAsync(Actor actor, ConfigScope scope, string scopeId)
    {
        actor.RequirePlatformAdmin();
        switch (scope)
        {
            case ConfigScope.Platform: if (!actor.IsPlatformAdmin) throw ApiException.Forbidden("平台 / 组织级配置仅平台管理员可管理"); break;
            case ConfigScope.Pool: await engine.RequirePoolManageAsync(actor, scopeId); break;
            case ConfigScope.Workspace: await engine.RequireWorkspaceAsync(actor, scopeId, null, manage: true); break;
        }
    }

    /// <summary>要求对某层级有查看权。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">层级对象 ID。</param>
    public async Task RequireViewAsync(Actor actor, ConfigScope scope, string scopeId)
    {
        if (scope == ConfigScope.Platform && actor.ResourceRestricted) throw ApiException.Forbidden("受限 Token 不可读取平台级配置");
        switch (scope)
        {
            case ConfigScope.Platform: break;
            case ConfigScope.Pool: await engine.RequirePoolViewAsync(actor, scopeId); break;
            case ConfigScope.Workspace: await engine.RequireWorkspaceAsync(actor, scopeId, null); break;
        }
    }
}

/// <summary>配置项写入请求。</summary>
/// <param name="Key">配置键。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="ParamsJson">参数 JSON。</param>
/// <param name="Content">正文（Skill）。</param>
public record ConfigUpsert(string Key, string? DisplayName, bool Enabled, string? ParamsJson, string? Content);

/// <summary>合并后的有效配置项。</summary>
/// <param name="Kind">种类。</param>
/// <param name="Key">键。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Enabled">最终是否启用。</param>
/// <param name="ParamsJson">最终参数。</param>
/// <param name="Content">最终正文。</param>
/// <param name="IsDefault">是否默认（模型）。</param>
/// <param name="DefinedAt">定义所在层级名称。</param>
/// <param name="DisabledAt">被谁停用（层级名称）。</param>
/// <param name="Chain">经过的层级。</param>
public record EffectiveItem(ConfigKind Kind, string Key, string? DisplayName, bool Enabled, string? ParamsJson, string? Content, bool IsDefault, string DefinedAt, string? DisabledAt, List<string> Chain);

/// <summary>
/// 配置服务：模型 / Skill / MCP 按 平台默认 → 祖先 Pool → 本 Pool → Workspace 逐级继承。
/// 规则：父级提供默认值；子级可以停用（收紧）；子级不能重新启用被上级停用的项；
/// 子级新增全新的项（扩大能力面）需要平台管理员或 Pool 策略显式授权（AllowOwn*）。
/// </summary>
public partial class ConfigService(DshDbContext db, PermissionEngine engine, ConfigAccess access, PolicyService policy)
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$")] private static partial Regex ModelKeyRegex();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")] private static partial Regex SkillKeyRegex();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")] private static partial Regex McpKeyRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")] private static partial Regex PluginKeyRegex();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")] private static partial Regex EnvironmentNameRegex();

    /// <summary>列出某层级自身定义的配置项。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="kind">种类（空为全部）。</param>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">层级对象 ID。</param>
    public async Task<List<ConfigItem>> ListAsync(Actor actor, ConfigKind? kind, ConfigScope scope, string scopeId)
    {
        await access.RequireViewAsync(actor, scope, scopeId);
        scopeId = scope == ConfigScope.Platform ? "" : scopeId;
        var q = db.ConfigItems.AsNoTracking().Where(c => c.Scope == scope && c.ScopeId == scopeId);
        if (kind != null) q = q.Where(c => c.Kind == kind);
        return await q.OrderBy(c => c.Kind).ThenBy(c => c.Key).ToListAsync();
    }

    /// <summary>新增 / 更新配置项，并执行“只能收紧、扩大需授权”的校验。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="kind">种类。</param>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">层级对象 ID。</param>
    /// <param name="req">写入内容。</param>
    /// <returns>保存后的配置项。</returns>
    public async Task<ConfigItem> UpsertAsync(Actor actor, ConfigKind kind, ConfigScope scope, string scopeId, ConfigUpsert req)
    {
        await access.RequireManageAsync(actor, scope, scopeId);
        scopeId = scope == ConfigScope.Platform ? "" : scopeId;
        var key = req.Key?.Trim() ?? "";
        ValidateKey(kind, key);
        var paramsJson = ValidateParams(kind, req.ParamsJson);

        if (scope != ConfigScope.Platform && !actor.IsPlatformAdmin)
        {
            var (poolId, wsId) = await ResolveTargetAsync(scope, scopeId);
            var upper = await ResolveInternalAsync(poolId, wsId, excludeScope: scope, excludeScopeId: scopeId);
            var inherited = upper.FirstOrDefault(i => i.Kind == kind && i.Key == key);
            if (inherited != null)
            {
                if (inherited.DisabledAt != null && req.Enabled) throw ApiException.Forbidden($"该项已被上级「{inherited.DisabledAt}」停用，子级不能重新启用");
                // 继承项只允许收紧（停用）；模型可以额外选择默认；Skill 在被授权时可覆盖正文
                var pol = await policy.GetEffectiveAsync(poolId);
                if (kind == ConfigKind.Model) paramsJson = MergeDefaultFlag(inherited.ParamsJson, paramsJson);
                else if (kind is ConfigKind.Mcp or ConfigKind.Plugin) paramsJson = inherited.ParamsJson;
                if (kind != ConfigKind.Skill || !pol.AllowOwnSkills) req = req with { Content = inherited.Content };
            }
            else
            {
                var pol = await policy.GetEffectiveAsync(poolId);
                var allowed = kind switch { ConfigKind.Model => pol.AllowOwnModels, ConfigKind.Mcp or ConfigKind.Plugin => pol.AllowOwnMcp, _ => pol.AllowOwnSkills }; // 插件与 MCP 同属“可执行第三方代码”，共用授权开关
                if (!allowed) throw ApiException.Forbidden($"当前 Pool 策略不允许自行新增{KindName(kind)}（扩大能力面需平台管理员显式授权，或先在上级 / 平台层定义后在此启用 / 停用）");
            }
        }

        var item = await db.ConfigItems.FirstOrDefaultAsync(c => c.Kind == kind && c.Scope == scope && c.ScopeId == scopeId && c.Key == key);
        if (item == null) { item = new ConfigItem { Id = IdGen.New("cfg"), Kind = kind, Scope = scope, ScopeId = scopeId, Key = key }; db.ConfigItems.Add(item); }
        item.DisplayName = req.DisplayName; item.Enabled = req.Enabled; item.ParamsJson = paramsJson; item.Content = req.Content;
        item.UpdatedBy = actor.Username; item.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>删除某层级的配置项（相当于取消该层的覆盖，回退到继承值）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">配置项 ID。</param>
    /// <returns>被删除的配置项。</returns>
    public async Task<ConfigItem> DeleteAsync(Actor actor, string id)
    {
        var item = await db.ConfigItems.FirstOrDefaultAsync(c => c.Id == id) ?? throw ApiException.NotFound("配置项不存在");
        await access.RequireManageAsync(actor, item.Scope, item.ScopeId);
        db.ConfigItems.Remove(item);
        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>计算 Pool（或 Workspace）的有效配置。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="workspaceId">Workspace ID（可空）。</param>
    /// <returns>有效配置项列表（含被停用的，供展示来源）。</returns>
    public Task<List<EffectiveItem>> ResolveAsync(string poolId, string? workspaceId) => ResolveInternalAsync(poolId, workspaceId, null, null);

    /// <summary>逐层合并配置。excludeScope 用于校验时排除“正在写入的这一层”，得到其上级的合并结果。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="excludeScope">排除的层级。</param>
    /// <param name="excludeScopeId">排除层级的对象 ID。</param>
    private async Task<List<EffectiveItem>> ResolveInternalAsync(string poolId, string? workspaceId, ConfigScope? excludeScope, string? excludeScopeId)
    {
        var graph = await engine.GetGraphAsync();
        var fullChain = graph.AncestorsOf(poolId).Reverse().Append(poolId).ToList(); // 根 → 本 Pool
        var pools = await db.Pools.AsNoTracking().Where(p => fullChain.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        // 层级链：平台 → 根 → … → 本 Pool → Workspace。某 Pool 的 InheritConfig=false 时，其上级（含平台默认）全部截断
        var startIdx = 0; var includePlatform = true;
        for (var i = 0; i < fullChain.Count; i++)
            if (pools.TryGetValue(fullChain[i], out var pp) && !pp.InheritConfig) { startIdx = i; includePlatform = false; }
        var levels = new List<(ConfigScope Scope, string Id, string Name)>();
        if (includePlatform) levels.Add((ConfigScope.Platform, "", "平台默认"));
        for (var i = startIdx; i < fullChain.Count; i++)
            if (pools.TryGetValue(fullChain[i], out var p)) levels.Add((ConfigScope.Pool, p.Id, p.Name));
        if (workspaceId != null)
        {
            var wsName = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.Name).FirstOrDefaultAsync() ?? workspaceId;
            levels.Add((ConfigScope.Workspace, workspaceId, "Workspace:" + wsName));
        }
        levels = levels.Where(l => !(excludeScope != null && l.Scope == excludeScope && l.Id == (excludeScopeId ?? ""))).ToList();

        var allItems = await db.ConfigItems.AsNoTracking().ToListAsync();
        var state = new Dictionary<(ConfigKind, string), EffectiveState>();
        foreach (var lv in levels)
        {
            foreach (var it in allItems.Where(i => i.Scope == lv.Scope && i.ScopeId == lv.Id).OrderBy(i => i.Key))
            {
                var k = (it.Kind, it.Key);
                if (!state.TryGetValue(k, out var st))
                {
                    state[k] = st = new EffectiveState { Kind = it.Kind, Key = it.Key, DisplayName = it.DisplayName, ParamsJson = it.ParamsJson, Content = it.Content, DefinedAt = lv.Name, Enabled = it.Enabled };
                    st.Chain.Add(lv.Name);
                    if (!it.Enabled) st.DisabledAt = lv.Name;
                    if (IsDefault(it)) st.DefaultAt = lv.Name;
                    continue;
                }
                st.Chain.Add(lv.Name);
                if (st.DisabledAt != null) continue; // 上级已停用，下级无法重新启用
                if (!it.Enabled) { st.Enabled = false; st.DisabledAt = lv.Name; continue; }
                // 下级覆盖：模型的默认标记、Skill 正文、显示名
                if (it.Kind == ConfigKind.Model) { if (IsDefault(it)) st.DefaultAt = lv.Name; st.ParamsJson = MergeDefaultFlag(st.ParamsJson, it.ParamsJson) ?? st.ParamsJson; }
                if (it.Kind == ConfigKind.Skill && !string.IsNullOrEmpty(it.Content)) st.Content = it.Content;
                if (!string.IsNullOrEmpty(it.DisplayName)) st.DisplayName = it.DisplayName;
            }
        }
        // 默认模型：以最后一个声明 isDefault 的层级为准；若没有声明，取第一个启用的模型
        var models = state.Values.Where(s => s.Kind == ConfigKind.Model && s.Enabled).ToList();
        string? defaultKey = models.Where(m => m.DefaultAt != null).OrderByDescending(m => levels.FindIndex(l => l.Name == m.DefaultAt)).FirstOrDefault()?.Key ?? models.FirstOrDefault()?.Key;
        return state.Values.OrderBy(s => s.Kind).ThenBy(s => s.Key)
            .Select(s => new EffectiveItem(s.Kind, s.Key, s.DisplayName, s.Enabled, s.ParamsJson, s.Content, s.Kind == ConfigKind.Model && s.Enabled && s.Key == defaultKey, s.DefinedAt, s.DisabledAt, s.Chain)).ToList();
    }

    /// <summary>配置项的可变状态（合并过程使用）。</summary>
    private sealed class EffectiveState
    {
        /// <summary>种类。</summary>
        public ConfigKind Kind { get; set; }
        /// <summary>键。</summary>
        public string Key { get; set; } = "";
        /// <summary>显示名。</summary>
        public string? DisplayName { get; set; }
        /// <summary>参数 JSON。</summary>
        public string? ParamsJson { get; set; }
        /// <summary>正文。</summary>
        public string? Content { get; set; }
        /// <summary>定义层级名称。</summary>
        public string DefinedAt { get; set; } = "";
        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; }
        /// <summary>停用层级名称。</summary>
        public string? DisabledAt { get; set; }
        /// <summary>声明默认的层级名称。</summary>
        public string? DefaultAt { get; set; }
        /// <summary>经过的层级。</summary>
        public List<string> Chain { get; } = [];
    }

    /// <summary>根据层级对象定位其所属 Pool / Workspace。</summary>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">对象 ID。</param>
    private async Task<(string PoolId, string? WorkspaceId)> ResolveTargetAsync(ConfigScope scope, string scopeId)
    {
        if (scope == ConfigScope.Pool) return (scopeId, null);
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == scopeId) ?? throw ApiException.NotFound("Workspace 不存在");
        return (ws.PoolId, scopeId);
    }

    /// <summary>判断模型配置是否声明为默认。</summary>
    /// <param name="it">配置项。</param>
    private static bool IsDefault(ConfigItem it)
    {
        if (it.Kind != ConfigKind.Model || string.IsNullOrWhiteSpace(it.ParamsJson)) return false;
        try { using var d = JsonDocument.Parse(it.ParamsJson); return d.RootElement.TryGetProperty("isDefault", out var v) && v.ValueKind == JsonValueKind.True; } catch { return false; }
    }

    /// <summary>保留上级模型连接参数，合并管理员在下级声明的默认选择和执行限制。</summary>
    /// <param name="baseJson">上级参数。</param>
    /// <param name="overrideJson">下级提交的参数。</param>
    private static string? MergeDefaultFlag(string? baseJson, string? overrideJson)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(baseJson)) try { dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baseJson) ?? dict; } catch { }
        var isDef = false;
        if (!string.IsNullOrWhiteSpace(overrideJson)) try { using var d = JsonDocument.Parse(overrideJson); isDef = d.RootElement.TryGetProperty("isDefault", out var v) && v.ValueKind == JsonValueKind.True; } catch { }
        var o = dict.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        o["isDefault"] = isDef;
        if (!string.IsNullOrWhiteSpace(overrideJson))
        {
            try
            {
                using var document = JsonDocument.Parse(overrideJson);
                foreach (var field in new[] { "taskBudget", "contextWindow" })
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(field, out var value)) o[field] = value.Clone();
            }
            catch (JsonException) { /* Legacy invalid JSON must not break configuration resolution. */ }
        }
        return JsonSerializer.Serialize(o);
    }

    /// <summary>种类中文名。</summary>
    /// <param name="k">种类。</param>
    private static string KindName(ConfigKind k) => k switch { ConfigKind.Model => "模型", ConfigKind.Mcp => "MCP 服务", ConfigKind.Plugin => "插件", _ => "Skill" };

    /// <summary>校验配置键格式。</summary>
    /// <param name="kind">种类。</param>
    /// <param name="key">键。</param>
    private static void ValidateKey(ConfigKind kind, string key)
    {
        var ok = kind switch { ConfigKind.Model => ModelKeyRegex().IsMatch(key), ConfigKind.Skill => SkillKeyRegex().IsMatch(key), ConfigKind.Plugin => PluginKeyRegex().IsMatch(key), _ => McpKeyRegex().IsMatch(key) };
        if (!ok) throw ApiException.BadRequest(kind switch
        {
            ConfigKind.Model => "模型键仅允许字母、数字及 . _ : / -，最长 128",
            ConfigKind.Skill => "Skill 名称仅允许小写字母、数字、连字符，最长 64", ConfigKind.Plugin => "插件标识仅允许字母、数字及 . _ -，最长 64",
            _ => "MCP 服务名仅允许字母、数字、下划线、连字符，最长 32"
        });
    }

    /// <summary>校验参数 JSON 的合法性与必填项。</summary>
    /// <param name="kind">种类。</param>
    /// <param name="json">参数 JSON。</param>
    /// <returns>规范化的 JSON（空则为 null）。</returns>
    private static string? ValidateParams(ConfigKind kind, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { if (kind == ConfigKind.Mcp) throw ApiException.BadRequest("MCP 必须配置 transport 及 command / url"); if (kind == ConfigKind.Plugin) throw ApiException.BadRequest("插件必须配置 package（插件包名）"); return null; }
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement.Clone(); } catch { throw ApiException.BadRequest("参数不是合法的 JSON"); }
        if (root.ValueKind != JsonValueKind.Object) throw ApiException.BadRequest("参数必须是 JSON 对象");
        if (kind == ConfigKind.Model) ModelExecutionLimits.FromElement(root);
        if (kind == ConfigKind.Model && root.TryGetProperty("apiKeyEnv", out var keyName)
            && (keyName.ValueKind != JsonValueKind.String || !EnvironmentNameRegex().IsMatch(keyName.GetString() ?? "")))
            throw ApiException.BadRequest("apiKeyEnv 必须填写密钥名称（例如 MINIMAX_API_KEY），不能填写密钥值；请在同层级的密钥模块保存密钥");
        if (kind == ConfigKind.Plugin && !(root.TryGetProperty("package", out var pkg) && !string.IsNullOrWhiteSpace(pkg.GetString()))) throw ApiException.BadRequest("插件必须配置 package（插件包名）");
        if (kind == ConfigKind.Mcp)
        {
            var transport = root.TryGetProperty("transport", out var t) ? t.GetString() : null;
            if (transport == "stdio" && !(root.TryGetProperty("command", out var c) && !string.IsNullOrWhiteSpace(c.GetString()))) throw ApiException.BadRequest("stdio 类型 MCP 需要 command");
            else if (transport == "streamable-http" && !(root.TryGetProperty("url", out var u) && !string.IsNullOrWhiteSpace(u.GetString()))) throw ApiException.BadRequest("streamable-http 类型 MCP 需要 url");
            else if (transport is not ("stdio" or "streamable-http")) throw ApiException.BadRequest("transport 仅支持 stdio / streamable-http");
        }
        return root.GetRawText();
    }
}
