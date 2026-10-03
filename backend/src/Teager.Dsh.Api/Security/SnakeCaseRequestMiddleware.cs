using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Teager.Dsh.Api.Security;

/// <summary>
/// 请求体兼容层：产品方案的示例使用 snake_case（如 <c>{"workspace_id": "ws_123"}</c>），而接口约定为 camelCase。
/// 该中间件把 JSON 请求体<b>顶层</b>的 snake_case 键（全小写 + 下划线）改写为 camelCase 后再交给模型绑定，
/// 两种写法都可用；嵌套对象（如 MCP 的 env / headers）保持原样，避免破坏环境变量名等用户数据。
/// </summary>
public class SnakeCaseRequestMiddleware(RequestDelegate next)
{
    /// <summary>处理请求。</summary>
    /// <param name="ctx">HTTP 上下文。</param>
    public async Task InvokeAsync(HttpContext ctx)
    {
        var r = ctx.Request;
        var write = HttpMethods.IsPost(r.Method) || HttpMethods.IsPut(r.Method) || HttpMethods.IsPatch(r.Method);
        if (write && r.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true && r.ContentLength is > 0 and < 5 * 1024 * 1024)
        {
            r.EnableBuffering();
            using var reader = new StreamReader(r.Body, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            try
            {
                if (JsonNode.Parse(text) is JsonObject obj && obj.Any(kv => IsSnake(kv.Key)))
                {
                    var fixedObj = new JsonObject();
                    foreach (var kv in obj.ToList()) { obj.Remove(kv.Key); fixedObj[IsSnake(kv.Key) ? ToCamel(kv.Key) : kv.Key] = kv.Value; }
                    var bytes = Encoding.UTF8.GetBytes(fixedObj.ToJsonString());
                    r.Body = new MemoryStream(bytes); r.ContentLength = bytes.Length;
                }
                else r.Body.Position = 0;
            }
            catch (JsonException) { r.Body.Position = 0; } // 非法 JSON 交给框架返回 400
        }
        await next(ctx);
    }

    /// <summary>是否为 snake_case 键：只含小写字母 / 数字 / 下划线且至少含一个下划线。</summary>
    /// <param name="k">键名。</param>
    private static bool IsSnake(string k) => k.Contains('_') && k.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    /// <summary>snake_case 转 camelCase。</summary>
    /// <param name="k">键名。</param>
    private static string ToCamel(string k)
    {
        var parts = k.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
