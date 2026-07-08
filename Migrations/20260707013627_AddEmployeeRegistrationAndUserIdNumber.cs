using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeRegistrationAndUserIdNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdNumber",
                table: "User",
                type: "varchar(18)",
                maxLength: 18,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "EmployeeRegistration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RealName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Phone = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdNumber = table.Column<string>(type: "varchar(18)", maxLength: 18, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConfirmedUserId = table.Column<int>(type: "int", nullable: true),
                    RejectReason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeRegistration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeRegistration_User_ConfirmedUserId",
                        column: x => x.ConfirmedUserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_User_IdNumber",
                table: "User",
                column: "IdNumber");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeRegistration_ConfirmedUserId",
                table: "EmployeeRegistration",
                column: "ConfirmedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeRegistration_IdNumber",
                table: "EmployeeRegistration",
                column: "IdNumber");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeRegistration_Phone",
                table: "EmployeeRegistration",
                column: "Phone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeRegistration");

            migrationBuilder.DropIndex(
                name: "IX_User_IdNumber",
                table: "User");

            migrationBuilder.DropColumn(
                name: "IdNumber",
                table: "User");
        }
    }
}
