using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teager.Dsh.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsageOnlyWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedUserId",
                table: "Workspaces",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultWorkspaceId",
                table: "Users",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanEditDetails",
                table: "PoolMembers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AllowedSharedSpacesJson",
                table: "ApiTokens",
                type: "nvarchar(max)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "WorkspaceProject",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    WorkspaceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Directory = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceProject", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_AssignedUserId",
                table: "Workspaces",
                column: "AssignedUserId",
                unique: true,
                filter: "[AssignedUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceProject_WorkspaceId_Name",
                table: "WorkspaceProject",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceProject");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_AssignedUserId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AssignedUserId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "DefaultWorkspaceId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CanEditDetails",
                table: "PoolMembers");

            migrationBuilder.DropColumn(
                name: "AllowedSharedSpacesJson",
                table: "ApiTokens");
        }
    }
}
