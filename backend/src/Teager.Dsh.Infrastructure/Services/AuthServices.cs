using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>认证服务：用户名密码登录、平台 Session（HttpOnly Cookie 对应的服务端会话）、改密。</summary>
public class AuthService(DshDbContext db, IOptions<DshOptions> opt, AuditService audit)
{
    /// <summary>用户不存在时用于等时校验的占位哈希。</summary>
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dsh-dummy-password-for-timing");

    /// <summary>计算令牌的 SHA256 十六进制摘要（库中只存摘要，不存明文）。</summary>
    /// <param name="token">令牌明文。</param>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>生成 URL 安全的随机令牌。</summary>
    /// <param name="bytes">随机字节数。</param>
    public static string RandomToken(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>校验密码强度：至少 8 位，且同时包含字母与数字。</summary>
    /// <param name="password">密码。</param>
    public static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw ApiException.BadRequest("密码至少 8 位，且必须同时包含字母和数字");
        if (password.Length > 128) throw ApiException.BadRequest("密码过长");
    }

    /// <summary>登录：成功后创建平台 Session 并返回 Cookie 令牌明文。</summary>
    /// <param name="username">登录名。</param>
    /// <param name="password">密码。</param>
    /// <param name="ip">来源 IP。</param>
    /// <param name="userAgent">浏览器 UA。</param>
    /// <returns>Cookie 令牌、过期时间与用户。</returns>
    public async Task<(string Token, DateTime ExpiresAt, User User)> LoginAsync(string username, string password, string? ip, string? userAgent)
    {
        username = (username ?? "").Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        // 用户不存在时也做一次哈希校验，避免通过响应时间探测用户名
        var ok = BCrypt.Net.BCrypt.Verify(password ?? "", user?.PasswordHash ?? DummyHash);
        if (user == null || !ok || user.Status != UserStatus.Active || user.IsServiceAccount)
        {
            await audit.LogAsync(new Actor { Username = username, Ip = ip }, "auth.login", "User", user?.Id, detail: "用户名或密码错误，或账号已禁用", result: "denied");
            throw ApiException.Unauthorized("用户名或密码错误");
        }
        var token = RandomToken();
        var exp = DateTime.Now.AddHours(opt.Value.Platform.SessionHours);
        db.PlatformSessions.Add(new PlatformSession { Id = Hash(token), UserId = user.Id, ExpiresAt = exp, Ip = ip, UserAgent = userAgent?.Length > 400 ? userAgent[..400] : userAgent });
        user.LastLoginAt = DateTime.Now;
        await db.SaveChangesAsync();
        await audit.LogAsync(new Actor { UserId = user.Id, Username = user.Username, Ip = ip }, "auth.login", "User", user.Id);
        return (token, exp, user);
    }

    /// <summary>退出登录：删除服务端会话。</summary>
    /// <param name="token">Cookie 令牌。</param>
    public async Task LogoutAsync(string token)
    {
        var id = Hash(token);
        var s = await db.PlatformSessions.FirstOrDefaultAsync(x => x.Id == id);
        if (s != null) { db.PlatformSessions.Remove(s); await db.SaveChangesAsync(); }
    }

    /// <summary>校验 Cookie 令牌，返回用户；无效 / 过期 / 用户被禁用返回 null。会话有效期采用滑动续期。</summary>
    /// <param name="token">Cookie 令牌。</param>
    public async Task<User?> ValidateSessionAsync(string token)
    {
        var id = Hash(token);
        var s = await db.PlatformSessions.FirstOrDefaultAsync(x => x.Id == id);
        if (s == null || s.ExpiresAt < DateTime.Now) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == s.UserId);
        if (user == null || user.Status != UserStatus.Active) return null;
        if (DateTime.Now - s.LastSeenAt > TimeSpan.FromMinutes(5))
        {
            s.LastSeenAt = DateTime.Now; s.ExpiresAt = DateTime.Now.AddHours(opt.Value.Platform.SessionHours);
            await db.SaveChangesAsync();
        }
        return user;
    }

    /// <summary>修改密码，成功后使该用户的其他会话失效。</summary>
    /// <param name="userId">用户 ID。</param>
    /// <param name="oldPassword">旧密码。</param>
    /// <param name="newPassword">新密码。</param>
    /// <param name="currentToken">当前会话令牌（保留）。</param>
    public async Task ChangePasswordAsync(string userId, string oldPassword, string newPassword, string? currentToken)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        if (user.PasswordHash == null || !BCrypt.Net.BCrypt.Verify(oldPassword ?? "", user.PasswordHash)) throw ApiException.BadRequest("旧密码不正确");
        ValidatePassword(newPassword);
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword); user.UpdatedAt = DateTime.Now;
        var keep = currentToken == null ? null : Hash(currentToken);
        db.PlatformSessions.RemoveRange(await db.PlatformSessions.Where(s => s.UserId == userId && s.Id != keep).ToListAsync());
        await db.SaveChangesAsync();
    }
}

