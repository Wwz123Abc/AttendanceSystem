using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddPunchAdjustLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PunchAdjustLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OperatorName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AdjustType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Remark = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecordExisted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PrevClockIn = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PrevClockOut = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    PrevStatus = table.Column<int>(type: "int", nullable: false),
                    PrevActualWorkHours = table.Column<decimal>(type: "decimal(65,30)", nullable: false),
                    PrevLateMinutes = table.Column<int>(type: "int", nullable: false),
                    PrevEarlyLeaveMinutes = table.Column<int>(type: "int", nullable: false),
                    PrevMidCheckResults = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NewClockIn = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NewClockOut = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AddedMidCheckTime = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RevertedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RevertedBy = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PunchAdjustLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PunchAdjustLog_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PunchAdjustLog_UserId_WorkDate",
                table: "PunchAdjustLog",
                columns: new[] { "UserId", "WorkDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PunchAdjustLog");
        }
    }
}
