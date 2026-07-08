using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AttendanceSystem.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiLocationAndDeptGroupFollow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttendanceGroupId",
                table: "Department",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LocationAbnormal",
                table: "AttendanceRecord",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LocationAbnormalNote",
                table: "AttendanceRecord",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "LocationValid",
                table: "AttendancePunch",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AttendanceGroupLocation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AttendanceGroupId = table.Column<int>(type: "int", nullable: false),
                    LocationName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Latitude = table.Column<double>(type: "double", nullable: false),
                    Longitude = table.Column<double>(type: "double", nullable: false),
                    RadiusMeters = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceGroupLocation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceGroupLocation_AttendanceGroup_AttendanceGroupId",
                        column: x => x.AttendanceGroupId,
                        principalTable: "AttendanceGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "Department",
                keyColumn: "Id",
                keyValue: 1,
                column: "AttendanceGroupId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_Department_AttendanceGroupId",
                table: "Department",
                column: "AttendanceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceGroupLocation_AttendanceGroupId",
                table: "AttendanceGroupLocation",
                column: "AttendanceGroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Department_AttendanceGroup_AttendanceGroupId",
                table: "Department",
                column: "AttendanceGroupId",
                principalTable: "AttendanceGroup",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ── 数据迁移：把旧的单地点/单部门字段的现有数据搬到新结构里，再删旧列 ──

            // 1) 旧的"一个考勤组一个打卡地点"→ 新的 AttendanceGroupLocation 表（只搬已经配了完整经纬度的）
            migrationBuilder.Sql(@"
                INSERT INTO `AttendanceGroupLocation` (`AttendanceGroupId`, `LocationName`, `Latitude`, `Longitude`, `RadiusMeters`)
                SELECT `Id`, `LocationName`, `LocationLatitude`, `LocationLongitude`, `PunchRadiusMeters`
                FROM `AttendanceGroup`
                WHERE `LocationLatitude` IS NOT NULL AND `LocationLongitude` IS NOT NULL;
            ");

            // 2) 旧的"考勤组→部门"单选（AttendanceGroup.DepartmentId）→ 新的"部门→考勤组"反向关联（Department.AttendanceGroupId）
            migrationBuilder.Sql(@"
                UPDATE `Department` d
                JOIN `AttendanceGroup` g ON g.`DepartmentId` = d.`Id`
                SET d.`AttendanceGroupId` = g.`Id`;
            ");

            // 3) 旧字段已经搬完，删掉（考勤组不再对应单个部门/单个地点）
            migrationBuilder.DropColumn(name: "DepartmentId",      table: "AttendanceGroup");
            migrationBuilder.DropColumn(name: "LocationLatitude",  table: "AttendanceGroup");
            migrationBuilder.DropColumn(name: "LocationLongitude", table: "AttendanceGroup");
            migrationBuilder.DropColumn(name: "LocationName",      table: "AttendanceGroup");
            migrationBuilder.DropColumn(name: "PunchRadiusMeters", table: "AttendanceGroup");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Department_AttendanceGroup_AttendanceGroupId",
                table: "Department");

            migrationBuilder.DropTable(
                name: "AttendanceGroupLocation");

            migrationBuilder.DropIndex(
                name: "IX_Department_AttendanceGroupId",
                table: "Department");

            migrationBuilder.DropColumn(
                name: "AttendanceGroupId",
                table: "Department");

            migrationBuilder.DropColumn(
                name: "LocationAbnormal",
                table: "AttendanceRecord");

            migrationBuilder.DropColumn(
                name: "LocationAbnormalNote",
                table: "AttendanceRecord");

            migrationBuilder.DropColumn(
                name: "LocationValid",
                table: "AttendancePunch");

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "AttendanceGroup",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LocationLatitude",
                table: "AttendanceGroup",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LocationLongitude",
                table: "AttendanceGroup",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationName",
                table: "AttendanceGroup",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "PunchRadiusMeters",
                table: "AttendanceGroup",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "AttendanceGroup",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "DepartmentId", "LocationLatitude", "LocationLongitude", "LocationName", "PunchRadiusMeters" },
                values: new object[] { null, null, null, null, 500 });
        }
    }
}