/// <summary>用户管理服务（平台管理员）。</summary>
public class UserService(DshDbContext db)
{
    /// <summary>分页 / 关键字查询用户。</summary>
    /// <param name="q">关键字（登录名 / 显示名）。</param>
    /// <returns>用户列表。</returns>
    public async Task<List<User>> ListAsync(string? q)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(u => u.Username.Contains(q) || u.DisplayName.Contains(q));
        return await query.OrderBy(u => u.Username).Take(500).ToListAsync();
    }

    /// <summary>成员选择器用的轻量查询：任何登录用户可用，仅返回有限字段与条数。</summary>
    /// <param name="q">关键字。</param>
    public async Task<List<object>> LookupAsync(string? q)
    {
        var query = db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(u => u.Username.Contains(q) || u.DisplayName.Contains(q));
        return (await query.OrderBy(u => u.Username).Take(20).ToListAsync()).Select(u => (object)new { u.Id, u.Username, u.DisplayName, u.IsServiceAccount }).ToList();
    }

    /// <summary>创建用户（或服务账号）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="username">登录名。</param>
    /// <param name="displayName">显示名。</param>
    /// <param name="email">邮箱。</param>
    /// <param name="password">初始密码（服务账号可空）。</param>
    /// <param name="role">平台角色。</param>
    /// <param name="isService">是否服务账号。</param>
    /// <returns>新用户。</returns>
    public async Task<User> CreateAsync(Actor actor, string username, string displayName, string? email, string? password, PlatformRole role, bool isService)
    {
        username = (username ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9._-]{2,40}$")) throw ApiException.BadRequest("登录名只能包含字母、数字、点、下划线、连字符，长度 2-40");
        if (await db.Users.AnyAsync(u => u.Username == username)) throw ApiException.Conflict("登录名已存在");
        if (!isService) AuthService.ValidatePassword(password);
        if (isService && role == PlatformRole.Admin) throw ApiException.BadRequest("服务账号不能是平台管理员");
        var u = new User
        {
            Id = IdGen.New("usr"), OrganizationId = await db.Organizations.Select(o => o.Id).FirstAsync(), Username = username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(), Email = email, Role = role, IsServiceAccount = isService,
            PasswordHash = isService ? null : BCrypt.Net.BCrypt.HashPassword(password)
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    /// <summary>更新用户资料 / 角色 / 状态；不允许移除最后一个可用的平台管理员。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">用户 ID。</param>
    /// <param name="displayName">显示名。</param>
    /// <param name="email">邮箱。</param>
    /// <param name="role">角色。</param>
    /// <param name="status">状态。</param>
    /// <returns>更新后的用户。</returns>
    public async Task<User> UpdateAsync(Actor actor, string id, string? displayName, string? email, PlatformRole? role, UserStatus? status)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("用户不存在");
        var losesAdmin = u.Role == PlatformRole.Admin && u.Status == UserStatus.Active && ((role != null && role != PlatformRole.Admin) || (status != null && status != UserStatus.Active));
        if (losesAdmin && await db.Users.CountAsync(x => x.Role == PlatformRole.Admin && x.Status == UserStatus.Active) <= 1) throw ApiException.BadRequest("不能移除或禁用最后一个可用的平台管理员");
        if (displayName != null) u.DisplayName = displayName.Trim();
        if (email != null) u.Email = email;
        if (role != null) { if (u.IsServiceAccount && role == PlatformRole.Admin) throw ApiException.BadRequest("服务账号不能是平台管理员"); u.Role = role.Value; }
        if (status != null)
        {
            u.Status = status.Value;
            if (status == UserStatus.Disabled) db.PlatformSessions.RemoveRange(await db.PlatformSessions.Where(s => s.UserId == id).ToListAsync());
        }
        u.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        return u;
    }

    /// <summary>管理员重置用户密码，并使其所有会话失效。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">用户 ID。</param>
    /// <param name="newPassword">新密码。</param>
    public async Task ResetPasswordAsync(Actor actor, string id, string newPassword)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("用户不存在");
        if (u.IsServiceAccount) throw ApiException.BadRequest("服务账号没有密码");
        AuthService.ValidatePassword(newPassword);
        u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword); u.UpdatedAt = DateTime.Now;
        db.PlatformSessions.RemoveRange(await db.PlatformSessions.Where(s => s.UserId == id).ToListAsync());
        await db.SaveChangesAsync();
    }
}

