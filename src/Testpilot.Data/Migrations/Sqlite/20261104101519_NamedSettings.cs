using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Testpilot.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class NamedSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "TestPlans",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "Stations",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "TestPlans");

            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "Stations");
        }
    }
}
