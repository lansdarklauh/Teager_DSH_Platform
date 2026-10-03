namespace Teager.Dsh.Infrastructure.Options;

/// <summary>平台配置根节点（配置节名：Dsh，环境变量前缀 Dsh__）。</summary>
public class DshOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Dsh";

    /// <summary>目录配置：数据 / 日志 / 缓存分离存放。</summary>
    public StorageOptions Storage { get; set; } = new();
    /// <summary>平台通用配置。</summary>
    public PlatformOptions Platform { get; set; } = new();
    /// <summary>Runtime 配置。</summary>
    public RuntimeOptions Runtime { get; set; } = new();
    /// <summary>安全配置。</summary>
    public SecurityOptions Security { get; set; } = new();
    /// <summary>初始化数据（首个管理员等）。</summary>
    public SeedOptions Seed { get; set; } = new();
    /// <summary>限流配置。</summary>
    public RateLimitOptions RateLimit { get; set; } = new();
    /// <summary>平台默认策略（Pool 策略只能在其基础上收紧）。</summary>
    public PlatformPolicyOptions Policy { get; set; } = new();
}

/// <summary>目录配置。三类目录建议挂载到不同的宿主机路径，避免撑满 Docker 数据盘。</summary>
public class StorageOptions
{
    /// <summary>持久化数据目录：Workspace 文件、共享空间、各 Pool 的 DSH_HOME、快照。必须挂载数据卷。</summary>
    public string DataDir { get; set; } = "./.data";
    /// <summary>日志目录：应用日志（Serilog 按天滚动）与 Agent 运行事件日志（JSONL）。</summary>
    public string LogDir { get; set; } = "./.logs";
    /// <summary>缓存目录：上传临时文件、打包临时文件等可丢弃内容。</summary>
    public string CacheDir { get; set; } = "./.cache";
}

/// <summary>平台通用配置。</summary>
public class PlatformOptions
{
    /// <summary>Pool 最大层级（默认 5）。</summary>
    public int MaxPoolDepth { get; set; } = 5;
    /// <summary>Web 登录会话有效小时数。</summary>
    public int SessionHours { get; set; } = 168;
    /// <summary>Cookie Secure 策略：auto（随请求协议 / X-Forwarded-Proto）/ true / false。</summary>
    public string CookieSecure { get; set; } = "auto";
    /// <summary>归档 Workspace 的保留天数（默认 30）。</summary>
    public int ArchiveRetentionDays { get; set; } = 30;
    /// <summary>审计日志保留天数（0 表示永久保留）。</summary>
    public int AuditRetentionDays { get; set; } = 365;
    /// <summary>单条消息入库的最大字符数，超出截断（完整内容在事件日志中）。</summary>
    public int MessageMaxChars { get; set; } = 60000;
    /// <summary>上传文件大小上限（MB）。</summary>
    public int MaxUploadMb { get; set; } = 200;
    /// <summary>浏览器允许的跨域来源（同域部署可为空）。</summary>
    public string[] CorsOrigins { get; set; } = [];
    /// <summary>对外访问基址，用于 SDK 示例展示（可空，空则取请求地址）。</summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>每个文件保留的历史版本数（通过平台文件 API 覆盖 / 删除前保存；0 关闭版本功能）。</summary>
    public int FileVersionsKeep { get; set; } = 10;
    /// <summary>是否自动执行数据库迁移。</summary>
    public bool AutoMigrate { get; set; } = true;
}

