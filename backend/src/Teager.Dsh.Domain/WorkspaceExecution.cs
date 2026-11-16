namespace Teager.Dsh.Domain;

/// <summary>工作空间中一次隔离执行任务的记录。</summary>
public class WorkspaceExecution
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>所属工作空间。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>发起用户。</summary>
    public string UserId { get; set; } = "";
    /// <summary>发起所用的 API Token。网页发起时为空。</summary>
    public string? TokenId { get; set; }
    /// <summary>容器名称。</summary>
    public string ContainerName { get; set; } = "";
    /// <summary>任务类型，例如 command。</summary>
    public string Kind { get; set; } = "command";
    /// <summary>运行状态。</summary>
    public string Status { get; set; } = "Running";
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>过期时间。到期后由维护任务回收。</summary>
    public DateTime ExpiresAt { get; set; }
    /// <summary>进程退出码。尚未结束时为空。</summary>
    public int? ExitCode { get; set; }
    /// <summary>映射端口（JSON）。</summary>
    public string PortsJson { get; set; } = "[]";
}
