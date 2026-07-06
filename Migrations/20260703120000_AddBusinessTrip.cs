using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessTrip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 审批申请表：新增“出差”三个字段（开始/结束时间、目的地）
            migrationBuilder.AddColumn<DateTime>(
                name: "BusinessTripStartTime",
                table: "ApprovalRequest",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BusinessTripEndTime",
                table: "ApprovalRequest",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessTripDestination",
                table: "ApprovalRequest",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "BusinessTripDurationDays",
                table: "ApprovalRequest",
                type: "decimal(65,30)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BusinessTripStartTime", table: "ApprovalRequest");
            migrationBuilder.DropColumn(name: "BusinessTripEndTime", table: "ApprovalRequest");
            migrationBuilder.DropColumn(name: "BusinessTripDestination", table: "ApprovalRequest");
            migrationBuilder.DropColumn(name: "BusinessTripDurationDays", table: "ApprovalRequest");
        }
    }
}
