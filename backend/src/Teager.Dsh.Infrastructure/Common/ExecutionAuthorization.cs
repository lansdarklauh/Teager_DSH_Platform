using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Data;

namespace Teager.Dsh.Infrastructure.Common;

/// <summary>Background work must re-read identity rather than trust a queued HTTP snapshot.</summary>
public static class ExecutionAuthorization
{
    public static async Task<Actor> RefreshAsync(DshDbContext db, Actor original, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == original.UserId && u.Status == UserStatus.Active, ct)
            ?? throw ApiException.Forbidden("用户已停用，任务不能继续执行");
        ApiToken? token = null;
        if (original.TokenId != null)
        {
            token = await db.ApiTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == original.TokenId && t.OwnerUserId == user.Id, ct);
            if (token == null || token.RevokedAt != null || token.ExpiresAt <= DateTime.Now) throw ApiException.Forbidden("Token 已失效，任务不能继续执行");
        }
        HashSet<string> Set(string json) => (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet();
        return new Actor { UserId = user.Id, Username = user.Username, IsPlatformAdmin = user.Role == PlatformRole.Admin && !user.IsServiceAccount, IsServiceAccount = user.IsServiceAccount,
            Via = original.Via, TokenId = token?.Id, Ip = original.Ip, Scopes = token == null ? null : Set(token.ScopesJson),
            AllowedPools = token == null ? [] : Set(token.AllowedPoolsJson), AllowedWorkspaces = token == null ? [] : Set(token.AllowedWorkspacesJson),
            AllowedSharedSpaces = token == null ? [] : Set(token.AllowedSharedSpacesJson) };
    }
}
