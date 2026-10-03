using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Domain;

namespace Teager.Dsh.Infrastructure.Data;

/// <summary>平台数据库上下文（SQL Server）。仅存元数据、权限、会话索引与审计；大文件与详细事件日志不入库。</summary>
public class DshDbContext(DbContextOptions<DshDbContext> options) : DbContext(options)
{
    /// <summary>组织。</summary>
    public DbSet<Organization> Organizations => Set<Organization>();
    /// <summary>用户。</summary>
    public DbSet<User> Users => Set<User>();
    /// <summary>Web 登录会话。</summary>
    public DbSet<PlatformSession> PlatformSessions => Set<PlatformSession>();
    /// <summary>Pool。</summary>
    public DbSet<Pool> Pools => Set<Pool>();
    /// <summary>Pool 闭包表。</summary>
    public DbSet<PoolClosure> PoolClosures => Set<PoolClosure>();
    /// <summary>Pool 成员。</summary>
    public DbSet<PoolMember> PoolMembers => Set<PoolMember>();
    /// <summary>Workspace。</summary>
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    /// <summary>Workspace 成员。</summary>
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    /// <summary>共享空间。</summary>
    public DbSet<SharedSpace> SharedSpaces => Set<SharedSpace>();
    /// <summary>共享空间授权。</summary>
    public DbSet<SharedSpaceGrant> SharedSpaceGrants => Set<SharedSpaceGrant>();
    /// <summary>共享空间挂载。</summary>
    public DbSet<WorkspaceSharedSpaceMount> WorkspaceSharedSpaceMounts => Set<WorkspaceSharedSpaceMount>();
    /// <summary>会话。</summary>
    public DbSet<Session> Sessions => Set<Session>();
    /// <summary>会话消息。</summary>
    public DbSet<SessionMessage> SessionMessages => Set<SessionMessage>();
    /// <summary>Agent 运行。</summary>
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    /// <summary>Runtime 实例。</summary>
    public DbSet<RuntimeInstance> Runtimes => Set<RuntimeInstance>();
    /// <summary>运行节点。</summary>
    public DbSet<RuntimeNode> RuntimeNodes => Set<RuntimeNode>();
    /// <summary>API Token。</summary>
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    /// <summary>配置项。</summary>
    public DbSet<ConfigItem> ConfigItems => Set<ConfigItem>();
    /// <summary>密钥。</summary>
    public DbSet<Secret> Secrets => Set<Secret>();
    /// <summary>用量记录。</summary>
    public DbSet<UsageLog> UsageLogs => Set<UsageLog>();
    /// <summary>审计日志。</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    /// <summary>Workspace 快照。</summary>
    public DbSet<WorkspaceSnapshot> WorkspaceSnapshots => Set<WorkspaceSnapshot>();

    /// <summary>枚举统一以字符串落库，便于直接查看数据。</summary>
    /// <param name="configurationBuilder">约定配置器。</param>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(24);
        configurationBuilder.Properties<string>().HaveMaxLength(256);
    }

    /// <summary>配置主键、索引与长文本字段。</summary>
    /// <param name="b">模型构建器。</param>
    protected override void OnModelCreating(ModelBuilder b)
    {
        // 所有以 Id 结尾的字符串列（主键 / 外键式引用）统一限长 40，避免复合索引超过 SQL Server 900 字节上限；下面的显式配置可覆盖
        foreach (var et in b.Model.GetEntityTypes())
            foreach (var p in et.GetProperties())
                if (p.ClrType == typeof(string) && p.Name.EndsWith("Id", StringComparison.Ordinal)) p.SetMaxLength(40);

        b.Entity<Organization>(e => { e.HasKey(x => x.Id); });

        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(100);
        });

        b.Entity<PlatformSession>(e =>
        {
            e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(64);
            e.HasIndex(x => x.UserId); e.HasIndex(x => x.ExpiresAt);
            e.Property(x => x.UserAgent).HasMaxLength(400);
        });

        b.Entity<Pool>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ParentPoolId);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.PolicyJson).HasColumnType("nvarchar(max)");
        });

        b.Entity<PoolClosure>(e =>
        {
            e.HasKey(x => new { x.AncestorPoolId, x.DescendantPoolId });
            e.HasIndex(x => x.DescendantPoolId);
        });

        b.Entity<PoolMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PoolId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Workspace>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PoolId);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.OverrideJson).HasColumnType("nvarchar(max)");
        });

        b.Entity<WorkspaceMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<SharedSpace>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerPoolId);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.MountName).HasMaxLength(64);
        });

        b.Entity<SharedSpaceGrant>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SharedSpaceId, x.SubjectType, x.SubjectId });
            e.HasIndex(x => new { x.SubjectType, x.SubjectId });
        });

        b.Entity<WorkspaceSharedSpaceMount>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WorkspaceId, x.SharedSpaceId }).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.MountName }).IsUnique();
            e.Property(x => x.MountName).HasMaxLength(64);
        });

        b.Entity<Session>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WorkspaceId, x.UpdatedAt });
            e.HasIndex(x => x.PoolId);
            e.Property(x => x.RuntimeSessionId).HasMaxLength(100);
        });

        b.Entity<SessionMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SessionId, x.Id });
            e.Property(x => x.Content).HasColumnType("nvarchar(max)");
        });

        b.Entity<AgentRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionId); e.HasIndex(x => new { x.PoolId, x.StartedAt });
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
        });

        b.Entity<RuntimeInstance>(e =>
        {
            e.ToTable("Runtimes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PoolId).IsUnique();
            e.Property(x => x.LastError).HasMaxLength(2000);
        });

        b.Entity<RuntimeNode>(e => { e.HasKey(x => x.Id); });

        b.Entity<ApiToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.ScopesJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.AllowedPoolsJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.AllowedWorkspacesJson).HasColumnType("nvarchar(max)");
        });

        b.Entity<ConfigItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Kind, x.Scope, x.ScopeId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.ParamsJson).HasColumnType("nvarchar(max)");
            e.Property(x => x.Content).HasColumnType("nvarchar(max)");
        });

        b.Entity<Secret>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Scope, x.ScopeId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.ValueEncrypted).HasColumnType("nvarchar(max)");
        });

        b.Entity<UsageLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PoolId, x.Time }); e.HasIndex(x => new { x.WorkspaceId, x.Time }); e.HasIndex(x => x.UserId);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Time); e.HasIndex(x => new { x.PoolId, x.Time }); e.HasIndex(x => new { x.WorkspaceId, x.Time });
            e.HasIndex(x => x.ActorUserId); e.HasIndex(x => x.Action);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.ResourceId).HasMaxLength(300);
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        b.Entity<WorkspaceSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.FilePath).HasMaxLength(500);
        });
    }
}
