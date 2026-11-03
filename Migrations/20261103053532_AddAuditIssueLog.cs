using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditIssueLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditIssueLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RunDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    EmployeeNo = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RealName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GroupName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IssueType = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Severity = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Detail = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Stored = table.Column<decimal>(type: "decimal(65,30)", nullable: true),
                    Expected = table.Column<decimal>(type: "decimal(65,30)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditIssueLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditIssueLog_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AuditIssueLog_RunDate_WorkDate",
                table: "AuditIssueLog",
                columns: new[] { "RunDate", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditIssueLog_UserId",
                table: "AuditIssueLog",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditIssueLog_WorkDate",
                table: "AuditIssueLog",
                column: "WorkDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditIssueLog");
        }
    }
}
