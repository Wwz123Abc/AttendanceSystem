using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiMidCheckWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) 先加新列
            migrationBuilder.AddColumn<string>(
                name: "MidCheckWindows",
                table: "ShiftSchedule",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MidCheckResults",
                table: "AttendanceRecord",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // 2) 老数据先搬过去，再删老列，避免线上已经配置的午间打卡窗口/已经记录的打卡时间被冲掉。
            //    ShiftSchedule 原来只有一段窗口，直接拼成新格式的"开始-结束"。
            migrationBuilder.Sql(@"
                UPDATE ShiftSchedule
                SET MidCheckWindows = CONCAT(DATE_FORMAT(MidCheckStartTime, '%H:%i'), '-', DATE_FORMAT(MidCheckEndTime, '%H:%i'))
                WHERE MidCheckStartTime IS NOT NULL AND MidCheckEndTime IS NOT NULL;
            ");
            //    AttendanceRecord 原来只存了"命中时间"，迁移脚本拿不到这条记录当时对应的窗口起止，
            //    退而求其次把命中时间本身当"起=止=命中时间"存进去——保留"这天确实打过午间卡、几点打的"这个事实，
            //    历史的 ActualWorkHours 已经算好存在记录里了，这里不会也不需要重新计算。
            migrationBuilder.Sql(@"
                UPDATE AttendanceRecord
                SET MidCheckResults = CONCAT(DATE_FORMAT(MidCheckTime, '%H:%i'), '-', DATE_FORMAT(MidCheckTime, '%H:%i'), '=', DATE_FORMAT(MidCheckTime, '%H:%i'))
                WHERE MidCheckTime IS NOT NULL;
            ");

            migrationBuilder.DropColumn(
                name: "MidCheckEndTime",
                table: "ShiftSchedule");

            migrationBuilder.DropColumn(
                name: "MidCheckStartTime",
                table: "ShiftSchedule");

            migrationBuilder.DropColumn(
                name: "MidCheckTime",
                table: "AttendanceRecord");

            migrationBuilder.UpdateData(
                table: "ShiftSchedule",
                keyColumn: "Id",
                keyValue: 1,
                column: "MidCheckWindows",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MidCheckWindows",
                table: "ShiftSchedule");

            migrationBuilder.DropColumn(
                name: "MidCheckResults",
                table: "AttendanceRecord");

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
    }
}
