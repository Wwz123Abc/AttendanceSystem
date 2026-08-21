using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandConfirmationAndPunchUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Confirmed",
                table: "ZKDeviceCommand",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "ZKDeviceCommand",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZKDeviceCommand_SN_Confirmed",
                table: "ZKDeviceCommand",
                columns: new[] { "SN", "Confirmed" });

            // 新索引要先建好，再删旧的——AttendancePunch.UserId 上有外键约束，MySQL/MariaDB 要求
            // 外键列任何时候都必须有索引覆盖，先删旧索引会有一瞬间没有索引覆盖 UserId，直接报错
            // "Cannot drop index ... needed in a foreign key constraint"。
            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunch_UserId_PunchType_PunchTime",
                table: "AttendancePunch",
                columns: new[] { "UserId", "PunchType", "PunchTime" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_AttendancePunch_UserId_PunchTime",
                table: "AttendancePunch");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 同样先建旧索引再删新索引，原因见 Up() 里的注释
            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunch_UserId_PunchTime",
                table: "AttendancePunch",
                columns: new[] { "UserId", "PunchTime" });

            migrationBuilder.DropIndex(
                name: "IX_ZKDeviceCommand_SN_Confirmed",
                table: "ZKDeviceCommand");

            migrationBuilder.DropIndex(
                name: "IX_AttendancePunch_UserId_PunchType_PunchTime",
                table: "AttendancePunch");

            migrationBuilder.DropColumn(
                name: "Confirmed",
                table: "ZKDeviceCommand");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "ZKDeviceCommand");
        }
    }
}
