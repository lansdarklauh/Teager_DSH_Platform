using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Options;
using Teager.Dsh.Infrastructure.Permissions;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>密钥服务：AES-GCM 加密存储；只写不读（接口永不返回明文）；仅在 Runtime 启动时按 Workspace &gt; Pool &gt; 组织 的优先级注入环境变量。</summary>
public partial class SecretService(DshDbContext db, IOptions<DshOptions> opt, ConfigAccess access)
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex NameRegex();

    /// <summary>由主密钥派生 AES-256 密钥。</summary>
    private byte[] Key() => SHA256.HashData(Encoding.UTF8.GetBytes(opt.Value.Security.MasterKey));

    /// <summary>加密明文，输出 Base64(nonce | tag | cipher)。</summary>
    /// <param name="plain">明文。</param>
    public string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    /// <summary>解密 Base64(nonce | tag | cipher)。</summary>
    /// <param name="b64">密文。</param>
    public string Decrypt(string b64)
    {
        var all = Convert.FromBase64String(b64);
        var nonce = all[..12]; var tag = all[12..28]; var cipher = all[28..];
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(Key(), 16)) aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>列出某层级的密钥（仅名称 / 描述 / 更新时间，永不含值）。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">层级对象 ID。</param>
    public async Task<List<object>> ListAsync(Actor actor, ConfigScope scope, string scopeId)
    {
        await access.RequireManageAsync(actor, scope, scopeId);
        scopeId = Norm(scope, scopeId);
        var rows = await db.Secrets.AsNoTracking().Where(s => s.Scope == scope && s.ScopeId == scopeId).OrderBy(s => s.Name).ToListAsync();
        return rows.Select(s => (object)new { s.Id, s.Name, s.Description, Scope = s.Scope.ToString(), s.ScopeId, s.UpdatedBy, s.UpdatedAt, HasValue = true }).ToList();
    }

    /// <summary>新增 / 覆盖密钥。名称即注入的环境变量名。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">层级对象 ID。</param>
    /// <param name="name">名称（环境变量名）。</param>
    /// <param name="value">明文值。</param>
    /// <param name="description">描述。</param>
    public async Task UpsertAsync(Actor actor, ConfigScope scope, string scopeId, string name, string value, string? description)
    {
        await access.RequireManageAsync(actor, scope, scopeId);
        scopeId = Norm(scope, scopeId);
        name = name?.Trim() ?? "";
        if (!NameRegex().IsMatch(name)) throw ApiException.BadRequest("密钥名称必须是合法的环境变量名（字母/数字/下划线，不以数字开头）");
        if (name.StartsWith("DSH_", StringComparison.OrdinalIgnoreCase) && !actor.IsPlatformAdmin) throw ApiException.Forbidden("DSH_ 前缀为平台保留，仅平台管理员可设置");
        if (string.IsNullOrEmpty(value)) throw ApiException.BadRequest("密钥值不能为空");
        var s = await db.Secrets.FirstOrDefaultAsync(x => x.Scope == scope && x.ScopeId == scopeId && x.Name == name);
        if (s == null) { s = new Secret { Id = IdGen.New("sec"), Scope = scope, ScopeId = scopeId, Name = name }; db.Secrets.Add(s); }
        s.ValueEncrypted = Encrypt(value); s.Description = description; s.UpdatedBy = actor.Username; s.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
    }

    /// <summary>删除密钥。</summary>
    /// <param name="actor">操作者。</param>
    /// <param name="id">密钥 ID。</param>
    /// <returns>被删除的密钥（用于审计）。</returns>
    public async Task<Secret> DeleteAsync(Actor actor, string id)
    {
        var s = await db.Secrets.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("密钥不存在");
        await access.RequireManageAsync(actor, s.Scope, s.ScopeId);
        db.Secrets.Remove(s);
        await db.SaveChangesAsync();
        return s;
    }

    /// <summary>为某 Workspace 计算要注入 Runtime 的环境变量：平台默认 &lt; 组织 &lt; Pool &lt; Workspace。仅 Runtime 内部使用。</summary>
    /// <param name="poolId">Pool ID。</param>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <returns>环境变量字典。</returns>
    public async Task<Dictionary<string, string>> ResolveEnvAsync(string poolId, string workspaceId)
    {
        var env = new Dictionary<string, string>(opt.Value.Runtime.ExtraEnv);
        var rows = await db.Secrets.AsNoTracking().Where(s =>
            (s.Scope == ConfigScope.Platform && s.ScopeId == "") || (s.Scope == ConfigScope.Pool && s.ScopeId == poolId) || (s.Scope == ConfigScope.Workspace && s.ScopeId == workspaceId)).ToListAsync();
        foreach (var s in rows.OrderBy(r => r.Scope)) // Platform → Pool → Workspace，后者覆盖前者
        {
            try { env[s.Name] = Decrypt(s.ValueEncrypted); } catch (CryptographicException) { /* 主密钥变更后旧密文无法解密，跳过 */ }
        }
        return env;
    }

    /// <summary>规范化层级 ID：平台层固定为空串。</summary>
    /// <param name="scope">层级。</param>
    /// <param name="scopeId">层级对象 ID。</param>
    private static string Norm(ConfigScope scope, string scopeId) => scope == ConfigScope.Platform ? "" : scopeId;
}
