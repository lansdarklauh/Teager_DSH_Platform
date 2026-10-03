using Microsoft.Extensions.Options;
using Teager.Dsh.Infrastructure.Options;

namespace Teager.Dsh.Infrastructure.Storage;

/// <summary>
/// 平台目录布局。数据 / 日志 / 缓存三类目录相互独立，可分别挂载不同的宿主机卷：
/// <code>
/// {DataDir}/workspaces/{wsId}/files      Workspace 私有文件（Agent 的工作目录根下的 files/）
/// {DataDir}/workspaces/{wsId}/shared/*   共享空间挂载点（符号链接 / 目录联接）
/// {DataDir}/shared/{spaceId}             共享空间真实数据
/// {DataDir}/pools/{poolId}/dsh_home      各 Pool 独立的 DSH_HOME
/// {DataDir}/snapshots/{snapshotId}.zip   Workspace 快照
/// {LogDir}/runs/{yyyyMM}/{sessionId}.jsonl  Agent 运行事件日志
/// {CacheDir}/tmp                         上传 / 打包临时文件
/// </code>
/// </summary>
public class StoragePaths
{
    /// <summary>数据根目录（绝对路径）。</summary>
    public string Data { get; }
    /// <summary>日志根目录（绝对路径）。</summary>
    public string Logs { get; }
    /// <summary>缓存根目录（绝对路径）。</summary>
    public string Cache { get; }

    /// <summary>根据配置解析并创建目录。</summary>
    /// <param name="opt">平台配置。</param>
    public StoragePaths(IOptions<DshOptions> opt)
    {
        var s = opt.Value.Storage;
        Data = Path.GetFullPath(s.DataDir);
        Logs = Path.GetFullPath(s.LogDir);
        Cache = Path.GetFullPath(s.CacheDir);
        foreach (var d in new[] { Data, Logs, Cache, Path.Combine(Data, "workspaces"), Path.Combine(Data, "shared"), Path.Combine(Data, "pools"), Path.Combine(Data, "snapshots"), Path.Combine(Logs, "runs"), Path.Combine(Cache, "tmp") })
            Directory.CreateDirectory(d);
    }

    /// <summary>Workspace 根目录（同时作为 Agent 的当前工作目录）。</summary>
    /// <param name="workspaceId">Workspace ID。</param>
    public string WorkspaceRoot(string workspaceId) => Path.Combine(Data, "workspaces", Safe(workspaceId));
    /// <summary>Workspace 私有文件目录。</summary>
    /// <param name="workspaceId">Workspace ID。</param>
    public string WorkspaceFiles(string workspaceId) => Path.Combine(WorkspaceRoot(workspaceId), "files");
    /// <summary>Workspace 共享挂载目录。</summary>
    /// <param name="workspaceId">Workspace ID。</param>
    public string WorkspaceShared(string workspaceId) => Path.Combine(WorkspaceRoot(workspaceId), "shared");
    /// <summary>共享空间真实目录。</summary>
    /// <param name="spaceId">共享空间 ID。</param>
    public string SharedSpace(string spaceId) => Path.Combine(Data, "shared", Safe(spaceId));
    /// <summary>Pool 根目录。</summary>
    /// <param name="poolId">Pool ID。</param>
    public string PoolRoot(string poolId) => Path.Combine(Data, "pools", Safe(poolId));
    /// <summary>Pool 独立的 DSH_HOME。</summary>
    /// <param name="poolId">Pool ID。</param>
    public string PoolDshHome(string poolId) => Path.Combine(PoolRoot(poolId), "dsh_home");
    /// <summary>快照文件绝对路径。</summary>
    /// <param name="snapshotId">快照 ID。</param>
    public string SnapshotFile(string snapshotId) => Path.Combine(Data, "snapshots", Safe(snapshotId) + ".zip");
    /// <summary>运行事件日志文件（按月分目录，一个会话一个文件）。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="createdAt">会话创建时间。</param>
    public string RunEventLog(string sessionId, DateTime createdAt) => Path.Combine(Logs, "runs", createdAt.ToString("yyyyMM"), Safe(sessionId) + ".jsonl");
    /// <summary>临时目录。</summary>
    public string Temp => Path.Combine(Cache, "tmp");

    /// <summary>校验 ID 仅含安全字符，防止路径穿越。</summary>
    /// <param name="id">ID。</param>
    private static string Safe(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-'))) throw new ArgumentException("非法 ID：" + id);
        return id;
    }
}
