namespace Teager.Dsh.Domain;

/// <summary>API Token Scope 常量（对应产品方案第 26 节）。</summary>
public static class Scopes
{
    /// <summary>读取 Pool。</summary>
    public const string PoolRead = "pool:read";
    /// <summary>管理 Pool。</summary>
    public const string PoolManage = "pool:manage";
    /// <summary>读取 Workspace。</summary>
    public const string WorkspaceRead = "workspace:read";
    /// <summary>写 Workspace（含元数据修改）。</summary>
    public const string WorkspaceWrite = "workspace:write";
    /// <summary>在 Workspace 中执行（终端等）。</summary>
    public const string WorkspaceExecute = "workspace:execute";
    /// <summary>管理 Workspace（成员、挂载、删除）。</summary>
    public const string WorkspaceManage = "workspace:manage";
    /// <summary>读取会话。</summary>
    public const string SessionRead = "session:read";
    /// <summary>创建会话。</summary>
    public const string SessionCreate = "session:create";
    /// <summary>向会话发送消息 / 执行任务。</summary>
    public const string SessionExecute = "session:execute";
    /// <summary>读取文件。</summary>
    public const string FilesRead = "files:read";
    /// <summary>写文件。</summary>
    public const string FilesWrite = "files:write";
    /// <summary>读取共享空间。</summary>
    public const string ShareRead = "share:read";
    /// <summary>写共享空间。</summary>
    public const string ShareWrite = "share:write";
    /// <summary>管理共享空间（含授权）。</summary>
    public const string ShareManage = "share:manage";
    /// <summary>读取 Runtime。</summary>
    public const string RuntimeRead = "runtime:read";
    /// <summary>管理 Runtime。</summary>
    public const string RuntimeManage = "runtime:manage";

    /// <summary>全部合法 Scope。</summary>
    public static readonly string[] All =
    [
        PoolRead, PoolManage, WorkspaceRead, WorkspaceWrite, WorkspaceExecute, WorkspaceManage,
        SessionRead, SessionCreate, SessionExecute, FilesRead, FilesWrite,
        ShareRead, ShareWrite, ShareManage, RuntimeRead, RuntimeManage
    ];
}

/// <summary>ID 生成工具：带前缀的短随机 ID，如 pool_a1b2c3d4e5f6。</summary>
public static class IdGen
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>生成带前缀的随机 ID。</summary>
    /// <param name="prefix">前缀，如 pool / ws / ss。</param>
    /// <returns>形如 prefix_xxxxxxxxxxxx 的 ID。</returns>
    public static string New(string prefix)
    {
        Span<byte> bytes = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var chars = new char[12];
        for (var i = 0; i < 12; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return prefix + "_" + new string(chars);
    }
}
