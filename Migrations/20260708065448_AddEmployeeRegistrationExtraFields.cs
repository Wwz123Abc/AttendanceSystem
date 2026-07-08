using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeRegistrationExtraFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContractCompany",
                table: "EmployeeRegistration",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactName",
                table: "EmployeeRegistration",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactPhone",
                table: "EmployeeRegistration",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "HomeAddress",
                table: "EmployeeRegistration",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "IdCardPhotoUrl",
                table: "EmployeeRegistration",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Position",
                table: "EmployeeRegistration",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractCompany",
                table: "EmployeeRegistration");

            migrationBuilder.DropColumn(
                name: "EmergencyContactName",
                table: "EmployeeRegistration");

            migrationBuilder.DropColumn(
                name: "EmergencyContactPhone",
                table: "EmployeeRegistration");

            migrationBuilder.DropColumn(
                name: "HomeAddress",
                table: "EmployeeRegistration");

            migrationBuilder.DropColumn(
                name: "IdCardPhotoUrl",
                table: "EmployeeRegistration");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "EmployeeRegistration");
        }
    }
}
