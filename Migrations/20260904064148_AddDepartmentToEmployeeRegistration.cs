using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartmentToEmployeeRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "EmployeeRegistration",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeRegistration_DepartmentId",
                table: "EmployeeRegistration",
                column: "DepartmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeRegistration_Department_DepartmentId",
                table: "EmployeeRegistration",
                column: "DepartmentId",
                principalTable: "Department",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeRegistration_Department_DepartmentId",
                table: "EmployeeRegistration");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeRegistration_DepartmentId",
                table: "EmployeeRegistration");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "EmployeeRegistration");
        }
    }
}
