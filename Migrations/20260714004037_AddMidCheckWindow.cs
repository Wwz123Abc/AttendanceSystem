using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddMidCheckWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "MidCheckEndTime",
                table: "ShiftSchedule",
                type: "time(6)",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "MidCheckStartTime",
                table: "ShiftSchedule",
                type: "time(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MidCheckTime",
                table: "AttendanceRecord",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ShiftSchedule",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "MidCheckEndTime", "MidCheckStartTime" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MidCheckEndTime",
                table: "ShiftSchedule");

            migrationBuilder.DropColumn(
                name: "MidCheckStartTime",
                table: "ShiftSchedule");

            migrationBuilder.DropColumn(
                name: "MidCheckTime",
                table: "AttendanceRecord");
        }
    }
}
