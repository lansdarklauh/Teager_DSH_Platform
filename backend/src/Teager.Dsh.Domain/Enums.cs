namespace Teager.Dsh.Domain;

/// <summary>平台级角色。</summary>
public enum PlatformRole
{
    /// <summary>普通用户，只能访问被授权的 Pool / Workspace。</summary>
    User = 0,
    /// <summary>平台管理员，可管理所有资源。</summary>
    Admin = 1
}

/// <summary>用户状态。</summary>
public enum UserStatus
{
    /// <summary>正常。</summary>
    Active = 0,
    /// <summary>已禁用，无法登录，Token 失效。</summary>
    Disabled = 1
}

/// <summary>Pool 内角色（权限从高到低：Owner &gt; Admin &gt; Member &gt; Viewer）。</summary>
public enum PoolRole
{
    /// <summary>只读成员。</summary>
    Viewer = 0,
    /// <summary>普通成员，可使用资源。</summary>
    Member = 1,
    /// <summary>管理员，可管理成员、Workspace、共享空间等。</summary>
    Admin = 2,
    /// <summary>所有者。</summary>
    Owner = 3
}

/// <summary>Workspace 内角色（Owner &gt; Admin &gt; Member &gt; Viewer）。</summary>
public enum WorkspaceRole
{
    /// <summary>只读：可查看会话与文件，不能发送消息或写文件。</summary>
    Viewer = 0,
    /// <summary>成员：可创建会话、发送消息、读写私有文件。</summary>
    Member = 1,
    /// <summary>管理员：可管理成员、挂载、配置。</summary>
    Admin = 2,
    /// <summary>所有者。</summary>
    Owner = 3
}

/// <summary>授权效果：显式拒绝优先于显式授权。</summary>
public enum GrantEffect
{
    /// <summary>允许。</summary>
    Allow = 0,
    /// <summary>显式拒绝。</summary>
    Deny = 1
}

/// <summary>共享空间权限级别（RO &lt; RW &lt; MANAGE）。</summary>
public enum SharePermission
{
    /// <summary>只读。</summary>
    RO = 1,
    /// <summary>读写。</summary>
    RW = 2,
    /// <summary>管理（含授权管理）。</summary>
    MANAGE = 3
}

/// <summary>授权主体类型。</summary>
public enum GrantSubjectType
{
    /// <summary>Pool。</summary>
    POOL = 0,
    /// <summary>用户。</summary>
    USER = 1
}

/// <summary>Pool 状态。</summary>
public enum PoolStatus
{
    /// <summary>正常。</summary>
    Active = 0,
    /// <summary>已归档。</summary>
    Archived = 1
}

/// <summary>Workspace 状态。</summary>
public enum WorkspaceStatus
{
    /// <summary>正常。</summary>
    Active = 0,
    /// <summary>已归档（保留期内可恢复）。</summary>
    Archived = 1
}

/// <summary>共享空间状态。</summary>
public enum SharedSpaceStatus
{
    /// <summary>正常。</summary>
    Active = 0,
    /// <summary>已归档。</summary>
    Archived = 1
}

/// <summary>会话状态。</summary>
public enum SessionStatus
{
    /// <summary>活跃。</summary>
    Active = 0,
    /// <summary>已归档。</summary>
    Archived = 1
}

/// <summary>一次 Agent 运行的状态。</summary>
public enum RunStatus
{
    /// <summary>运行中。</summary>
    Running = 0,
    /// <summary>已完成。</summary>
    Completed = 1,
    /// <summary>失败。</summary>
    Failed = 2,
    /// <summary>已取消。</summary>
    Cancelled = 3
}

/// <summary>Runtime 生命周期状态。</summary>
public enum RuntimeStatus
{
    /// <summary>已停止。</summary>
    STOPPED = 0,
    /// <summary>启动中。</summary>
    STARTING = 1,
    /// <summary>运行中（有活动任务）。</summary>
    RUNNING = 2,
    /// <summary>空闲（已就绪但暂无任务）。</summary>
    IDLE = 3,
    /// <summary>停止中。</summary>
    STOPPING = 4,
    /// <summary>错误。</summary>
    ERROR = 5,
    /// <summary>维护中（拒绝新任务）。</summary>
    MAINTENANCE = 6
}

/// <summary>Token 类型。</summary>
public enum TokenType
{
    /// <summary>个人 Token。</summary>
    Personal = 0,
    /// <summary>服务 Token（绑定服务账号）。</summary>
    Service = 1,
    /// <summary>临时 Token（短期有效）。</summary>
    Temporary = 2
}

/// <summary>配置项种类。</summary>
public enum ConfigKind
{
    /// <summary>模型。</summary>
    Model = 0,
    /// <summary>Skill。</summary>
    Skill = 1,
    /// <summary>MCP 服务。</summary>
    Mcp = 2,
    /// <summary>DSH 插件（声明式配置；插件包需预先安装到镜像 / DSH_HOME）。</summary>
    Plugin = 3
}

/// <summary>配置 / 密钥所属层级（自上而下继承）。</summary>
public enum ConfigScope
{
    /// <summary>平台（含组织级）默认。</summary>
    Platform = 0,
    /// <summary>Pool。</summary>
    Pool = 1,
    /// <summary>Workspace。</summary>
    Workspace = 2
}
