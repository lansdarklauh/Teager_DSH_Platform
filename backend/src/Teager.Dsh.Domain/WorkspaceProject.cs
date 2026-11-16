namespace Teager.Dsh.Domain;

/// <summary>工作空间中的项目。目录位于该工作空间私有文件下。</summary>
public class WorkspaceProject
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>所属工作空间。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>项目名称。同一工作空间内不可重复。</summary>
    public string Name { get; set; } = "";
    /// <summary>项目说明。</summary>
    public string? Description { get; set; }
    /// <summary>相对私有文件根目录的路径，例如 projects/prj_xxx。</summary>
    public string Directory { get; set; } = "";
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
