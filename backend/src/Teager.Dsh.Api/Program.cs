using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Serilog;
using Teager.Dsh.Api.Security;
using Teager.Dsh.Infrastructure;
using Teager.Dsh.Infrastructure.Common;
using Teager.Dsh.Infrastructure.Data;
using Teager.Dsh.Infrastructure.Hosting;
using Teager.Dsh.Infrastructure.Options;

var builder = WebApplication.CreateBuilder(args);

// ── 日志：控制台 + 按天滚动文件（日志目录可独立挂载卷，默认保留 14 天）──
var logDir = Path.GetFullPath(builder.Configuration["Dsh:Storage:LogDir"] ?? "./.logs");
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "app", "dsh-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

builder.Services.AddDshInfrastructure(builder.Configuration);
var dsh = builder.Configuration.GetSection(DshOptions.SectionName).Get<DshOptions>() ?? new DshOptions();

builder.Services.AddControllers(o => o.Filters.Add(new AuthorizeFilter()))
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    });
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = (long)dsh.Platform.MaxUploadMb * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = (long)dsh.Platform.MaxUploadMb * 1024 * 1024 + 1024 * 1024);

builder.Services.AddAuthentication(PlatformAuth.Scheme).AddScheme<AuthenticationSchemeOptions, PlatformAuthHandler>(PlatformAuth.Scheme, null);
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); // 部署在反向代理（nginx / Teager_DevOps 网关）之后，信任其转发头
});

// ── 限流：按 Token / 用户 / IP 分区；登录接口按 IP 单独限制 ──
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (ctx, ct) => { ctx.HttpContext.Response.ContentType = "application/json"; await ctx.HttpContext.Response.WriteAsync("{\"code\":\"rate_limited\",\"message\":\"请求过于频繁，请稍后再试\"}", ct); };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        if (dsh.RateLimit.PermitPerMinute <= 0 || ctx.Request.Path.StartsWithSegments("/health")) return RateLimitPartition.GetNoLimiter("none");
        var key = ctx.Request.Headers.Authorization.ToString() is { Length: > 12 } a ? "t:" + a[^12..] : ctx.Request.Cookies.TryGetValue(PlatformAuth.CookieName, out var c) ? "c:" + c[..Math.Min(12, c.Length)] : "ip:" + ctx.Connection.RemoteIpAddress;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = dsh.RateLimit.PermitPerMinute, Window = TimeSpan.FromMinutes(1) });
    });
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter("login:" + ctx.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, dsh.RateLimit.LoginPerMinute), Window = TimeSpan.FromMinutes(1) }));
});

if (dsh.Platform.CorsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(dsh.Platform.CorsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
Log.Information("DSH 平台启动：数据目录 {Data}，日志目录 {Logs}，缓存目录 {Cache}", Path.GetFullPath(dsh.Storage.DataDir), logDir, Path.GetFullPath(dsh.Storage.CacheDir));

await DbInitializer.InitializeAsync(app.Services);

app.UseForwardedHeaders();
app.UseMiddleware<GatewayMiddleware>();
if (dsh.Platform.CorsOrigins.Length > 0) app.UseCors();
app.UseMiddleware<SnakeCaseRequestMiddleware>();
app.UseWebSockets();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

// 健康检查：live 不依赖外部组件；ready 校验数据库连通
app.MapGet("/health/live", () => Results.Ok(new { status = "live", time = DateTime.Now })).AllowAnonymous();
app.MapGet("/health/ready", async (IServiceProvider sp) =>
{
    await using var scope = sp.CreateAsyncScope();
    var ok = await scope.ServiceProvider.GetRequiredService<DshDbContext>().Database.CanConnectAsync();
    return ok ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "database_unavailable" }, statusCode: 503);
}).AllowAnonymous();

app.Run();

/// <summary>为集成测试暴露入口类型。</summary>
public partial class Program;
