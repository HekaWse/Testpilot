using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Testpilot.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fauf = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ArtNr = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Fz = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Station = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Operator = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlanVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Result = table.Column<int>(type: "INTEGER", nullable: true),
                    SoftwareVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TestPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtNr = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Fz = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutionPlanJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Identification = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunDevices_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunStepResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StepType = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Channel = table.Column<int>(type: "INTEGER", nullable: true),
                    Function = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Value = table.Column<double>(type: "REAL", nullable: true),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    LimitLow = table.Column<double>(type: "REAL", nullable: true),
                    LimitHigh = table.Column<double>(type: "REAL", nullable: true),
                    Unit = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunStepResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunStepResults_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StationDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Driver = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ConnectionParametersJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StationDevices_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TestPlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StepType = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    LimitLow = table.Column<double>(type: "REAL", nullable: true),
                    LimitHigh = table.Column<double>(type: "REAL", nullable: true),
                    Unit = table.Column<int>(type: "INTEGER", nullable: true),
                    BreakpointFlag = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestSteps_TestPlans_TestPlanId",
                        column: x => x.TestPlanId,
                        principalTable: "TestPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunDevices_RunId_DeviceKey",
                table: "RunDevices",
                columns: new[] { "RunId", "DeviceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunStepResults_RunId_Order",
                table: "RunStepResults",
                columns: new[] { "RunId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StationDevices_StationId_DeviceKey",
                table: "StationDevices",
                columns: new[] { "StationId", "DeviceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stations_Name",
                table: "Stations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestPlans_ArtNr_Fz_Version",
                table: "TestPlans",
                columns: new[] { "ArtNr", "Fz", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestPlans_OneReleasedVersion",
                table: "TestPlans",
                columns: new[] { "ArtNr", "Fz" },
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TestSteps_TestPlanId_Order",
                table: "TestSteps",
                columns: new[] { "TestPlanId", "Order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunDevices");

            migrationBuilder.DropTable(
                name: "RunStepResults");

            migrationBuilder.DropTable(
                name: "StationDevices");

            migrationBuilder.DropTable(
                name: "TestSteps");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Stations");

            migrationBuilder.DropTable(
                name: "TestPlans");
        }
    }
}
