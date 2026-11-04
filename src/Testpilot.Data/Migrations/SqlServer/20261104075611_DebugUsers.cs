using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Testpilot.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class DebugUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DebugUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShortName = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PasswordSalt = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PasswordIterations = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebugUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DebugUsers_ShortName",
                table: "DebugUsers",
                column: "ShortName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DebugUsers");
        }
    }
}
