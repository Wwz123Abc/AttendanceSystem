using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalStepApproverStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 必须先建新索引、再删旧索引：ApproverUserId 上有外键约束，MySQL 不允许把"唯一还在覆盖
            // 这个外键列的索引"删掉，哪怕紧接着就要建一个同样以它开头的新索引也不行——两条语句是
            // 分开执行的，删除那一刻必须已经有别的索引覆盖着这个外键列（发现于 2026-09-21 实际部署时）。
            migrationBuilder.CreateIndex(
                name: "IX_ApprovalStep_ApproverUserId_ApprovalStatus",
                table: "ApprovalStep",
                columns: new[] { "ApproverUserId", "ApprovalStatus" });

            migrationBuilder.DropIndex(
                name: "IX_ApprovalStep_ApproverUserId",
                table: "ApprovalStep");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 同样先建后删，理由见 Up() 的注释。
            migrationBuilder.CreateIndex(
                name: "IX_ApprovalStep_ApproverUserId",
                table: "ApprovalStep",
                column: "ApproverUserId");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalStep_ApproverUserId_ApprovalStatus",
                table: "ApprovalStep");
        }
    }
}