/// <summary>API Token 服务：创建 / 校验 / 吊销。Token 明文仅在创建时返回一次，库中只存 SHA256。</summary>
public class TokenService(DshDbContext db, PermissionEngine engine)
{
    /// <summary>Token 前缀。</summary>
    public const string Prefix = "dshp_";

    /// <summary>创建 Token。</summary>
    /// <param name="actor">创建者（必须是 Web 登录，不允许用 Token 创建 Token）。</param>
    /// <param name="name">名称。</param>
    /// <param name="type">类型。</param>
    /// <param name="scopes">Scope 列表。</param>
    /// <param name="allowedPools">允许的 Pool（空为不限）。</param>
    /// <param name="allowedWorkspaces">允许的 Workspace（空为不限）。</param>
    /// <param name="expiresInDays">有效天数（临时 Token 以小时计见 expiresInHours）。</param>
    /// <param name="expiresInHours">有效小时数（仅临时 Token）。</param>
    /// <param name="serviceAccountId">服务 Token 归属的服务账号（仅平台管理员）。</param>
    /// <returns>Token 记录与明文。</returns>
    public async Task<(ApiToken Token, string Plain)> CreateAsync(Actor actor, string name, TokenType type, string[] scopes, string[] allowedPools, string[] allowedWorkspaces, int? expiresInDays, int? expiresInHours, string? serviceAccountId)
    {
        if (actor.Via != "web") throw ApiException.Forbidden("Token 只能通过 Web 登录态创建");
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 100) throw ApiException.BadRequest("Token 名称不能为空且不超过 100 字符");
        if (scopes.Length == 0) throw ApiException.BadRequest("至少选择一个 Scope");
        var bad = scopes.Except(Scopes.All).ToList();
        if (bad.Count > 0) throw ApiException.BadRequest("未知 Scope：" + string.Join(", ", bad));

        var owner = actor.UserId;
        if (type == TokenType.Service)
        {
            if (!actor.IsPlatformAdmin) throw ApiException.Forbidden("只有平台管理员可以创建服务 Token");
            var svc = await db.Users.FirstOrDefaultAsync(u => u.Id == serviceAccountId && u.IsServiceAccount && u.Status == UserStatus.Active) ?? throw ApiException.BadRequest("请选择有效的服务账号");
            owner = svc.Id;
        }
        // 白名单里的 Pool / Workspace 创建者必须自己可见，避免通过 Token 探测不可见资源
        foreach (var p in allowedPools) await engine.RequirePoolViewAsync(actor, p);
        foreach (var w in allowedWorkspaces) await engine.RequireWorkspaceAsync(actor, w, null);

