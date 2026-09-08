using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchScopingAndDeviceAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "ZKDevice",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScopedDepartmentId",
                table: "User",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserZKDevice",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    ZKDeviceId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserZKDevice", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserZKDevice_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserZKDevice_ZKDevice_ZKDeviceId",
                        column: x => x.ZKDeviceId,
                        principalTable: "ZKDevice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ZKDevice_DepartmentId",
                table: "ZKDevice",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_User_ScopedDepartmentId",
                table: "User",
                column: "ScopedDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_UserZKDevice_UserId_ZKDeviceId",
                table: "UserZKDevice",
                columns: new[] { "UserId", "ZKDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserZKDevice_ZKDeviceId",
                table: "UserZKDevice",
                column: "ZKDeviceId");

            migrationBuilder.AddForeignKey(
                name: "FK_User_Department_ScopedDepartmentId",
                table: "User",
                column: "ScopedDepartmentId",
                principalTable: "Department",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ZKDevice_Department_DepartmentId",
                table: "ZKDevice",
                column: "DepartmentId",
                principalTable: "Department",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_User_Department_ScopedDepartmentId",
                table: "User");

            migrationBuilder.DropForeignKey(
                name: "FK_ZKDevice_Department_DepartmentId",
                table: "ZKDevice");

            migrationBuilder.DropTable(
                name: "UserZKDevice");

            migrationBuilder.DropIndex(
                name: "IX_ZKDevice_DepartmentId",
                table: "ZKDevice");

            migrationBuilder.DropIndex(
                name: "IX_User_ScopedDepartmentId",
                table: "User");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "ZKDevice");

            migrationBuilder.DropColumn(
                name: "ScopedDepartmentId",
                table: "User");
        }
    }
}
