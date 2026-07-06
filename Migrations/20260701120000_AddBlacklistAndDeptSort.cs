using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddBlacklistAndDeptSort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 员工表：新增“是否黑名单”开关（默认 false）
            migrationBuilder.AddColumn<bool>(
                name: "IsBlacklisted",
                table: "User",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // 部门表：新增“排序号”（默认 0）
            migrationBuilder.AddColumn<int>(
                name: "SortIndex",
                table: "Department",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsBlacklisted", table: "User");
            migrationBuilder.DropColumn(name: "SortIndex", table: "Department");
        }
    }
}