        DateTime? exp = type switch
        {
            TokenType.Temporary => DateTime.Now.AddHours(Math.Clamp(expiresInHours ?? 1, 1, 24)),
            TokenType.Service => expiresInDays is > 0 ? DateTime.Now.AddDays(Math.Min(expiresInDays.Value, 730)) : DateTime.Now.AddDays(365),
            _ => expiresInDays is > 0 ? DateTime.Now.AddDays(Math.Min(expiresInDays.Value, 365)) : DateTime.Now.AddDays(90)
        };
        var plain = Prefix + AuthService.RandomToken(30);
        var t = new ApiToken
        {
            Id = IdGen.New("tok"), OwnerUserId = owner, Name = name, Type = type, TokenHash = AuthService.Hash(plain), TokenPrefix = plain[..12],
            ScopesJson = JsonSerializer.Serialize(scopes.Distinct()), AllowedPoolsJson = JsonSerializer.Serialize(allowedPools.Distinct()),
            AllowedWorkspacesJson = JsonSerializer.Serialize(allowedWorkspaces.Distinct()), ExpiresAt = exp, CreatedBy = actor.UserId
        };
        db.ApiTokens.Add(t);
        await db.SaveChangesAsync();
        return (t, plain);
    }

    /// <summary>列出 Token（默认仅自己的；管理员可查看全部）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="all">是否查看全部（仅管理员）。</param>
    public async Task<List<object>> ListAsync(Actor actor, bool all)
    {
        var q = db.ApiTokens.AsNoTracking().AsQueryable();
        if (!(all && actor.IsPlatformAdmin)) q = q.Where(t => t.OwnerUserId == actor.UserId || t.CreatedBy == actor.UserId);
        var rows = await q.OrderByDescending(t => t.CreatedAt).Take(500).ToListAsync();
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Username);
        return rows.Select(t => (object)new
        {
            t.Id, t.Name, Type = t.Type.ToString(), t.TokenPrefix, Owner = users.GetValueOrDefault(t.OwnerUserId), t.OwnerUserId,
            Scopes = JsonSerializer.Deserialize<string[]>(t.ScopesJson), AllowedPools = JsonSerializer.Deserialize<string[]>(t.AllowedPoolsJson), AllowedWorkspaces = JsonSerializer.Deserialize<string[]>(t.AllowedWorkspacesJson),
            t.ExpiresAt, t.RevokedAt, t.LastUsedAt, t.LastUsedIp, t.CreatedAt,
            Status = t.RevokedAt != null ? "已吊销" : t.ExpiresAt != null && t.ExpiresAt < DateTime.Now ? "已过期" : "有效"
        }).ToList();
    }

    /// <summary>吊销 Token（所有者、创建者或管理员）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">Token ID。</param>
    public async Task<ApiToken> RevokeAsync(Actor actor, string id)
    {
        var t = await db.ApiTokens.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Token 不存在");
        if (t.OwnerUserId != actor.UserId && t.CreatedBy != actor.UserId && !actor.IsPlatformAdmin) throw ApiException.NotFound("Token 不存在");
        t.RevokedAt ??= DateTime.Now;
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>校验 Bearer Token，返回 Actor；无效返回 null。</summary>
    /// <param name="plain">Token 明文。</param>
    /// <param name="ip">来源 IP。</param>
    public async Task<Actor?> ValidateAsync(string plain, string? ip)
    {
        if (!plain.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var hash = AuthService.Hash(plain);
        var t = await db.ApiTokens.FirstOrDefaultAsync(x => x.TokenHash == hash);
        if (t == null || t.RevokedAt != null || (t.ExpiresAt != null && t.ExpiresAt < DateTime.Now)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == t.OwnerUserId);
        if (user == null || user.Status != UserStatus.Active) return null;
        if (t.LastUsedAt == null || DateTime.Now - t.LastUsedAt > TimeSpan.FromMinutes(1)) { t.LastUsedAt = DateTime.Now; t.LastUsedIp = ip; await db.SaveChangesAsync(); }
        return new Actor
        {
            UserId = user.Id, Username = user.Username, IsPlatformAdmin = user.Role == PlatformRole.Admin && !user.IsServiceAccount, Via = "token", TokenId = t.Id, Ip = ip,
            Scopes = JsonSerializer.Deserialize<string[]>(t.ScopesJson)!.ToHashSet(),
            AllowedPools = JsonSerializer.Deserialize<string[]>(t.AllowedPoolsJson)!.ToHashSet(),
            AllowedWorkspaces = JsonSerializer.Deserialize<string[]>(t.AllowedWorkspacesJson)!.ToHashSet()
        };
    }
}
