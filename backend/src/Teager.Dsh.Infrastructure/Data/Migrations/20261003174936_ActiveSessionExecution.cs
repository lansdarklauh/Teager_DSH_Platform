using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teager.Dsh.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActiveSessionExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_AgentRuns_ActiveSession",
                table: "AgentRuns",
                column: "SessionId",
                unique: true,
                filter: "[Status] = N'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AgentRuns_ActiveSession",
                table: "AgentRuns");
        }
    }
}
