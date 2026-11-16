namespace Teager.Dsh.Domain;

/// <summary>组织（第一版仅一个默认组织，为后续多组织预留）。</summary>
public class Organization
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>组织名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>描述。</summary>
    public string? Description { get; set; }
    /// <summary>创建时间（本地时间）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>平台用户（含服务账号）。</summary>
public class User
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>所属组织。</summary>
    public string OrganizationId { get; set; } = "";
    /// <summary>登录名（唯一）。</summary>
    public string Username { get; set; } = "";
    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>邮箱。</summary>
    public string? Email { get; set; }
    /// <summary>密码哈希（BCrypt）；服务账号为空表示不可密码登录。</summary>
    public string? PasswordHash { get; set; }
    /// <summary>平台角色。</summary>
    public PlatformRole Role { get; set; } = PlatformRole.User;
    /// <summary>是否服务账号（仅能通过 Service Token 访问）。</summary>
    public bool IsServiceAccount { get; set; }
    /// <summary>该用户的默认个人工作空间。</summary>
    public string? DefaultWorkspaceId { get; set; }
    /// <summary>状态。</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;
    /// <summary>最近登录时间。</summary>
    public DateTime? LastLoginAt { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Web 登录会话（平台 Session，对应 HttpOnly Cookie）。</summary>
public class PlatformSession
{
    /// <summary>主键：Cookie 令牌的 SHA256 十六进制摘要（库中不存明文）。</summary>
    public string Id { get; set; } = "";
    /// <summary>用户 ID。</summary>
    public string UserId { get; set; } = "";
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>过期时间。</summary>
    public DateTime ExpiresAt { get; set; }
    /// <summary>最近活跃时间。</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.Now;
    /// <summary>登录 IP。</summary>
    public string? Ip { get; set; }
    /// <summary>浏览器 UA。</summary>
    public string? UserAgent { get; set; }
}

/// <summary>Pool：工作空间组（严格单父树）。</summary>
public class Pool
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>所属组织。</summary>
    public string OrganizationId { get; set; } = "";
    /// <summary>父 Pool（null 表示根）。</summary>
    public string? ParentPoolId { get; set; }
    /// <summary>名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>描述。</summary>
    public string? Description { get; set; }
    /// <summary>状态。</summary>
    public PoolStatus Status { get; set; } = PoolStatus.Active;
    /// <summary>层级深度（根为 1），用于限制最大层数。</summary>
    public int Depth { get; set; } = 1;
    /// <summary>Runtime Provider 名称（DeepSeekHarness / Mock）。</summary>
    public string RuntimeProvider { get; set; } = "DeepSeekHarness";
    /// <summary>是否继承父级配置。</summary>
    public bool InheritConfig { get; set; } = true;
    /// <summary>本级策略（JSON，见 PoolPolicy），只能收紧不能扩大。</summary>
    public string? PolicyJson { get; set; }
    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Pool 闭包表：记录任意祖先-后代关系，便于权限快速判断。</summary>
public class PoolClosure
{
    /// <summary>祖先 Pool。</summary>
    public string AncestorPoolId { get; set; } = "";
    /// <summary>后代 Pool。</summary>
    public string DescendantPoolId { get; set; } = "";
    /// <summary>距离（自身为 0）。</summary>
    public int Depth { get; set; }
}

/// <summary>Pool 直接成员（祖先成员关系通过闭包表计算，不重复存储）。</summary>
public class PoolMember
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>Pool ID。</summary>
    public string PoolId { get; set; } = "";
    /// <summary>用户 ID。</summary>
    public string UserId { get; set; } = "";
    /// <summary>Pool 内角色。</summary>
    public PoolRole Role { get; set; } = PoolRole.Member;
    /// <summary>成员类型（固定 DIRECT）。</summary>
    public string MembershipType { get; set; } = "DIRECT";
    /// <summary>是否允许该成员修改 Pool 基本信息。不能据此管理成员、配置或结构。</summary>
    public bool CanEditDetails { get; set; }
    /// <summary>加入时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Workspace：Agent 真正工作的目录与会话边界。</summary>
public class Workspace
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>所属 Pool（仅一个）。</summary>
    public string PoolId { get; set; } = "";
    /// <summary>个人工作空间归属的用户。部门工作空间为空。</summary>
    public string? AssignedUserId { get; set; }
    /// <summary>名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>描述。</summary>
    public string? Description { get; set; }
    /// <summary>状态。</summary>
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Active;
    /// <summary>是否继承 Pool 有效成员的访问权限。</summary>
    public bool InheritPoolMembers { get; set; } = true;
    /// <summary>Workspace 级覆盖配置（JSON，预留给策略收紧等）。</summary>
    public string? OverrideJson { get; set; }
    /// <summary>归档时间。</summary>
    public DateTime? ArchivedAt { get; set; }
    /// <summary>归档保留截止时间，超期后由维护任务永久清理。</summary>
    public DateTime? PurgeAfter { get; set; }
    /// <summary>私有文件占用字节数（缓存值，周期刷新）。</summary>
    public long StorageBytes { get; set; }
    /// <summary>最近活跃时间。</summary>
    public DateTime? LastActiveAt { get; set; }
    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Workspace 显式成员 / 显式拒绝。</summary>
public class WorkspaceMember
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>Workspace ID。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>用户 ID。</summary>
    public string UserId { get; set; } = "";
    /// <summary>角色（Effect 为 Deny 时忽略）。</summary>
    public WorkspaceRole Role { get; set; } = WorkspaceRole.Member;
    /// <summary>授权效果，Deny 表示显式拒绝。</summary>
    public GrantEffect Effect { get; set; } = GrantEffect.Allow;
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>共享空间：独立一级资源，可授权给多个 Pool / 用户。</summary>
public class SharedSpace
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>所属组织。</summary>
    public string OrganizationId { get; set; } = "";
    /// <summary>所属（拥有）Pool，可为空（组织级）。</summary>
    public string? OwnerPoolId { get; set; }
    /// <summary>名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>描述。</summary>
    public string? Description { get; set; }
    /// <summary>默认挂载名（Agent 侧目录名）。</summary>
    public string MountName { get; set; } = "";
    /// <summary>配额（MB），0 表示不限。</summary>
    public int QuotaMb { get; set; }
    /// <summary>状态。</summary>
    public SharedSpaceStatus Status { get; set; } = SharedSpaceStatus.Active;
    /// <summary>占用字节数（缓存值）。</summary>
    public long StorageBytes { get; set; }
    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>共享空间授权。</summary>
public class SharedSpaceGrant
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>共享空间 ID。</summary>
    public string SharedSpaceId { get; set; } = "";
    /// <summary>主体类型（POOL / USER）。</summary>
    public GrantSubjectType SubjectType { get; set; } = GrantSubjectType.POOL;
    /// <summary>主体 ID（Pool ID 或用户 ID）。</summary>
    public string SubjectId { get; set; } = "";
    /// <summary>权限级别（RO / RW / MANAGE）。</summary>
    public SharePermission Permission { get; set; } = SharePermission.RO;
    /// <summary>授权效果（Deny 为显式拒绝）。</summary>
    public GrantEffect Effect { get; set; } = GrantEffect.Allow;
    /// <summary>是否继承给子 Pool（仅对 POOL 主体有意义）。</summary>
    public bool InheritToChildren { get; set; } = true;
    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Workspace 对共享空间的实际挂载：权限 ≠ 挂载。</summary>
public class WorkspaceSharedSpaceMount
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>Workspace ID。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>共享空间 ID。</summary>
    public string SharedSpaceId { get; set; } = "";
    /// <summary>挂载名（/workspace/shared/{MountName}）。</summary>
    public string MountName { get; set; } = "";
    /// <summary>是否启用挂载。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>权限覆盖，只能降低（例如 RW 降为 RO），不能扩大。</summary>
    public SharePermission? PermissionOverride { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>会话：Workspace 下持续存在的一次 Agent 对话上下文。</summary>
public class Session
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>Workspace ID。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>冗余的 Pool ID，便于统计与检索。</summary>
    public string PoolId { get; set; } = "";
    /// <summary>标题。</summary>
    public string Title { get; set; } = "新会话";
    /// <summary>状态。</summary>
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    /// <summary>选择的模型键（为空使用默认）。</summary>
    public string? ModelKey { get; set; }
    /// <summary>Runtime 内部会话 ID（如 DSH 的 session-xxx），对外隐藏。</summary>
    public string? RuntimeSessionId { get; set; }
    /// <summary>事件序号计数器（事件详情存放在文件日志中）。</summary>
    public long LastEventSeq { get; set; }
    /// <summary>消息条数。</summary>
    public int MessageCount { get; set; }
    /// <summary>创建人。</summary>
    public string CreatedBy { get; set; } = "";
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    /// <summary>最近消息时间。</summary>
    public DateTime? LastMessageAt { get; set; }
}

/// <summary>会话消息（仅存文本摘要；思考 / 工具调用等详细事件写入文件日志）。</summary>
public class SessionMessage
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }
    /// <summary>会话 ID。</summary>
    public string SessionId { get; set; } = "";
    /// <summary>角色：user / assistant / system。</summary>
    public string Role { get; set; } = "user";
    /// <summary>消息正文（超长会被截断，完整内容见事件日志）。</summary>
    public string Content { get; set; } = "";
    /// <summary>关联的运行 ID。</summary>
    public string? RunId { get; set; }
    /// <summary>本条消息引用的项目（JSON）。没有引用时为空。</summary>
    public string? ProjectReferencesJson { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>一次 Agent 运行记录（用于用量统计、事件日志定位）。</summary>
public class AgentRun
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>会话 ID。</summary>
    public string SessionId { get; set; } = "";
    /// <summary>Workspace ID。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>Pool ID。</summary>
    public string PoolId { get; set; } = "";
    /// <summary>发起用户。</summary>
    public string UserId { get; set; } = "";
    /// <summary>状态。</summary>
    public RunStatus Status { get; set; } = RunStatus.Running;
    /// <summary>使用的模型键。</summary>
    public string? Model { get; set; }
    /// <summary>开始时间。</summary>
    public DateTime StartedAt { get; set; } = DateTime.Now;
    /// <summary>结束时间。</summary>
    public DateTime? FinishedAt { get; set; }
    /// <summary>输入 token。</summary>
    public long InputTokens { get; set; }
    /// <summary>输出 token。</summary>
    public long OutputTokens { get; set; }
    /// <summary>工具调用次数。</summary>
    public int ToolCalls { get; set; }
    /// <summary>失败原因。</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>调用方幂等请求号。同一用户重复提交时用于找回同一次运行。</summary>
    public string? RequestId { get; set; }
    /// <summary>请求内容摘要，用于确认幂等请求没有被改写。</summary>
    public string? RequestHash { get; set; }
    /// <summary>本次运行预留的模型额度。</summary>
    public long ReservedTokens { get; set; }
}

