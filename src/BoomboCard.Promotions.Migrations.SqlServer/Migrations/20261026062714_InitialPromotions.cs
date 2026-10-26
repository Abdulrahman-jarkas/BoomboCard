using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoomboCard.Promotions.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialPromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "promotions");

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "promotions",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Consumer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.EventId, x.Consumer });
                });

            migrationBuilder.CreateTable(
                name: "JobRuns",
                schema: "promotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ItemsProcessed = table.Column<int>(type: "int", nullable: false),
                    ItemsFailed = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "promotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromotionRedemptions",
                schema: "promotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromotionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckoutSessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CustomerOrdinal = table.Column<int>(type: "int", nullable: false),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotionRedemptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Promotions",
                schema: "promotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DiscountKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DiscountRate = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    DiscountCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScopeKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScopeProductIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Stacking = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MinimumSpendAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MinimumSpendCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    MaxRedemptions = table.Column<int>(type: "int", nullable: true),
                    MaxRedemptionsPerCustomer = table.Column<int>(type: "int", nullable: true),
                    RedemptionCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Promotions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_History",
                schema: "promotions",
                table: "JobRuns",
                columns: new[] { "JobName", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Dispatch",
                schema: "promotions",
                table: "OutboxMessages",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PromotionRedemptions_PromotionRedeemedAt",
                schema: "promotions",
                table: "PromotionRedemptions",
                columns: new[] { "PromotionId", "RedeemedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PromotionRedemptions_Session",
                schema: "promotions",
                table: "PromotionRedemptions",
                column: "CheckoutSessionId");

            migrationBuilder.CreateIndex(
                name: "UX_PromotionRedemptions_CustomerOrdinal",
                schema: "promotions",
                table: "PromotionRedemptions",
                columns: new[] { "PromotionId", "CustomerId", "CustomerOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PromotionRedemptions_Session",
                schema: "promotions",
                table: "PromotionRedemptions",
                columns: new[] { "PromotionId", "CheckoutSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_CreatedAt",
                schema: "promotions",
                table: "Promotions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_EndsAt",
                schema: "promotions",
                table: "Promotions",
                column: "EndsAtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_Promotions_Code",
                schema: "promotions",
                table: "Promotions",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "promotions");

            migrationBuilder.DropTable(
                name: "JobRuns",
                schema: "promotions");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "promotions");

            migrationBuilder.DropTable(
                name: "PromotionRedemptions",
                schema: "promotions");

            migrationBuilder.DropTable(
                name: "Promotions",
                schema: "promotions");
        }
    }
}
