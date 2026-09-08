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

            // 老数据可能有真实重复行（比如去重粒度改成"精确到分钟"之前、同一分钟内不同秒的两次打卡
            // 会被存成两条一模一样 UserId+PunchType+PunchTime 的记录）——直接建唯一索引会因为这些
            // 存量重复数据报错、整个迁移失败。先按 (UserId, PunchType, PunchTime) 分组，同组只留
            // Id 最小的那条（最早落库的），删掉多余的重复行，再建唯一索引。
            migrationBuilder.Sql(@"
                DELETE p1 FROM AttendancePunch p1
                INNER JOIN AttendancePunch p2
                    ON p1.UserId = p2.UserId
                   AND p1.PunchType = p2.PunchType
                   AND p1.PunchTime = p2.PunchTime
                   AND p1.Id > p2.Id;
            ");

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
