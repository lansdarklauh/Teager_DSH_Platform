using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>修复旧配置中误填到 apiKeyEnv 的上游密钥，同层级加密保存，不输出明文。</summary>
public class ModelCredentialMigration(DshDbContext db, SecretService secrets)
{
    public async Task<int> NormalizeAsync()
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await DatabaseAdmission.LockAsync(db);
        var changed = 0;
        foreach (var item in await db.ConfigItems.Where(c => c.Kind == ConfigKind.Model).ToListAsync())
        {
            JsonObject? parameters;
            try { parameters = JsonNode.Parse(item.ParamsJson ?? "{}") as JsonObject; }
            catch (System.Text.Json.JsonException) { continue; }
            if (parameters?["apiKeyEnv"] is not JsonValue value || !value.TryGetValue<string>(out var key)
                || string.IsNullOrEmpty(key) || !key.StartsWith("sk-", StringComparison.Ordinal) || key.Length < 20) continue;

            // 新名称不依赖用户输入，也不会覆盖已有密钥。
            var name = "DSH_MODEL_KEY_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
            db.Secrets.Add(new Secret
            {
                Id = IdGen.New("sec"), Scope = item.Scope, ScopeId = item.ScopeId,
                Name = name, ValueEncrypted = secrets.Encrypt(key),
                Description = "从旧模型配置自动迁移的上游密钥", UpdatedBy = "system"
            });
            parameters["apiKeyEnv"] = name;
            item.ParamsJson = parameters.ToJsonString();
            item.UpdatedAt = DateTime.Now;
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return changed;
    }
}