/// <summary>Runtime 配置。</summary>
public class RuntimeOptions
{
    /// <summary>新建 Pool 的默认 Provider：DeepSeekHarness / Mock。</summary>
    public string DefaultProvider { get; set; } = "DeepSeekHarness";
    /// <summary>dsh 可执行文件（名称或绝对路径），Windows 可指向 dsh.cmd。</summary>
    public string DshExecutable { get; set; } = "dsh";
    /// <summary>首次创建 Pool 的 DSH_HOME 时，从该目录复制 .credentials.yaml / settings.yaml 作为基础配置（可空）。</summary>
    public string? SeedHome { get; set; }
    /// <summary>Runtime 空闲多少分钟后从 RUNNING 转为 IDLE。</summary>
    public int IdleMinutes { get; set; } = 5;
    /// <summary>Runtime 空闲多少分钟后自动停止。</summary>
    public int AutoStopMinutes { get; set; } = 60;
    /// <summary>单个 Pool 同时运行的 Agent 任务数上限（Pool 策略可进一步收紧）。</summary>
    public int MaxConcurrentRunsPerPool { get; set; } = 4;
    /// <summary>单次运行的最长分钟数。</summary>
    public int RunTimeoutMinutes { get; set; } = 30;
    /// <summary>是否启用 Workspace 终端（WebSocket）。终端等同于容器内 shell，请谨慎开启。</summary>
    public bool TerminalEnabled { get; set; } = false;
    /// <summary>平台级默认环境变量（优先级最低，被组织 / Pool / Workspace 密钥覆盖），如 DEEPSEEK_API_KEY。仅注入 Runtime，不对前端暴露。</summary>
    public Dictionary<string, string> ExtraEnv { get; set; } = new();
    /// <summary>允许透传给 Runtime 子进程的宿主环境变量名（其余环境变量一律不传，防止泄露平台密钥）。</summary>
    public string[] EnvPassthrough { get; set; } = ["PATH", "Path", "PATHEXT", "SystemRoot", "SYSTEMROOT", "ComSpec", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR", "LANG", "LC_ALL", "TZ", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy", "NPM_CONFIG_REGISTRY"];
}

/// <summary>安全配置。</summary>
public class SecurityOptions
{
    /// <summary>密钥主密钥（用于 AES-GCM 加密 Secret，任意长度字符串，生产必须修改并妥善保管）。</summary>
    public string MasterKey { get; set; } = "CHANGE_ME_DSH_MASTER_KEY_CHANGE_ME";
}

/// <summary>初始化数据。</summary>
public class SeedOptions
{
    /// <summary>默认组织名称。</summary>
    public string OrganizationName { get; set; } = "默认组织";
    /// <summary>首个管理员登录名。</summary>
    public string AdminUsername { get; set; } = "admin";
    /// <summary>首个管理员密码；为空时不创建（生产必须显式设置）。</summary>
    public string? AdminPassword { get; set; }
    /// <summary>管理员显示名。</summary>
    public string AdminDisplayName { get; set; } = "平台管理员";
    /// <summary>是否写入演示数据（集团/编辑部/市场部等示例 Pool 与用户，仅用于体验）。</summary>
    public bool DemoData { get; set; } = false;
}

/// <summary>限流配置。</summary>
public class RateLimitOptions
{
    /// <summary>每分钟每主体（Token/用户/IP）的请求上限，0 表示不限。</summary>
    public int PermitPerMinute { get; set; } = 1200;
    /// <summary>登录接口每分钟每 IP 的尝试上限。</summary>
    public int LoginPerMinute { get; set; } = 10;
}

/// <summary>平台默认策略（Pool 策略在其上逐级收紧）。</summary>
public class PlatformPolicyOptions
{
    /// <summary>是否允许 Agent 访问网络（信息性策略，注入 Runtime 上下文）。</summary>
    public bool AllowNetwork { get; set; } = true;
    /// <summary>是否允许 Workspace 终端。</summary>
    public bool AllowTerminal { get; set; } = true;
    /// <summary>沙箱模式：read-only / workspace-write / danger-full-access。</summary>
    public string SandboxMode { get; set; } = "workspace-write";
    /// <summary>每个 Pool 最多 Workspace 数（0 不限）。</summary>
    public int MaxWorkspaces { get; set; } = 100;
    /// <summary>单个 Workspace 私有文件配额（MB，0 不限）。</summary>
    public int MaxStorageMbPerWorkspace { get; set; } = 5120;
    /// <summary>每个 Pool 并发运行数（0 不限）。</summary>
    public int MaxConcurrentRuns { get; set; } = 4;
    /// <summary>每个 Pool 每日消息数（0 不限）。</summary>
    public int MaxMessagesPerDay { get; set; } = 0;
    /// <summary>每个 Pool 每日 token 数（0 不限）。</summary>
    public long MaxTokensPerDay { get; set; } = 0;
    /// <summary>单次运行最长分钟数（0 不限）。</summary>
    public int MaxRunMinutes { get; set; } = 30;
}
