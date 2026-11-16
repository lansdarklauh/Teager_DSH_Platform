using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Services;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>某次运行中生效的共享空间挂载。</summary>
/// <param name="MountName">挂载名。</param>
/// <param name="SharedSpaceId">共享空间 ID。</param>
/// <param name="SpaceName">共享空间名称。</param>
/// <param name="Permission">有效权限。</param>
/// <param name="Directory">共享空间真实目录。</param>
public record ActiveMount(string MountName, string SharedSpaceId, string SpaceName, SharePermission Permission, string Directory);

/// <summary>运行前的物化结果。</summary>
/// <param name="Request">发给 Provider 的请求。</param>
/// <param name="Mounts">生效挂载。</param>
/// <param name="Model">本次使用的模型键。</param>
public record RunPreparation(RunRequest Request, List<ActiveMount> Mounts, string? Model);

/// <summary>
/// 运行环境物化：把“平台里的权限 / 配置 / 密钥”落成 Runtime 能理解的目录与文件。
/// Workspace 根目录结构（即 Agent 的 cwd）：<c>files/</c> 私有文件、<c>shared/&lt;挂载名&gt;/</c> 共享空间链接、<c>.dsh/skills/</c> 有效 Skill、<c>AGENTS.md</c> 平台说明。
/// </summary>
public class RuntimeMaterializer(DshDbContext db, StoragePaths paths, PermissionEngine engine, ConfigService config, SecretService secrets, PolicyService policy, IOptions<DshOptions> opt)
{
    private const string SkillMarker = ".platform-managed";

    /// <summary>为一次运行准备好目录、链接、Skill、模型 / MCP 覆盖文件与环境变量。</summary>
    /// <param name="actor">发起运行的主体。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="wa">发起人在 Workspace 的访问结果。</param>
    /// <param name="runId">运行 ID。</param>
    /// <param name="runtimeSessionId">Runtime 会话 ID。</param>
    /// <param name="prompt">用户输入。</param>
    /// <param name="modelKey">请求的模型键（可空）。</param>
    /// <param name="pool">所属 Pool。</param>
    /// <returns>物化结果。</returns>
    public async Task<RunPreparation> PrepareAsync(Actor actor, Workspace ws, WorkspaceAccess wa, string runId, string? runtimeSessionId, string prompt, string? modelKey, Pool pool)
    {
        var root = paths.WorkspaceRoot(ws.Id);
        var eff = await policy.GetEffectiveAsync(pool.Id);

        // 1) 共享空间挂载：只链接“权限 ∩ 挂载启用”的空间，其余链接一律清理
        var mounts = await EnsureLayoutAsync(actor, ws, wa);

        // 2) 有效配置 → Skill 文件 / 模型与 MCP 覆盖
        var effective = await config.ResolveAsync(pool.Id, ws.Id);
        WriteSkills(root, effective.Where(e => e.Kind == ConfigKind.Skill && e.Enabled).ToList());
        var (patchFile, model) = WritePatch(pool.Id, runId, effective, modelKey);

        // 3) 平台说明（Agent 会读取 AGENTS.md）
        WriteAgentsMd(root, ws, mounts, eff);

        // 4) 环境变量：密钥 + 策略
        var env = await secrets.ResolveEnvAsync(pool.Id, ws.Id);
        env["DSH_PERMISSION_MODE"] = eff.SandboxMode;
        env["DSH_PLATFORM_WORKSPACE_ID"] = ws.Id;
        env["DSH_PLATFORM_NETWORK"] = eff.AllowNetwork ? "allow" : "deny";
        if (model != null) env["DSH_SELECTED_MODEL"] = model;

        var timeoutMin = Math.Min(opt.Value.Runtime.RunTimeoutMinutes, eff.MaxRunMinutes > 0 ? eff.MaxRunMinutes : int.MaxValue);
        var req = new RunRequest(runId, pool.Id, ws.Id, root, paths.PoolDshHome(pool.Id), runtimeSessionId, prompt, env, patchFile, TimeSpan.FromMinutes(Math.Max(1, timeoutMin))) { ExecutionActor = actor, Mounts = mounts };
        return new RunPreparation(req, mounts, model);
    }

