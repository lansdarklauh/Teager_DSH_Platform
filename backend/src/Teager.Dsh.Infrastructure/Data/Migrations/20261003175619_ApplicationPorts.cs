using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teager.Dsh.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationPorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PortsJson",
                table: "WorkspaceExecution",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PortsJson",
                table: "WorkspaceExecution");
        }
    }
}