/// <summary>Runtime 实例：每个 Pool 一个逻辑独立的运行环境（产品方案中的 Runtime 表）。</summary>
public class RuntimeInstance
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>Pool ID（一对一）。</summary>
    public string PoolId { get; set; } = "";
    /// <summary>Provider 名称。</summary>
    public string Provider { get; set; } = "DeepSeekHarness";
    /// <summary>运行节点。</summary>
    public string NodeId { get; set; } = "local";
    /// <summary>状态。</summary>
    public RuntimeStatus Status { get; set; } = RuntimeStatus.STOPPED;
    /// <summary>内部端点（仅内部可见，不对外暴露）。</summary>
    public string? InternalEndpoint { get; set; }
    /// <summary>Runtime 版本。</summary>
    public string? Version { get; set; }
    /// <summary>启动时间。</summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>最近活跃时间。</summary>
    public DateTime? LastActiveAt { get; set; }
    /// <summary>最近一次错误信息。</summary>
    public string? LastError { get; set; }
}

/// <summary>运行节点（第一版仅本机节点，为多节点调度预留）。</summary>
public class RuntimeNode
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>主机描述。</summary>
    public string? Host { get; set; }
    /// <summary>状态：Online / Offline / Draining。</summary>
    public string Status { get; set; } = "Online";
    /// <summary>可承载的 Runtime 数量上限。</summary>
    public int MaxRuntimes { get; set; } = 100;
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>API Token（库中仅存哈希）。</summary>
public class ApiToken
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>所属用户（服务 Token 为服务账号）。</summary>
    public string OwnerUserId { get; set; } = "";
    /// <summary>名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>类型。</summary>
    public TokenType Type { get; set; } = TokenType.Personal;
    /// <summary>Token SHA256 摘要。</summary>
    public string TokenHash { get; set; } = "";
    /// <summary>Token 前缀（仅用于展示识别）。</summary>
    public string TokenPrefix { get; set; } = "";
    /// <summary>Scope 列表（JSON 数组）。</summary>
    public string ScopesJson { get; set; } = "[]";
    /// <summary>允许的 Pool（JSON 数组，空表示不限）。</summary>
    public string AllowedPoolsJson { get; set; } = "[]";
    /// <summary>允许的 Workspace（JSON 数组，空表示不限）。</summary>
    public string AllowedWorkspacesJson { get; set; } = "[]";
    /// <summary>允许的共享空间（JSON 数组，空表示不限）。</summary>
    public string AllowedSharedSpacesJson { get; set; } = "[]";
    /// <summary>过期时间（空表示永不过期）。</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>吊销时间。</summary>
    public DateTime? RevokedAt { get; set; }
    /// <summary>最近使用时间。</summary>
    public DateTime? LastUsedAt { get; set; }
    /// <summary>最近使用 IP。</summary>
    public string? LastUsedIp { get; set; }
    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>配置项：模型 / Skill / MCP，按层级继承与覆盖。</summary>
