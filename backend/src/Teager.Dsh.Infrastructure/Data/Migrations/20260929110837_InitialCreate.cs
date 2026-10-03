using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teager.Dsh.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkspaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ToolCalls = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TokenPrefix = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ScopesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    AllowedPoolsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    AllowedWorkspacesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedIp = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Time = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Ip = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Via = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResourceId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    WorkspaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ScopeId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ParamsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    Content = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Ip = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PoolClosures",
                columns: table => new
                {
                    AncestorPoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DescendantPoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Depth = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoolClosures", x => new { x.AncestorPoolId, x.DescendantPoolId });
                });

            migrationBuilder.CreateTable(
                name: "PoolMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    MembershipType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoolMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pools",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OrganizationId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ParentPoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Depth = table.Column<int>(type: "int", nullable: false),
                    RuntimeProvider = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InheritConfig = table.Column<bool>(type: "bit", nullable: false),
                    PolicyJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeNodes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    MaxRuntimes = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runtimes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NodeId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    InternalEndpoint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Version = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastActiveAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runtimes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Secrets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ScopeId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ValueEncrypted = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Secrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SessionMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    RunId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkspaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ModelKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RuntimeSessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastEventSeq = table.Column<long>(type: "bigint", nullable: false),
                    MessageCount = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharedSpaceGrants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SharedSpaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Effect = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    InheritToChildren = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedSpaceGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharedSpaces",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OrganizationId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OwnerPoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MountName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    QuotaMb = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    StorageBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedSpaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Time = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkspaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RunId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ToolCalls = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OrganizationId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    IsServiceAccount = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkspaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Effect = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PoolId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    InheritPoolMembers = table.Column<bool>(type: "bit", nullable: false),
                    OverrideJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PurgeAfter = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StorageBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastActiveAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceSharedSpaceMounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkspaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SharedSpaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MountName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    PermissionOverride = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceSharedSpaceMounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceSnapshots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    WorkspaceId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    FilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_PoolId_StartedAt",
                table: "AgentRuns",
                columns: new[] { "PoolId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionId",
                table: "AgentRuns",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_OwnerUserId",
                table: "ApiTokens",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenHash",
                table: "ApiTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action",
                table: "AuditLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ActorUserId",
                table: "AuditLogs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_PoolId_Time",
                table: "AuditLogs",
                columns: new[] { "PoolId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Time",
                table: "AuditLogs",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_WorkspaceId_Time",
                table: "AuditLogs",
                columns: new[] { "WorkspaceId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigItems_Kind_Scope_ScopeId_Key",
                table: "ConfigItems",
                columns: new[] { "Kind", "Scope", "ScopeId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSessions_ExpiresAt",
                table: "PlatformSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSessions_UserId",
                table: "PlatformSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PoolClosures_DescendantPoolId",
                table: "PoolClosures",
                column: "DescendantPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_PoolMembers_PoolId_UserId",
                table: "PoolMembers",
                columns: new[] { "PoolId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PoolMembers_UserId",
                table: "PoolMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Pools_ParentPoolId",
                table: "Pools",
                column: "ParentPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_Runtimes_PoolId",
                table: "Runtimes",
                column: "PoolId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Secrets_Scope_ScopeId_Name",
                table: "Secrets",
                columns: new[] { "Scope", "ScopeId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionMessages_SessionId_Id",
                table: "SessionMessages",
                columns: new[] { "SessionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PoolId",
                table: "Sessions",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_WorkspaceId_UpdatedAt",
                table: "Sessions",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedSpaceGrants_SharedSpaceId_SubjectType_SubjectId",
                table: "SharedSpaceGrants",
                columns: new[] { "SharedSpaceId", "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedSpaceGrants_SubjectType_SubjectId",
                table: "SharedSpaceGrants",
                columns: new[] { "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedSpaces_OwnerPoolId",
                table: "SharedSpaces",
                column: "OwnerPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageLogs_PoolId_Time",
                table: "UsageLogs",
                columns: new[] { "PoolId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageLogs_UserId",
                table: "UsageLogs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageLogs_WorkspaceId_Time",
                table: "UsageLogs",
                columns: new[] { "WorkspaceId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_UserId",
                table: "WorkspaceMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_WorkspaceId_UserId",
                table: "WorkspaceMembers",
                columns: new[] { "WorkspaceId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_PoolId",
                table: "Workspaces",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceSharedSpaceMounts_WorkspaceId_MountName",
                table: "WorkspaceSharedSpaceMounts",
                columns: new[] { "WorkspaceId", "MountName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceSharedSpaceMounts_WorkspaceId_SharedSpaceId",
                table: "WorkspaceSharedSpaceMounts",
                columns: new[] { "WorkspaceId", "SharedSpaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceSnapshots_WorkspaceId",
                table: "WorkspaceSnapshots",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentRuns");

            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "ConfigItems");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropTable(
                name: "PlatformSessions");

            migrationBuilder.DropTable(
                name: "PoolClosures");

            migrationBuilder.DropTable(
                name: "PoolMembers");

            migrationBuilder.DropTable(
                name: "Pools");

            migrationBuilder.DropTable(
                name: "RuntimeNodes");

            migrationBuilder.DropTable(
                name: "Runtimes");

            migrationBuilder.DropTable(
                name: "Secrets");

            migrationBuilder.DropTable(
                name: "SessionMessages");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "SharedSpaceGrants");

            migrationBuilder.DropTable(
                name: "SharedSpaces");

            migrationBuilder.DropTable(
                name: "UsageLogs");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WorkspaceMembers");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "WorkspaceSharedSpaceMounts");

            migrationBuilder.DropTable(
                name: "WorkspaceSnapshots");
        }
    }
}
