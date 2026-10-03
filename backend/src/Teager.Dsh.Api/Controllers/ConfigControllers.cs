using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Runtime;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Api.Controllers;

/// <summary>配置项写入请求。</summary>
/// <param name="Kind">种类 Model / Skill / Mcp。</param>
/// <param name="Scope">层级 Platform / Pool / Workspace。</param>
/// <param name="ScopeId">层级对象 ID。</param>
/// <param name="Key">配置键。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Params">参数对象。</param>
/// <param name="Content">正文（Skill）。</param>
public record ConfigRequest(ConfigKind Kind, ConfigScope Scope, string? ScopeId, string Key, string? DisplayName, bool? Enabled, System.Text.Json.JsonElement? Params, string? Content);
/// <summary>密钥写入请求。</summary>
/// <param name="Scope">层级。</param>
/// <param name="ScopeId">层级对象 ID。</param>
/// <param name="Name">名称（环境变量名）。</param>
/// <param name="Value">密钥值。</param>
/// <param name="Description">描述。</param>
public record SecretRequest(ConfigScope Scope, string? ScopeId, string Name, string Value, string? Description);
/// <summary>创建 Token 请求。</summary>
/// <param name="Name">名称。</param>
/// <param name="Type">类型。</param>
/// <param name="Scopes">Scope 列表。</param>
/// <param name="AllowedPools">允许的 Pool。</param>
/// <param name="AllowedWorkspaces">允许的 Workspace。</param>
/// <param name="ExpiresInDays">有效天数。</param>
/// <param name="ExpiresInHours">有效小时（临时 Token）。</param>
/// <param name="ServiceAccountId">服务账号 ID（服务 Token）。</param>
public record CreateTokenRequest(string Name, TokenType? Type, string[]? Scopes, string[]? AllowedPools, string[]? AllowedWorkspaces, int? ExpiresInDays, int? ExpiresInHours, string? ServiceAccountId);

/// <summary>模型 / Skill / MCP 配置：按层级维护，逐级继承（子级只能收紧，扩大需授权）。</summary>
public class ConfigsController(ActorAccessor accessor, ConfigService config, AuditService audit, DshDbContext db) : ApiBase(accessor)
{
    /// <summary>某层级自身定义的配置项。</summary>
    [HttpGet("configs"), Scope(Scopes.PoolRead)]
    public async Task<object> List([FromQuery] ConfigScope scope, [FromQuery] string? scopeId, [FromQuery] ConfigKind? kind)
        => (await config.ListAsync(Me, kind, scope, scopeId ?? "")).Select(c => new { c.Id, Kind = c.Kind.ToString(), Scope = c.Scope.ToString(), c.ScopeId, c.Key, c.DisplayName, c.Enabled, Params = Parse(c.ParamsJson), c.Content, c.UpdatedBy, c.UpdatedAt });

    /// <summary>新增 / 更新配置项。</summary>
    [HttpPut("configs"), Scope(Scopes.PoolManage)]
    public async Task<object> Upsert(ConfigRequest req)
    {
        var item = await config.UpsertAsync(Me, req.Kind, req.Scope, req.ScopeId ?? "", new ConfigUpsert(req.Key, req.DisplayName, req.Enabled ?? true, req.Params?.GetRawText(), req.Content));
        var (pool, ws) = await Ctx(req.Scope, req.ScopeId);
        await audit.LogAsync(Me, "config.upsert", "Config", item.Id, pool, ws, $"{req.Kind} {req.Key} @{req.Scope} 启用={item.Enabled}");
        return new { item.Id };
    }

    /// <summary>删除配置项（取消该层覆盖）。</summary>
    [HttpDelete("configs/{id}"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> Delete(string id)
    {
        var item = await config.DeleteAsync(Me, id);
        var (pool, ws) = await Ctx(item.Scope, item.ScopeId);
        await audit.LogAsync(Me, "config.delete", "Config", id, pool, ws, $"{item.Kind} {item.Key} @{item.Scope}");
        return NoContent();
    }

    /// <summary>解析审计上下文中的 Pool / Workspace。</summary>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">对象 ID。</param>
    private async Task<(string? Pool, string? Ws)> Ctx(ConfigScope scope, string? scopeId) => scope switch
    {
        ConfigScope.Pool => (scopeId, null),
        ConfigScope.Workspace => ((await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == scopeId))?.PoolId, scopeId),
        _ => (null, null)
    };

    /// <summary>解析参数 JSON 为对象返回。</summary>
    /// <param name="json">JSON 文本。</param>
    private static object? Parse(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json); } catch { return null; } }
}

/// <summary>密钥管理：只写不读，值经 AES-GCM 加密，仅在启动 Runtime 时注入环境变量。</summary>
public class SecretsController(ActorAccessor accessor, SecretService secrets, AuditService audit, DshDbContext db) : ApiBase(accessor)
{
    /// <summary>某层级的密钥列表（不含值）。</summary>
    [HttpGet("secrets"), Scope(Scopes.PoolManage)]
    public async Task<object> List([FromQuery] ConfigScope scope, [FromQuery] string? scopeId) => await secrets.ListAsync(Me, scope, scopeId ?? "");

