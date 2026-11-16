using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teager.Dsh.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IdempotentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "AgentRuns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "AgentRuns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_UserId_RequestId",
                table: "AgentRuns",
                columns: new[] { "UserId", "RequestId" },
                unique: true,
                filter: "[RequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_UserId_RequestId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "AgentRuns");
        }
    }
}
