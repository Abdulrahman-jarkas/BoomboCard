using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoomboCard.Notifications.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                schema: "notifications",
                table: "OutboxMessages",
                type: "nvarchar(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceState",
                schema: "notifications",
                table: "OutboxMessages",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceParent",
                schema: "notifications",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "TraceState",
                schema: "notifications",
                table: "OutboxMessages");
        }
    }
}