    /// <summary>新增 / 覆盖密钥。</summary>
    [HttpPut("secrets"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> Upsert(SecretRequest req)
    {
        await secrets.UpsertAsync(Me, req.Scope, req.ScopeId ?? "", req.Name, req.Value, req.Description);
        var pool = req.Scope == ConfigScope.Pool ? req.ScopeId : req.Scope == ConfigScope.Workspace ? (await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == req.ScopeId))?.PoolId : null;
        await audit.LogAsync(Me, "secret.upsert", "Secret", req.Name, pool, req.Scope == ConfigScope.Workspace ? req.ScopeId : null, $"设置密钥 {req.Name} @{req.Scope}（值不记录）");
        return NoContent();
    }

    /// <summary>删除密钥。</summary>
    [HttpDelete("secrets/{id}"), Scope(Scopes.PoolManage)]
    public async Task<IActionResult> Delete(string id)
    {
        var s = await secrets.DeleteAsync(Me, id);
        await audit.LogAsync(Me, "secret.delete", "Secret", s.Name, s.Scope == ConfigScope.Pool ? s.ScopeId : null, detail: $"删除密钥 {s.Name} @{s.Scope}");
        return NoContent();
    }
}

/// <summary>API Token：个人 / 服务 / 临时，含 Scope 与 Pool / Workspace 白名单。明文仅创建时返回一次。</summary>
public class TokensController(ActorAccessor accessor, TokenService tokens, AuditService audit) : ApiBase(accessor)
{
    /// <summary>Token 列表（默认自己的，管理员 all=true 查看全部）。</summary>
    [HttpGet("tokens")]
    public async Task<object> List([FromQuery] bool all = false) => await tokens.ListAsync(Me, all);

    /// <summary>创建 Token（仅 Web 登录态）。</summary>
    [HttpPost("tokens")]
    public async Task<object> Create(CreateTokenRequest req)
    {
        var (t, plain) = await tokens.CreateAsync(Me, req.Name, req.Type ?? TokenType.Personal, req.Scopes ?? [], req.AllowedPools ?? [], req.AllowedWorkspaces ?? [], req.ExpiresInDays, req.ExpiresInHours, req.ServiceAccountId);
        await audit.LogAsync(Me, "token.create", "ApiToken", t.Id, detail: $"创建 {t.Type} Token「{t.Name}」（{t.TokenPrefix}…），过期：{t.ExpiresAt:yyyy-MM-dd HH:mm}，Scope：{t.ScopesJson}");
        return new { t.Id, t.Name, Token = plain, t.ExpiresAt, Notice = "Token 仅显示一次，请立即保存" };
    }

    /// <summary>吊销 Token。</summary>
    [HttpDelete("tokens/{id}")]
    public async Task<IActionResult> Revoke(string id)
    {
        var t = await tokens.RevokeAsync(Me, id);
        await audit.LogAsync(Me, "token.revoke", "ApiToken", id, detail: $"吊销 Token「{t.Name}」");
        return NoContent();
    }
}

/// <summary>审计日志查询。</summary>
public class AuditController(ActorAccessor accessor, AuditService audit) : ApiBase(accessor)
{
    /// <summary>分页查询：管理员看全部，Pool 管理者看所辖 Pool，其他人只看自己。</summary>
    [HttpGet("audit")]
    public async Task<object> Query([FromQuery] string? poolId, [FromQuery] bool includeDescendants, [FromQuery] string? workspaceId, [FromQuery] string? action, [FromQuery] string? actor,
        [FromQuery] string? result, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var (total, items) = await audit.QueryAsync(Me, new AuditQuery(poolId, includeDescendants, workspaceId, action, actor, result, from, to, keyword, page, pageSize));
        return new { total, page, pageSize, items = items.Select(a => new { a.Id, a.Time, a.ActorName, a.Ip, a.Via, a.Action, a.ResourceType, a.ResourceId, a.PoolId, a.WorkspaceId, a.Result, a.Detail }) };
    }
}

/// <summary>用量统计。</summary>
public class UsageController(ActorAccessor accessor, UsageService usage) : ApiBase(accessor)
{
    /// <summary>用量汇总与趋势。</summary>
    [HttpGet("usage/summary")]
    public async Task<object> Summary([FromQuery] string? poolId, [FromQuery] string? workspaceId, [FromQuery] int days = 14) => await usage.SummaryAsync(Me, poolId, workspaceId, days);
}

/// <summary>元信息：Scope 清单、限制、能力开关，供前端与 SDK 使用。</summary>
public class MetaController(ActorAccessor accessor, IOptions<DshOptions> opt, IEnumerable<IRuntimeProvider> providers) : ApiBase(accessor)
{
    /// <summary>平台元信息。</summary>
    [HttpGet("meta")]
    public object Get() => new
    {
        Name = "DSH 多工作空间管理平台", Version = typeof(MetaController).Assembly.GetName().Version?.ToString(),
        Scopes = Scopes.All, MaxPoolDepth = opt.Value.Platform.MaxPoolDepth, MaxUploadMb = opt.Value.Platform.MaxUploadMb, ArchiveRetentionDays = opt.Value.Platform.ArchiveRetentionDays,
        TerminalEnabled = opt.Value.Runtime.TerminalEnabled, RuntimeProviders = providers.Select(p => p.Name), DefaultProvider = opt.Value.Runtime.DefaultProvider, PublicBaseUrl = opt.Value.Platform.PublicBaseUrl,
        TokenTypes = new[] { "Personal", "Service", "Temporary" }, Me = new { Me.Username, Me.IsPlatformAdmin, Me.Via }
    };
}