    /// <summary>确保 Workspace 目录布局：创建 files/ 与 shared/，按“Pool 授权 ∩ 挂载启用 ∩ 用户权限”建立共享空间链接并清理多余链接。</summary>
    /// <param name="actor">发起主体（用于计算用户级授权 / 拒绝）。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="wa">发起人在 Workspace 的访问结果。</param>
    /// <returns>生效的挂载列表。</returns>
    public async Task<List<ActiveMount>> EnsureLayoutAsync(Actor actor, Workspace ws, WorkspaceAccess wa)
    {
        Directory.CreateDirectory(paths.WorkspaceFiles(ws.Id));
        var sharedDir = paths.WorkspaceShared(ws.Id);
        Directory.CreateDirectory(sharedDir);
        var mounts = new List<ActiveMount>();
        var spaces = await db.SharedSpaces.AsNoTracking().ToDictionaryAsync(s => s.Id);
        foreach (var m in await db.WorkspaceSharedSpaceMounts.AsNoTracking().Where(m => m.WorkspaceId == ws.Id && m.Enabled).ToListAsync())
        {
            if (!spaces.TryGetValue(m.SharedSpaceId, out var s) || s.Status != SharedSpaceStatus.Active) continue;
            var acc = await engine.GetWorkspaceSpaceAccessAsync(actor, ws, wa, s, m);
            if (acc.Permission == null) continue;
            var perm = acc.Permission.Value == SharePermission.MANAGE ? SharePermission.RW : acc.Permission.Value;
            var dir = paths.SharedSpace(s.Id);
            Directory.CreateDirectory(dir);
            LinkHelper.EnsureDirLink(Path.Combine(sharedDir, m.MountName), dir);
            mounts.Add(new ActiveMount(m.MountName, s.Id, s.Name, perm, dir));
        }
        LinkHelper.RemoveLinks(sharedDir, mounts.Select(m => m.MountName).ToHashSet());
        return mounts;
    }

    /// <summary>写入平台管理的 Skill（带标记文件，仅清理平台生成的目录，不动用户自建 Skill）。</summary>
    /// <param name="root">Workspace 根目录。</param>
    /// <param name="skills">有效 Skill。</param>
    private static void WriteSkills(string root, List<EffectiveItem> skills)
    {
        var dir = Path.Combine(root, ".dsh", "skills");
        Directory.CreateDirectory(dir);
        var keep = skills.Select(s => s.Key).ToHashSet();
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (File.Exists(Path.Combine(d, SkillMarker)) && !keep.Contains(Path.GetFileName(d))) Directory.Delete(d, true);
        foreach (var s in skills)
        {
            var sd = Path.Combine(dir, s.Key);
            Directory.CreateDirectory(sd);
            var body = s.Content ?? "";
            var text = body.TrimStart().StartsWith("---") ? body : $"---\nname: {s.Key}\ndescription: {Yaml.Scalar(s.DisplayName ?? s.Key)}\n---\n\n{body}\n";
            File.WriteAllText(Path.Combine(sd, "SKILL.md"), text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(sd, SkillMarker), "由 DSH 平台生成，请勿手工修改；在平台的 Skill 管理中维护。");
        }
    }

