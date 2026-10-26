using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftChangeApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ShiftChangeEndDate",
                table: "ApprovalRequest",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ShiftChangeStartDate",
                table: "ApprovalRequest",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShiftChangeTargetShiftId",
                table: "ApprovalRequest",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShiftChangeTargetShiftName",
                table: "ApprovalRequest",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShiftChangeEndDate",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "ShiftChangeStartDate",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "ShiftChangeTargetShiftId",
                table: "ApprovalRequest");

            migrationBuilder.DropColumn(
                name: "ShiftChangeTargetShiftName",
                table: "ApprovalRequest");
        }
    }
}
