using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Api.Security;

/// <summary>认证常量。</summary>
public static class PlatformAuth
{
    /// <summary>认证方案名。</summary>
    public const string Scheme = "Platform";
    /// <summary>Web 登录 Cookie 名。</summary>
    public const string CookieName = "dsh_session";
    /// <summary>浏览器发起写请求必须携带的头（配合 SameSite 防 CSRF）。</summary>
    public const string CsrfHeader = "X-Requested-With";
    /// <summary>CSRF 头的期望值。</summary>
    public const string CsrfValue = "dsh-web";

    /// <summary>获取客户端 IP（已由转发头中间件处理）。</summary>
    /// <param name="ctx">HTTP 上下文。</param>
    public static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString();

    /// <summary>判断是否应设置 Secure Cookie。</summary>
    /// <param name="ctx">HTTP 上下文。</param>
    /// <param name="mode">配置：auto / true / false。</param>
    public static bool UseSecureCookie(HttpContext ctx, string mode) => mode.Equals("true", StringComparison.OrdinalIgnoreCase) || (mode.Equals("auto", StringComparison.OrdinalIgnoreCase) && ctx.Request.IsHttps);
}

/// <summary>
/// 统一认证处理器：Authorization: Bearer dshp_xxx（SDK / API）或 HttpOnly Cookie（Web）二选一，
/// 认证结果写入请求级的 <see cref="ActorAccessor"/>，后续授权全部由权限引擎完成。
/// </summary>
public class PlatformAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ActorAccessor actorAccessor, AuthService auth, TokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ip = PlatformAuth.ClientIp(Context);
        Actor? actor = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            actor = await tokens.ValidateAsync(header["Bearer ".Length..].Trim(), ip);
            if (actor == null) return AuthenticateResult.Fail("Token 无效、已过期或已被吊销");
        }
        else if (Request.Cookies.TryGetValue(PlatformAuth.CookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
        {
            var user = await auth.ValidateSessionAsync(cookie);
            if (user == null) return AuthenticateResult.NoResult();
            actor = new Actor { UserId = user.Id, Username = user.Username, IsPlatformAdmin = user.Role == PlatformRole.Admin, Via = "web", Ip = ip };
        }
        if (actor == null) return AuthenticateResult.NoResult();

        actorAccessor.Current = actor;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.UserId), new Claim(ClaimTypes.Name, actor.Username)], PlatformAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), PlatformAuth.Scheme));
    }

    /// <inheritdoc />
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        await Response.WriteAsJsonAsync(new { code = "unauthorized", message = "未登录或登录已过期" });
    }

    /// <inheritdoc />
    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        await Response.WriteAsJsonAsync(new { code = "forbidden", message = "无权执行该操作" });
    }
}

/// <summary>Token Scope 校验特性：标注在控制器或动作上，Web 登录不受限，API Token 必须包含指定 Scope。</summary>
/// <param name="scope">所需 Scope。</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class ScopeAttribute(string scope) : Attribute, IAsyncActionFilter
{
    /// <inheritdoc />
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<ActorAccessor>();
        accessor.Require().RequireScope(scope);
        return next();
    }
}

/// <summary>全局异常与 CSRF 中间件：业务异常转统一 JSON；带 Cookie 的写请求必须携带 CSRF 头。</summary>
public class GatewayMiddleware(RequestDelegate next, ILogger<GatewayMiddleware> logger)
{
    /// <summary>处理请求。</summary>
    /// <param name="ctx">HTTP 上下文。</param>
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            var m = ctx.Request.Method;
            var unsafeMethod = !(HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m));
            if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Cookies.ContainsKey(PlatformAuth.CookieName)
                && !ctx.Request.Headers.ContainsKey("Authorization")
                && ctx.Request.Headers[PlatformAuth.CsrfHeader] != PlatformAuth.CsrfValue)
                throw ApiException.Forbidden("缺少 CSRF 校验头");
            await next(ctx);
        }
        catch (ApiException ex) { await Write(ctx, ex.Status, ex.Code, ex.Message); }
        catch (ArgumentException ex) { await Write(ctx, 400, "bad_request", ex.Message); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        { await Write(ctx, 409, "conflict", "数据已存在（唯一约束冲突）"); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* 客户端断开 */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "未处理异常 {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            await Write(ctx, 500, "internal_error", "服务器内部错误，请稍后重试或联系管理员");
        }
    }

    /// <summary>写入 JSON 错误响应（响应已开始时忽略）。</summary>
    /// <param name="ctx">HTTP 上下文。</param>
    /// <param name="status">状态码。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误信息。</param>
    private static async Task Write(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { code, message });
    }
}