    /// <summary>生成 DSH 配置覆盖文件：默认模型、自定义 Provider、MCP 服务。返回文件路径与选中的模型键。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="effective">有效配置。</param>
    /// <param name="requestedModel">请求的模型键。</param>
    private (string? File, string? Model) WritePatch(string poolId, string workspaceId, List<EffectiveItem> effective, string? requestedModel)
    {
        var rows = new List<object>();
        var models = effective.Where(e => e.Kind == ConfigKind.Model && e.Enabled).ToList();
        if (!string.IsNullOrEmpty(requestedModel) && models.All(m => m.Key != requestedModel)) throw ApiException.Conflict("所选模型已不可用，请重新选择模型");
        var chosen = models.FirstOrDefault(m => m.Key == requestedModel) ?? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault();
        if (chosen != null)
        {
            var p = ParseParams(chosen.ParamsJson);
            var provider = Str(p, "provider") ?? "deepseek-official";
            var model = Str(p, "model") ?? chosen.Key;
            var cfg = new Dictionary<string, object?> { ["provider"] = provider, ["model"] = model };
            if (Str(p, "reasoningEffort") is { Length: > 0 } re) cfg["reasoningEffort"] = re;
            rows.Add(new Dictionary<string, object?> { ["id"] = "agent-default-model", ["name"] = "@deepseek-ai/dsh-agent-default-model", ["config"] = cfg });

            // 自定义 OpenAI 兼容 Provider：同一 provider 下的多个模型合并到一个条目（config 为整体替换）
            var custom = models.Select(m => (m, p: ParseParams(m.ParamsJson))).Where(x => !string.IsNullOrEmpty(Str(x.p, "baseUrl"))).ToList();
            if (custom.Count > 0)
            {
                var providers = new Dictionary<string, object?>();
                foreach (var g in custom.GroupBy(x => Str(x.p, "provider") ?? x.m.Key))
                {
                    var first = g.First().p;
                    providers[g.Key] = new Dictionary<string, object?>
                    {
                        ["displayName"] = g.Key, ["apiKeyEnv"] = Str(first, "apiKeyEnv") ?? "OPENAI_API_KEY", ["api"] = Str(first, "api") ?? "openai-completions", ["baseURL"] = Str(first, "baseUrl"),
                        ["models"] = g.Select(x =>
                        {
                            var profile = new Dictionary<string, object?> { ["id"] = Str(x.p, "model") ?? x.m.Key, ["name"] = x.m.DisplayName ?? x.m.Key };
                            var limits = ModelExecutionLimits.FromJson(x.m.ParamsJson);
                            if (limits.ContextWindow > 0) profile["contextWindow"] = limits.ContextWindow;
                            return (object)profile;
                        }).ToList()
                    };
                }
                rows.Add(new Dictionary<string, object?> { ["id"] = "llm-pi-ai", ["name"] = "@deepseek-ai/dsh-llm-pi-ai", ["config"] = new Dictionary<string, object?> { ["providers"] = providers } });
            }
        }
        // MCP：作为新条目插入（stdio 的 env / http 的 headers 里 ${NAME} 转为 process.env 引用，避免把密钥写进文件）
        var inserts = new List<object>();
        foreach (var m in effective.Where(e => e.Kind == ConfigKind.Mcp && e.Enabled))
        {
            var p = ParseParams(m.ParamsJson);
            var cfg = new Dictionary<string, object?> { ["serverName"] = m.Key, ["transport"] = Str(p, "transport") };
            if (Str(p, "transport") == "stdio")
            {
                cfg["command"] = Str(p, "command");
                if (p.TryGetValue("args", out var a) && a.ValueKind == JsonValueKind.Array) cfg["args"] = a.EnumerateArray().Select(x => (object?)x.GetString()).ToList();
            }
            else cfg["url"] = Str(p, "url");
            foreach (var field in new[] { "env", "headers" })
                if (p.TryGetValue(field, out var e) && e.ValueKind == JsonValueKind.Object)
                    cfg[field] = e.EnumerateObject().ToDictionary(o => o.Name, o => (object?)Yaml.EnvRef(o.Value.GetString() ?? ""));
            inserts.Add(new Dictionary<string, object?> { ["id"] = "mcp-" + m.Key, ["name"] = "@deepseek-ai/dsh-mcp-client", ["config"] = cfg });
        }
        // 插件：声明式插入（插件包必须已安装在镜像 / DSH_HOME 的 profile 中，平台不会在运行时联网安装）
        foreach (var pl in effective.Where(e => e.Kind == ConfigKind.Plugin && e.Enabled))
        {
            var p = ParseParams(pl.ParamsJson);
            var entry = new Dictionary<string, object?> { ["id"] = "plugin-" + pl.Key, ["name"] = Str(p, "package") };
            if (p.TryGetValue("config", out var pc) && pc.ValueKind == JsonValueKind.Object) entry["config"] = ToObj(pc);
            inserts.Add(entry);
        }
        if (inserts.Count > 0) rows.Add(new Dictionary<string, object?> { ["insert"] = inserts });

        var dir = Path.Combine(paths.PoolRoot(poolId), "patches");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, workspaceId + ".yml");
        // DSH 要求 patch 文件不能为空（空文件启动失败），无覆盖时写入 []
        File.WriteAllText(file, rows.Count == 0 ? "[]\n" : "# 由 DSH 平台生成，每次运行前覆盖\n" + Yaml.Emit(rows), new UTF8Encoding(false));
        return (file, chosen?.Key);
    }

    /// <summary>生成 Workspace 根目录下的 AGENTS.md，向 Agent 说明目录结构与共享空间读写权限。</summary>
    /// <param name="root">Workspace 根目录。</param>
    /// <param name="ws">Workspace。</param>
    /// <param name="mounts">生效挂载。</param>
    /// <param name="eff">有效策略。</param>
    private static void WriteAgentsMd(string root, Workspace ws, List<ActiveMount> mounts, EffectivePolicy eff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 工作空间：{ws.Name}");
        sb.AppendLine();
        sb.AppendLine("> 本文件由 DSH 平台自动生成，请勿修改。");
        sb.AppendLine();
        sb.AppendLine("## 目录结构");
        sb.AppendLine("- `files/`：本工作空间的私有文件，可读写，你的产出请保存在这里。");
        if (mounts.Count == 0) sb.AppendLine("- `shared/`：当前没有挂载任何共享空间。");
        foreach (var m in mounts)
            sb.AppendLine($"- `shared/{m.MountName}/`：共享空间「{m.SpaceName}」，**{(m.Permission == SharePermission.RO ? "只读（RO），禁止修改、删除或新增文件" : "可读写（RW），该目录由多个团队共用，修改前请谨慎")}**。");
        sb.AppendLine();
        sb.AppendLine("## 约束");
        sb.AppendLine($"- 沙箱模式：{eff.SandboxMode}；网络访问：{(eff.AllowNetwork ? "允许" : "受平台策略限制，请不要访问外部网络")}。");
        sb.AppendLine("- 不要读取或输出任何环境变量中的密钥。");
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>解析参数 JSON。</summary>
    /// <param name="json">JSON 文本。</param>
    private static Dictionary<string, JsonElement> ParseParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new(); } catch { return new(); }
    }

    /// <summary>JsonElement 转为字典 / 列表 / 标量对象树，供 YAML 输出。</summary>
    /// <param name="e">JSON 元素。</param>
    private static object? ToObj(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(o => o.Name, o => ToObj(o.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(ToObj).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    /// <summary>读取字符串参数。</summary>
    /// <param name="p">参数字典。</param>
    /// <param name="key">键。</param>
    private static string? Str(Dictionary<string, JsonElement> p, string key) => p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>极简 YAML 输出：仅覆盖字典 / 列表 / 标量，标量统一用 JSON 字符串形式（合法 YAML），并支持 !!js 表达式。</summary>
public static class Yaml
{
    /// <summary>环境变量引用标记：值形如 ${NAME} 时输出为 !!js process.env.NAME，否则为字面量。</summary>
    /// <param name="value">原值。</param>
    public static object EnvRef(string value)
    {
        var t = value.Trim();
        if (t.StartsWith("${") && t.EndsWith('}') && t.Length > 3) return new JsExpr("process.env." + t[2..^1]);
        return value;
    }

    /// <summary>YAML 表达式标量。</summary>
    /// <param name="Code">JavaScript 表达式。</param>
    public record JsExpr(string Code);

    /// <summary>标量序列化选项：不转义中文，保持生成文件可读。</summary>
    private static readonly JsonSerializerOptions ScalarOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>把值转为安全的单行 YAML 标量（JSON 字符串）。</summary>
    /// <param name="s">文本。</param>
    public static string Scalar(string s) => JsonSerializer.Serialize(s, ScalarOptions);

    /// <summary>把对象树输出为 YAML 文本。</summary>
    /// <param name="root">字典 / 列表 / 标量。</param>
    public static string Emit(object? root)
    {
        var sb = new StringBuilder();
        Write(sb, root, 0, true);
        return sb.ToString();
    }

    /// <summary>递归写入。</summary>
    /// <param name="sb">输出。</param>
    /// <param name="v">值。</param>
    /// <param name="indent">缩进层级。</param>
    /// <param name="top">是否顶层。</param>
    private static void Write(StringBuilder sb, object? v, int indent, bool top = false)
    {
        var pad = new string(' ', indent * 2);
        switch (v)
        {
            case IDictionary<string, object?> d:
                foreach (var kv in d)
                {
                    if (kv.Value is null) continue;
                    if (IsScalar(kv.Value)) sb.Append(pad).Append(Scalar(kv.Key)).Append(": ").AppendLine(Inline(kv.Value));
                    else { sb.Append(pad).Append(Scalar(kv.Key)).AppendLine(":"); Write(sb, kv.Value, indent + 1); }
                }
                break;
            case System.Collections.IEnumerable list when v is not string:
                foreach (var item in list)
                {
                    if (IsScalar(item)) { sb.Append(pad).Append("- ").AppendLine(Inline(item)); continue; }
                    var sub = new StringBuilder();
                    Write(sub, item, indent + 1);
                    var lines = sub.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < lines.Length; i++) sb.Append(i == 0 ? pad + "- " + lines[i].TrimStart() : lines[i]).Append('\n');
                }
                break;
            default: sb.Append(pad).AppendLine(Inline(v)); break;
        }
    }

    /// <summary>是否标量。</summary>
    /// <param name="v">值。</param>
    private static bool IsScalar(object? v) => v is null or string or bool or int or long or double or JsExpr;

    /// <summary>标量的单行文本。</summary>
    /// <param name="v">值。</param>
    private static string Inline(object? v) => v switch
    {
        null => "null", string s => Scalar(s), bool b => b ? "true" : "false", JsExpr j => "!!js " + Scalar(j.Code), _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
    };
}
