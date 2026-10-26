using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoomboCard.Catalog.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                schema: "catalog",
                table: "OutboxMessages",
                type: "nvarchar(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceState",
                schema: "catalog",
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
                schema: "catalog",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "TraceState",
                schema: "catalog",
                table: "OutboxMessages");
        }
    }
}
