using System.Text.Json.Nodes;
using Teager.Dsh.Infrastructure.Storage;

namespace Teager.Dsh.Infrastructure.Runtime;

/// <summary>Runtime 描述：一个 Pool 对应一个逻辑独立的运行环境。</summary>
/// <param name="PoolId">Pool ID。</param>
/// <param name="DshHome">该 Pool 独立的 DSH_HOME 目录。</param>
public record RuntimeSpec(string PoolId, string DshHome);

/// <summary>Runtime 健康检查结果。</summary>
/// <param name="Healthy">是否健康。</param>
/// <param name="Version">Runtime 版本。</param>
/// <param name="Message">说明。</param>
/// <param name="ActiveRuns">正在运行的任务数。</param>
public record RuntimeHealth(bool Healthy, string? Version, string? Message, int ActiveRuns);

/// <summary>一次消息发送（Agent 运行）请求。</summary>
/// <param name="RunId">运行 ID。</param>
/// <param name="PoolId">Pool ID。</param>
/// <param name="WorkspaceId">Workspace ID。</param>
/// <param name="WorkspaceRoot">Workspace 根目录（Agent 当前工作目录，含 files/、shared/）。</param>
/// <param name="DshHome">DSH_HOME。</param>
/// <param name="RuntimeSessionId">Runtime 内部会话 ID，首次为空。</param>
/// <param name="Prompt">用户输入。</param>
/// <param name="Env">要注入的环境变量（含密钥与策略）。</param>
/// <param name="PatchFile">DSH 配置覆盖文件路径（模型 / MCP）。</param>
/// <param name="Timeout">最长运行时间。</param>
public record RunRequest(string RunId, string PoolId, string WorkspaceId, string WorkspaceRoot, string DshHome, string? RuntimeSessionId, string Prompt,
    IReadOnlyDictionary<string, string> Env, string? PatchFile, TimeSpan Timeout)
{
    public Teager.Dsh.Infrastructure.Common.Actor? ExecutionActor { get; init; }
    public IReadOnlyList<ActiveMount> Mounts { get; init; } = [];
    public long ModelBudget { get; init; }
}

/// <summary>终端打开请求。</summary>
/// <param name="WorkspaceRoot">工作目录。</param>
/// <param name="Env">环境变量（已清洗）。</param>
public record TerminalRequest(string WorkspaceRoot, IReadOnlyDictionary<string, string> Env);

/// <summary>交互终端会话。</summary>
public interface ITerminalSession : IAsyncDisposable
{
    /// <summary>终端输出流（stdout + stderr 合并）。</summary>
    IAsyncEnumerable<string> ReadOutputAsync(CancellationToken ct);
    /// <summary>写入一行输入。</summary>
    /// <param name="text">输入内容。</param>
    Task WriteAsync(string text);
}

/// <summary>
/// Runtime Provider：把不同 Agent Runtime（DSH / 未来的 Codex、Claude Code 等）适配为统一接口。
/// 平台其余部分不依赖任何 Runtime 的原生 API。
/// </summary>
public interface IRuntimeProvider
{
    /// <summary>Provider 名称（与 Pool.RuntimeProvider 对应）。</summary>
    string Name { get; }
    /// <summary>启动（准备）Runtime：初始化目录、校验可执行文件，返回健康状态。</summary>
    /// <param name="spec">Runtime 描述。</param>
    /// <param name="ct">取消令牌。</param>
    Task<RuntimeHealth> StartRuntimeAsync(RuntimeSpec spec, CancellationToken ct);
    /// <summary>停止 Runtime：终止该 Pool 下所有运行中的进程。</summary>
    /// <param name="poolId">Pool ID。</param>
    Task StopRuntimeAsync(string poolId);
    /// <summary>重启 Runtime。</summary>
    /// <param name="spec">Runtime 描述。</param>
    /// <param name="ct">取消令牌。</param>
    Task<RuntimeHealth> RestartRuntimeAsync(RuntimeSpec spec, CancellationToken ct);
    /// <summary>健康检查。</summary>
    /// <param name="spec">Runtime 描述。</param>
    /// <param name="ct">取消令牌。</param>
    Task<RuntimeHealth> HealthCheckAsync(RuntimeSpec spec, CancellationToken ct);
    /// <summary>创建 Runtime 会话；返回 null 表示在首次发送消息时惰性创建。</summary>
    /// <param name="req">运行请求（不含 Prompt 语义）。</param>
    Task<string?> CreateSessionAsync(RunRequest req);
    /// <summary>恢复 Runtime 会话，返回会话是否仍可继续。</summary>
    /// <param name="spec">Runtime 描述。</param>
    /// <param name="runtimeSessionId">Runtime 会话 ID。</param>
    Task<bool> ResumeSessionAsync(RuntimeSpec spec, string runtimeSessionId);
    /// <summary>发送消息并流式返回事件（即 StreamEvents）。事件为 JSON 对象，必含 type 字段。</summary>
    /// <param name="req">运行请求。</param>
    /// <param name="ct">取消令牌（取消时应终止底层进程）。</param>
    IAsyncEnumerable<JsonObject> SendMessageAsync(RunRequest req, CancellationToken ct);
    /// <summary>列出 Workspace 内文件。</summary>
    /// <param name="workspaceRoot">Workspace 根目录。</param>
    /// <param name="relative">相对路径。</param>
    Task<IReadOnlyList<FileEntry>> GetFilesAsync(string workspaceRoot, string? relative);
    /// <summary>打开交互终端。</summary>
    /// <param name="req">终端请求。</param>
    Task<ITerminalSession> OpenTerminalAsync(TerminalRequest req);
}
