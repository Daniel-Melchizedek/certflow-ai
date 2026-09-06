using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CertFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkRescheduleSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BulkSessionId",
                table: "RescheduleRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BulkRescheduleSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationToken = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CandidateEntraUserId = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SourceMessageId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkRescheduleSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedInboundMessages",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SenderEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SourceMessageId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedInboundMessages", x => x.IdempotencyKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RescheduleRequests_BulkSessionId",
                table: "RescheduleRequests",
                column: "BulkSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkRescheduleSessions_CorrelationToken",
                table: "BulkRescheduleSessions",
                column: "CorrelationToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkRescheduleSessions_IdempotencyKey",
                table: "BulkRescheduleSessions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RescheduleRequests_BulkRescheduleSessions_BulkSessionId",
                table: "RescheduleRequests",
                column: "BulkSessionId",
                principalTable: "BulkRescheduleSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RescheduleRequests_BulkRescheduleSessions_BulkSessionId",
                table: "RescheduleRequests");

            migrationBuilder.DropTable(
                name: "BulkRescheduleSessions");

            migrationBuilder.DropTable(
                name: "ProcessedInboundMessages");

            migrationBuilder.DropIndex(
                name: "IX_RescheduleRequests_BulkSessionId",
                table: "RescheduleRequests");

            migrationBuilder.DropColumn(
                name: "BulkSessionId",
                table: "RescheduleRequests");
        }
    }
}
