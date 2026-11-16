using System.Text.Json;
using Teager.Dsh.Infrastructure.Common;

namespace Teager.Dsh.Infrastructure.Services;

/// <summary>管理员配置的模型限制。0 表示不附加平台限制，由模型/引擎决定容量。</summary>
public record ModelExecutionLimits(long TaskBudget = 0, int ContextWindow = 0)
{
    public static ModelExecutionLimits FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        using var document = JsonDocument.Parse(json);
        return FromElement(document.RootElement);
    }

    public static ModelExecutionLimits FromElement(JsonElement root)
    {
        long Read(string name, long maximum)
        {
            if (!root.TryGetProperty(name, out var value)) return 0;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0 || number > maximum)
                throw ApiException.BadRequest($"{name} 必须为非负整数，0 表示使用模型/引擎原生设置");
            return number;
        }
        return new(Read("taskBudget", 9_007_199_254_740_991), (int)Read("contextWindow", int.MaxValue));
    }
}