public class ConfigItem
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>种类。</summary>
    public ConfigKind Kind { get; set; }
    /// <summary>所属层级。</summary>
    public ConfigScope Scope { get; set; }
    /// <summary>层级对象 ID（Platform 为空串，Pool / Workspace 为对应 ID）。</summary>
    public string ScopeId { get; set; } = "";
    /// <summary>配置键（模型 ID / Skill 名 / MCP serverName），同键在下级覆盖上级。</summary>
    public string Key { get; set; } = "";
    /// <summary>显示名。</summary>
    public string? DisplayName { get; set; }
    /// <summary>是否启用；下级将上级项设为 false 即“收紧”。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>参数（JSON）：模型 provider/baseUrl/apiKeyEnv/isDefault，MCP 的 transport/command/url 等。</summary>
    public string? ParamsJson { get; set; }
    /// <summary>正文（Skill 的 SKILL.md 内容）。</summary>
    public string? Content { get; set; }
    /// <summary>更新人。</summary>
    public string? UpdatedBy { get; set; }
    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>密钥：AES-GCM 加密存储，仅在启动 Runtime 时注入为环境变量，不写入 Workspace。</summary>
public class Secret
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>层级（Platform 即组织级 / Pool / Workspace）。</summary>
    public ConfigScope Scope { get; set; }
    /// <summary>层级对象 ID。</summary>
    public string ScopeId { get; set; } = "";
    /// <summary>名称（同时作为注入的环境变量名）。</summary>
    public string Name { get; set; } = "";
    /// <summary>加密后的值（Base64）。</summary>
    public string ValueEncrypted { get; set; } = "";
    /// <summary>描述。</summary>
    public string? Description { get; set; }
    /// <summary>更新人。</summary>
    public string? UpdatedBy { get; set; }
    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>用量记录（每次 Agent 运行一条汇总，不含逐条事件）。</summary>
