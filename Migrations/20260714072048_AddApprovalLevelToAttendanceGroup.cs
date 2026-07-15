using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalLevelToAttendanceGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue 用 1（Level1/一级审批），不用 EF 默认生成的 0——
            // 0 不对应枚举里任何一个值，存量考勤组升级后应该落在"一级审批"这个有意义的默认状态，而不是一个未定义的空值。
            migrationBuilder.AddColumn<int>(
                name: "ApprovalLevel",
                table: "AttendanceGroup",
                type: "int",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalLevel",
                table: "AttendanceGroup");
        }
    }
}
