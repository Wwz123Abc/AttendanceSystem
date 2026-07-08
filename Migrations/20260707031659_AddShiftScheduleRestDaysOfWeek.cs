using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftScheduleRestDaysOfWeek : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RestDaysOfWeek",
                table: "ShiftSchedule",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            // 新加的列对已有班次统一先给个空字符串，这里把它们都补成"0,6"（周六周日休息），
            // 和这些班次原来实际遵循的全局周末休息规则保持一致，避免看起来变成"全年无休"
            migrationBuilder.Sql("UPDATE `ShiftSchedule` SET `RestDaysOfWeek` = '0,6' WHERE `RestDaysOfWeek` = '';");

            migrationBuilder.UpdateData(
                table: "ShiftSchedule",
                keyColumn: "Id",
                keyValue: 1,
                column: "RestDaysOfWeek",
                value: "0,6");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestDaysOfWeek",
                table: "ShiftSchedule");
        }
    }
}
