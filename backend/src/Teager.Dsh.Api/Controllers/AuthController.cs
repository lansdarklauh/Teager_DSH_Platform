using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Api.Controllers;

/// <summary>登录请求。</summary>
/// <param name="Username">登录名。</param>
/// <param name="Password">密码。</param>
public record LoginRequest(string Username, string Password);
/// <summary>修改密码请求。</summary>
/// <param name="OldPassword">旧密码。</param>
/// <param name="NewPassword">新密码。</param>
public record ChangePasswordRequest(string OldPassword, string NewPassword);

/// <summary>认证：用户名密码登录，登录态为服务端 Session + HttpOnly Cookie；退出、当前用户、改密。</summary>
public class AuthController(ActorAccessor accessor, AuthService auth, IOptions<DshOptions> opt, DshDbContext db, PermissionEngine engine, AuditService audit, PersonalWorkspaceService personal) : ApiBase(accessor)
{
    /// <summary>登录（按 IP 限流）。</summary>
    [HttpPost("auth/login"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<object> Login(LoginRequest req)
    {
        var (token, exp, user) = await auth.LoginAsync(req.Username, req.Password, PlatformAuth.ClientIp(HttpContext), Request.Headers.UserAgent.ToString());
        Response.Cookies.Append(PlatformAuth.CookieName, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = PlatformAuth.UseSecureCookie(HttpContext, opt.Value.Platform.CookieSecure), Expires = new DateTimeOffset(exp), Path = "/"
        });
        return await MeView(user.Id, "web");
    }

    /// <summary>退出登录。</summary>
    [HttpPost("auth/logout")]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue(PlatformAuth.CookieName, out var t)) await auth.LogoutAsync(t);
        await audit.LogAsync(Me, "auth.logout", "User", Me.UserId);
        Response.Cookies.Delete(PlatformAuth.CookieName, new CookieOptions { Path = "/" });
        return NoContent();
    }

    /// <summary>当前用户信息。</summary>
    [HttpGet("auth/me")]
    public async Task<object> Me_() => await MeView(Me.UserId, Me.Via);

    /// <summary>修改自己的密码。</summary>
    [HttpPost("auth/change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        if (Me.Via != "web") throw ApiException.Forbidden("请在 Web 登录状态下修改密码");
        Request.Cookies.TryGetValue(PlatformAuth.CookieName, out var current);
        await auth.ChangePasswordAsync(Me.UserId, req.OldPassword, req.NewPassword, current);
        await audit.LogAsync(Me, "auth.password.change", "User", Me.UserId);
        return NoContent();
    }

    /// <summary>用户视图（含通过 Pool 关系计算的有效 Pool 集合）。</summary>
    /// <param name="userId">用户 ID。</param>
    /// <param name="via">访问方式（web / token）。</param>
    private async Task<object> MeView(string userId, string via)
    {
        if (via == "web") await personal.EnsureAsync(userId);
        var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == userId);
        var graph = await engine.GetGraphAsync();
        var direct = await engine.GetDirectRolesAsync(userId);
        var effective = await engine.GetEffectivePoolIdsAsync(userId);
        if (via == "token")
        {
            var visible = await engine.GetAccessiblePoolsAsync(Me);
            direct = direct.Where(p => visible.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            effective.IntersectWith(visible.Keys);
        }
        return new
        {
            u.Id, u.Username, u.DisplayName, u.Email, Role = u.Role.ToString(), u.IsServiceAccount, u.LastLoginAt,
            u.DefaultWorkspaceId,
            LandingPath = u.Role == PlatformRole.Admin ? "/app" : u.DefaultWorkspaceId == null ? "/app" : "/w/" + u.DefaultWorkspaceId,
            Via = via,
            DirectPools = direct.Where(kv => graph.Nodes.ContainsKey(kv.Key)).Select(kv => new { Id = kv.Key, Name = graph.NameOf(kv.Key), Role = kv.Value.ToString() }),
            EffectivePools = effective.Select(p => new { Id = p, Name = graph.NameOf(p), Direct = direct.ContainsKey(p) })
        };
    }
}
