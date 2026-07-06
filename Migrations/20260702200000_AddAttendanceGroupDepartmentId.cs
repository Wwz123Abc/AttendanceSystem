using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceGroupDepartmentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 考勤组表：新增“对应部门编号”，用于“按部门自动建考勤组、考勤组跟随部门”
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "AttendanceGroup",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "DepartmentId", table: "AttendanceGroup");
        }
    }
}
