using Teager.Dsh.Domain;

namespace Teager.Dsh.Infrastructure.Common;

/// <summary>业务异常：由全局中间件转换为统一的 JSON 错误响应。</summary>
/// <param name="status">HTTP 状态码。</param>
/// <param name="code">机器可读的错误码。</param>
/// <param name="message">面向用户的中文错误信息。</param>
public class ApiException(int status, string code, string message) : Exception(message)
{
    /// <summary>HTTP 状态码。</summary>
    public int Status { get; } = status;
    /// <summary>错误码。</summary>
    public string Code { get; } = code;

    /// <summary>400 参数错误。</summary>
    /// <param name="message">错误信息。</param>
    public static ApiException BadRequest(string message) => new(400, "bad_request", message);
    /// <summary>401 未登录。</summary>
    /// <param name="message">错误信息。</param>
    public static ApiException Unauthorized(string message = "未登录或登录已过期") => new(401, "unauthorized", message);
    /// <summary>403 无权限。</summary>
    /// <param name="message">错误信息。</param>
    public static ApiException Forbidden(string message = "无权执行该操作") => new(403, "forbidden", message);
    /// <summary>404 资源不存在（对无权访问的资源同样返回 404，避免泄露存在性）。</summary>
    /// <param name="message">错误信息。</param>
    public static ApiException NotFound(string message = "资源不存在") => new(404, "not_found", message);
    /// <summary>409 冲突。</summary>
    /// <param name="message">错误信息。</param>
    public static ApiException Conflict(string message) => new(409, "conflict", message);
    /// <summary>429 超出配额或限流。</summary>
    /// <param name="message">错误信息。</param>
    public static ApiException Quota(string message) => new(429, "quota_exceeded", message);
}

/// <summary>当前请求的操作主体：Web 登录用户或 API Token 持有者。</summary>
public sealed class Actor
{
    /// <summary>用户 ID。</summary>
    public string UserId { get; init; } = "";
    /// <summary>登录名。</summary>
    public string Username { get; init; } = "";
    /// <summary>是否平台管理员。</summary>
    public bool IsPlatformAdmin { get; init; }
    /// <summary>访问方式：web / token。</summary>
    public string Via { get; init; } = "web";
    /// <summary>Token ID（Web 登录为空）。</summary>
    public string? TokenId { get; init; }
    /// <summary>Token 允许的 Scope；null 表示不受 Scope 限制（Web 登录）。</summary>
    public HashSet<string>? Scopes { get; init; }
    /// <summary>Token 允许的 Pool（空表示不限）。</summary>
    public HashSet<string> AllowedPools { get; init; } = [];
    /// <summary>Token 允许的 Workspace（空表示不限）。</summary>
    public HashSet<string> AllowedWorkspaces { get; init; } = [];
    /// <summary>来源 IP。</summary>
    public string? Ip { get; init; }

    /// <summary>Web 登录不限 Scope；Token 必须包含对应 Scope。</summary>
    /// <param name="scope">需要的 Scope。</param>
    /// <returns>是否具备。</returns>
    public bool HasScope(string scope) => Scopes == null || Scopes.Contains(scope);

    /// <summary>要求具备某 Scope，否则抛 403。</summary>
    /// <param name="scope">需要的 Scope。</param>
    public void RequireScope(string scope)
    {
        if (!HasScope(scope)) throw ApiException.Forbidden($"Token 缺少所需 Scope：{scope}");
    }

    /// <summary>Token 的 Pool 白名单是否放行（Web 登录始终放行）。</summary>
    /// <param name="poolId">Pool ID。</param>
    public bool PoolAllowed(string poolId) => AllowedPools.Count == 0 || AllowedPools.Contains(poolId);

    /// <summary>Token 的 Workspace 白名单是否放行（Web 登录始终放行）。</summary>
    /// <param name="workspaceId">Workspace ID。</param>
    public bool WorkspaceAllowed(string workspaceId) => AllowedWorkspaces.Count == 0 || AllowedWorkspaces.Contains(workspaceId);
}

/// <summary>请求级的 Actor 访问器，由认证处理器写入。</summary>
public class ActorAccessor
{
    /// <summary>当前 Actor，未认证为 null。</summary>
    public Actor? Current { get; set; }

    /// <summary>获取当前 Actor，未认证抛 401。</summary>
    /// <returns>当前 Actor。</returns>
    public Actor Require() => Current ?? throw ApiException.Unauthorized();
}
