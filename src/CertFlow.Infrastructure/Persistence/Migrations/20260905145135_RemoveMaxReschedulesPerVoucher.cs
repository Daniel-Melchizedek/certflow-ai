using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CertFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMaxReschedulesPerVoucher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxReschedulesPerVoucher",
                table: "ReschedulePolicies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxReschedulesPerVoucher",
                table: "ReschedulePolicies",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
