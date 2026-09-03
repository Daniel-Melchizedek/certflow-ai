using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CertFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CandidateEntraUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Candidates",
                columns: table => new
                {
                    EntraUserId = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Department = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candidates", x => x.EntraUserId);
                });

            migrationBuilder.CreateTable(
                name: "ExamPrograms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamPrograms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TestCenters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AddressLine1 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AddressLine2 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IanaTimeZone = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestCenters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExamVouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateEntraUserId = table.Column<string>(type: "nvarchar(36)", nullable: false),
                    ExamProgramId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsUsed = table.Column<bool>(type: "bit", nullable: false),
                    RescheduleCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamVouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExamVouchers_Candidates_CandidateEntraUserId",
                        column: x => x.CandidateEntraUserId,
                        principalTable: "Candidates",
                        principalColumn: "EntraUserId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamVouchers_ExamPrograms_ExamProgramId",
                        column: x => x.ExamProgramId,
                        principalTable: "ExamPrograms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReschedulePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExamProgramId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinHoursBeforeExam = table.Column<int>(type: "int", nullable: false),
                    MaxReschedulesPerVoucher = table.Column<int>(type: "int", nullable: false),
                    AllowDeliveryModeChange = table.Column<bool>(type: "bit", nullable: false),
                    RequiresAccommodationReview = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReschedulePolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReschedulePolicies_ExamPrograms_ExamProgramId",
                        column: x => x.ExamProgramId,
                        principalTable: "ExamPrograms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestCenterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentSlots_TestCenters_TestCenterId",
                        column: x => x.TestCenterId,
                        principalTable: "TestCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateEntraUserId = table.Column<string>(type: "nvarchar(36)", nullable: false),
                    SlotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoucherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    HasAccommodations = table.Column<bool>(type: "bit", nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RegistrationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Appointments_AppointmentSlots_SlotId",
                        column: x => x.SlotId,
                        principalTable: "AppointmentSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Appointments_Candidates_CandidateEntraUserId",
                        column: x => x.CandidateEntraUserId,
                        principalTable: "Candidates",
                        principalColumn: "EntraUserId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Appointments_ExamVouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "ExamVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RescheduleRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateEntraUserId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProposedSlotIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConfirmedSlotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationToken = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AgentReasoning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RescheduleRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RescheduleRequests_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_CandidateEntraUserId",
                table: "Appointments",
                column: "CandidateEntraUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_SlotId",
                table: "Appointments",
                column: "SlotId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_VoucherId",
                table: "Appointments",
                column: "VoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSlots_TestCenterId",
                table: "AppointmentSlots",
                column: "TestCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamVouchers_CandidateEntraUserId",
                table: "ExamVouchers",
                column: "CandidateEntraUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamVouchers_ExamProgramId",
                table: "ExamVouchers",
                column: "ExamProgramId");

            migrationBuilder.CreateIndex(
                name: "IX_ReschedulePolicies_ExamProgramId",
                table: "ReschedulePolicies",
                column: "ExamProgramId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RescheduleRequests_AppointmentId",
                table: "RescheduleRequests",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RescheduleRequests_CorrelationToken",
                table: "RescheduleRequests",
                column: "CorrelationToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RescheduleRequests_IdempotencyKey",
                table: "RescheduleRequests",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ReschedulePolicies");

            migrationBuilder.DropTable(
                name: "RescheduleRequests");

            migrationBuilder.DropTable(
                name: "Appointments");

            migrationBuilder.DropTable(
                name: "AppointmentSlots");

            migrationBuilder.DropTable(
                name: "ExamVouchers");

            migrationBuilder.DropTable(
                name: "TestCenters");

            migrationBuilder.DropTable(
                name: "Candidates");

            migrationBuilder.DropTable(
                name: "ExamPrograms");
        }
    }
}
