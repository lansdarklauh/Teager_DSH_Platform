using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Services;
using Xunit;

namespace Teager.Dsh.Tests;

public class ModelCredentialTests
{
    [Fact]
    public async Task Broken_Override_Does_Not_Fall_Back_And_Model_Error_Explains_Recovery()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("credentials");
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "work", null);
        var secrets = env.Get<SecretService>();
        const string dummy = "dummy-platform-model-key";
        await secrets.UpsertAsync(env.Admin, ConfigScope.Platform, "", "MODEL_KEY", dummy, null);
        await secrets.UpsertAsync(env.Admin, ConfigScope.Platform, "", "OTHER_KEY", dummy, null);
        var corrupted = Convert.FromBase64String(secrets.Encrypt("dummy-workspace-model-key"));
        corrupted[0] ^= 1;
        var row = new Secret { Id = "bad-workspace-key", Scope = ConfigScope.Workspace, ScopeId = ws.Id, Name = "MODEL_KEY", ValueEncrypted = Convert.ToBase64String(corrupted) };
        env.Db.Secrets.Add(row);
        await env.Db.SaveChangesAsync();
        foreach (var invalid in new[] { row.ValueEncrypted, "invalid-base64", Convert.ToBase64String(new byte[4]) })
        {
            row.ValueEncrypted = invalid;
            await env.Db.SaveChangesAsync();
            var resolved = await secrets.ResolveEnvAsync(pool.Id, ws.Id);
            Assert.False(resolved.ContainsKey("MODEL_KEY"));
            Assert.Equal(dummy, await secrets.RequireModelKeyAsync(pool.Id, ws.Id, "OTHER_KEY", resolved));
            var error = await Assert.ThrowsAsync<ApiException>(() => secrets.RequireModelKeyAsync(pool.Id, ws.Id, "MODEL_KEY", resolved));
            Assert.Equal(409, error.Status);
            Assert.Contains("无法解密", error.Message);
            Assert.Contains("重新保存", error.Message);
            Assert.DoesNotContain(dummy, error.Message);
            Assert.DoesNotContain(invalid, error.Message);
        }
        await secrets.UpsertAsync(env.Admin, ConfigScope.Workspace, ws.Id, "MODEL_KEY", "dummy-replacement", null);
        Assert.Equal("dummy-replacement", await secrets.RequireModelKeyAsync(pool.Id, ws.Id, "MODEL_KEY", await secrets.ResolveEnvAsync(pool.Id, ws.Id)));
        var finalEnv = await secrets.ResolveEnvAsync(pool.Id, ws.Id);
        var missing = await Assert.ThrowsAsync<ApiException>(() => secrets.RequireModelKeyAsync(pool.Id, ws.Id, "MISSING", finalEnv));
        Assert.Contains("尚未配置", missing.Message);
    }

    [Fact]
    public async Task Misplaced_Key_Is_Encrypted_At_Same_Scope_And_Migration_Is_Idempotent()
    {
        await using var env = await TestEnv.CreateAsync();
        var pool = await env.PoolAsync("model");
        var ws = await env.Get<WorkspaceService>().CreateAsync(env.Admin, pool.Id, "w", null);
        const string dummyKey = "sk-test-dummy-credential-for-migration";
        env.Db.ConfigItems.Add(new ConfigItem { Id = "old-model", Kind = ConfigKind.Model, Scope = ConfigScope.Platform, Key = "existing-model", ParamsJson = JsonSerializer.Serialize(new { apiKeyEnv = dummyKey, model = "existing-model", isDefault = true }) });
        await env.Db.SaveChangesAsync();
        Assert.Equal(1, await env.Get<ModelCredentialMigration>().NormalizeAsync());
        Assert.Equal(0, await env.Get<ModelCredentialMigration>().NormalizeAsync());
        var model = await env.Db.ConfigItems.SingleAsync();
        Assert.DoesNotContain(dummyKey, model.ParamsJson);
        var name = JsonDocument.Parse(model.ParamsJson!).RootElement.GetProperty("apiKeyEnv").GetString()!;
        var secret = await env.Db.Secrets.SingleAsync();
        Assert.Equal(ConfigScope.Platform, secret.Scope);
        Assert.Equal("", secret.ScopeId);
        Assert.Equal(name, secret.Name);
        Assert.DoesNotContain(dummyKey, secret.ValueEncrypted);
        Assert.Equal(dummyKey, (await env.Get<SecretService>().ResolveEnvAsync(pool.Id, ws.Id))[name]);
        Assert.Contains("existing-model", model.ParamsJson);
    }

    [Fact]
    public async Task Model_Configuration_Rejects_Key_Value_And_Non_String_Name()
    {
        await using var env = await TestEnv.CreateAsync();
        foreach (var invalid in new[] { "{\"apiKeyEnv\":\"sk-test-dummy-key\"}", "{\"apiKeyEnv\":123}" })
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => env.Get<ConfigService>().UpsertAsync(env.Admin, ConfigKind.Model, ConfigScope.Platform, "", new ConfigUpsert("model", null, true, invalid, null)));
            Assert.Equal(400, error.Status);
            Assert.DoesNotContain("sk-test-dummy-key", error.Message);
        }
        Assert.Empty(await env.Db.ConfigItems.ToListAsync());
    }
}
