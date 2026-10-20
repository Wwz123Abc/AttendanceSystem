using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupHourlyWorkStartDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "HourlyWorkStartDate",
                table: "AttendanceGroup",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HourlyWorkStartDate",
                table: "AttendanceGroup");
        }
    }
}
