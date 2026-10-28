using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Testpilot.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class TestSystemTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TestSystemTypeId",
                table: "TestPlans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Stations",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TestSystemTypeId",
                table: "Stations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TestSystemTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestSystemTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemTypeDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestSystemTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemTypeDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SystemTypeDevices_TestSystemTypes_TestSystemTypeId",
                        column: x => x.TestSystemTypeId,
                        principalTable: "TestSystemTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TestPlans_TestSystemTypeId",
                table: "TestPlans",
                column: "TestSystemTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Stations_TestSystemTypeId",
                table: "Stations",
                column: "TestSystemTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemTypeDevices_TestSystemTypeId_DeviceKey",
                table: "SystemTypeDevices",
                columns: new[] { "TestSystemTypeId", "DeviceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestSystemTypes_Name",
                table: "TestSystemTypes",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Stations_TestSystemTypes_TestSystemTypeId",
                table: "Stations",
                column: "TestSystemTypeId",
                principalTable: "TestSystemTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TestPlans_TestSystemTypes_TestSystemTypeId",
                table: "TestPlans",
                column: "TestSystemTypeId",
                principalTable: "TestSystemTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Stations_TestSystemTypes_TestSystemTypeId",
                table: "Stations");

            migrationBuilder.DropForeignKey(
                name: "FK_TestPlans_TestSystemTypes_TestSystemTypeId",
                table: "TestPlans");

            migrationBuilder.DropTable(
                name: "SystemTypeDevices");

            migrationBuilder.DropTable(
                name: "TestSystemTypes");

            migrationBuilder.DropIndex(
                name: "IX_TestPlans_TestSystemTypeId",
                table: "TestPlans");

            migrationBuilder.DropIndex(
                name: "IX_Stations_TestSystemTypeId",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "TestSystemTypeId",
                table: "TestPlans");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "TestSystemTypeId",
                table: "Stations");
        }
    }
}
