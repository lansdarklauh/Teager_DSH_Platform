using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Teager.Dsh.Domain;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Services;

namespace Teager.Dsh.Infrastructure.Runtime;

public record IsolatedAgentSpec(string BaseUrl, string ApiKey, string Model, string Prompt, string? SessionId, TimeSpan Timeout, string Patch)
{
    public long Budget { get; init; }
    public string? CatalogProvider { get; init; }
    public IReadOnlyDictionary<string, string> Skills { get; init; } = new Dictionary<string, string>();
}

public partial class DockerSandbox
{
    public async IAsyncEnumerable<JsonObject> SendAgentAsync(RunRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        RequireEnabled();
        await using var scope = scopes.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<ConfigService>().ResolveAsync(request.PoolId, request.WorkspaceId);
        var models = items.Where(c => c.Kind == ConfigKind.Model && c.Enabled).ToList();
        var selected = request.Env.GetValueOrDefault("DSH_SELECTED_MODEL");
        if (!string.IsNullOrEmpty(selected) && models.All(m => m.Key != selected)) throw ApiException.Conflict("所选模型已不可用，请重新选择模型");
        var model = string.IsNullOrEmpty(request.ExecutionActor?.UserId) ? null : models.FirstOrDefault(m => m.Key == request.Env.GetValueOrDefault("DSH_SELECTED_MODEL"))
            ?? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault();
        if (model == null) throw ApiException.Conflict("管理员尚未为个人环境配置可用模型");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(model.ParamsJson ?? "{}") ?? [];
        string? Value(string key) => parameters.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var upstream = Value("baseUrl") ?? "https://api.deepseek.com/v1";
        var keyName = Value("apiKeyEnv") ?? "DEEPSEEK_API_KEY";
        var secret = await scope.ServiceProvider.GetRequiredService<SecretService>().RequireModelKeyAsync(request.PoolId, request.WorkspaceId, keyName, request.Env);
        var modelName = Value("model") ?? model.Key;
        var limits = ModelExecutionLimits.FromJson(model.ParamsJson);
        var modelProfile = new Dictionary<string, object?> { ["id"] = modelName, ["name"] = model.DisplayName ?? model.Key };
        if (limits.ContextWindow > 0) modelProfile["contextWindow"] = limits.ContextWindow;
        var patch = JsonSerializer.Serialize(new object[]
        {
            new { id = "agent-default-model", name = "@deepseek-ai/dsh-agent-default-model", config = new { provider = "isolated-model", model = modelName } },
            new { id = "llm-pi-ai", name = "@deepseek-ai/dsh-llm-pi-ai", config = new { providers = new Dictionary<string, object> { ["isolated-model"] = new { displayName = model.DisplayName, apiKeyEnv = "DSH_ISOLATED_MODEL_KEY", api = "openai-completions", baseURL = "http://127.0.0.1:8765/v1", models = new[] { modelProfile } } } } }
        });
        var spec = new IsolatedAgentSpec(upstream, secret, modelName, request.Prompt, request.RuntimeSessionId, request.Timeout, patch) { Budget = request.ModelBudget, CatalogProvider = Value("provider"), Skills = items.Where(i => i.Kind == ConfigKind.Skill && i.Enabled).ToDictionary(i => i.Key, i => i.Content ?? "") };
        var args = new List<string> { "dsh", "headless", "--patch", "/workspace/.dsh/agent.json", "--json" };
        if (request.RuntimeSessionId != null) { args.Add("--session-id"); args.Add(request.RuntimeSessionId); }
        args.Add("-");
        var execution = await StartAsync(request.ExecutionActor!, request.WorkspaceId, args.ToArray(), agent: spec, ct: ct);
        var info = new System.Diagnostics.ProcessStartInfo(Settings.DockerExecutable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "logs", "--follow", execution.ContainerName }) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("无法订阅隔离任务");
        using var kill = ct.Register(() => ProcessUtil.KillTree(process));
        var errors = process.StandardError.ReadToEndAsync(ct);
        var reportedError = false;
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
            {
                JsonObject? item = null;
                try { item = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
                if (item != null)
                {
                    if (item["type"]?.GetValue<string>() == "error" || item["reason"]?["kind"]?.GetValue<string>() == "error") reportedError = true;
                    NormalizeModelLimitError(item);
                    yield return item;
                }
            }
            await process.WaitForExitAsync(ct);
            var status = await CliAsync(["inspect", "--format", "{{.State.ExitCode}}", execution.ContainerName], ct);
            if (status.Trim() != "0" && !reportedError) yield return new JsonObject { ["type"] = "error", ["message"] = "隔离 Agent 执行失败；请检查模型配置和运行日志" };
        }
        finally
        {
            ProcessUtil.KillTree(process);
            await StopAsync(execution.Id);
            try { await errors; } catch (OperationCanceledException) { }
        }
    }

    private static void NormalizeModelLimitError(JsonObject item)
    {
        var error = item["type"]?.GetValue<string>() == "error" ? item : item["reason"]?["error"] as JsonObject;
        if (error?["message"] is not JsonValue message || !message.TryGetValue<string>(out var value)) return;
        if (value.Contains("execution_model_budget_exceeded", StringComparison.Ordinal))
            error["message"] = "本次任务的模型预算不足，请管理员在模型配置中提高单次任务预算，或设为 0 使用原生设置";
        else if (value.Contains("execution_model_call_limit", StringComparison.Ordinal))
            error["message"] = "本次任务已达到平台模型调用次数上限（100 次），请拆分任务";
    }
}