public class UsageLog
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }
    /// <summary>发生时间。</summary>
    public DateTime Time { get; set; } = DateTime.Now;
    /// <summary>用户。</summary>
    public string UserId { get; set; } = "";
    /// <summary>Pool。</summary>
    public string PoolId { get; set; } = "";
    /// <summary>Workspace。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>会话。</summary>
    public string SessionId { get; set; } = "";
    /// <summary>运行。</summary>
    public string RunId { get; set; } = "";
    /// <summary>模型。</summary>
    public string? Model { get; set; }
    /// <summary>输入 token。</summary>
    public long InputTokens { get; set; }
    /// <summary>输出 token。</summary>
    public long OutputTokens { get; set; }
    /// <summary>工具调用次数。</summary>
    public int ToolCalls { get; set; }
    /// <summary>耗时（毫秒）。</summary>
    public long DurationMs { get; set; }
    /// <summary>是否成功。</summary>
    public bool Success { get; set; } = true;
}

/// <summary>审计日志。</summary>
public class AuditLog
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }
    /// <summary>发生时间（本地时间）。</summary>
    public DateTime Time { get; set; } = DateTime.Now;
    /// <summary>操作人 ID。</summary>
    public string? ActorUserId { get; set; }
    /// <summary>操作人登录名（冗余，便于用户删除后追溯）。</summary>
    public string? ActorName { get; set; }
    /// <summary>来源 IP。</summary>
    public string? Ip { get; set; }
    /// <summary>访问方式：web / token / system。</summary>
    public string Via { get; set; } = "web";
    /// <summary>操作，如 pool.create、sharedspace.write。</summary>
    public string Action { get; set; } = "";
    /// <summary>资源类型。</summary>
    public string? ResourceType { get; set; }
    /// <summary>资源 ID。</summary>
    public string? ResourceId { get; set; }
    /// <summary>相关 Pool。</summary>
    public string? PoolId { get; set; }
    /// <summary>相关 Workspace。</summary>
    public string? WorkspaceId { get; set; }
    /// <summary>结果：success / denied / failed。</summary>
    public string Result { get; set; } = "success";
    /// <summary>详情（短文本，详细内容不入库）。</summary>
    public string? Detail { get; set; }
}

/// <summary>Workspace 快照（文件打包为 zip，存放在数据卷）。</summary>
public class WorkspaceSnapshot
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = "";
    /// <summary>Workspace ID。</summary>
    public string WorkspaceId { get; set; } = "";
    /// <summary>名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>备注。</summary>
    public string? Description { get; set; }
    /// <summary>相对数据根目录的文件路径。</summary>
    public string FilePath { get; set; } = "";
    /// <summary>大小（字节）。</summary>
    public long SizeBytes { get; set; }
    /// <summary>创建人。</summary>
    public string? CreatedBy { get; set; }
    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
