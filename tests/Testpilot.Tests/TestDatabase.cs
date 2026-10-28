using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;

namespace Testpilot.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    public SqliteTestpilotDbContext Context { get; private set; } = null!;
    public TestPlanService Plans => new(Context, NullLogger<TestPlanService>.Instance);

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await database.connection.OpenAsync();
        database.Context = database.NewContext();
        await database.Context.Database.MigrateAsync();
        return database;
    }

    public SqliteTestpilotDbContext NewContext() => new(new DbContextOptionsBuilder<SqliteTestpilotDbContext>().UseSqlite(connection).Options);

    public static TestPlan Draft(int version = 1, string artNr = "4711", string fz = "A") => new()
    {
        ArtNr = artNr, Fz = fz, Version = version,
        Steps = [new TestStep { Order = 0, Name = "VCC_3V3", DeviceKey = "dmm1",
            Unit = MeasurementUnit.Volt, LimitLow = 3.2, LimitHigh = 3.4, BreakpointFlag = true,
            ParametersJson = StepParameters.Serialize(new MeasureParameters(1003, "VOLT:DC", 10, 1)) }]
    };

    /// <summary>Optimizes a <see cref="Draft"/> plan against a simulated meter that provides channel 1003.</summary>
    public static ExecutionPlan Optimize(TestPlan plan)
    {
        var configuration = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", "{\"channels\":[1003]}");
        var profile = new DriverRegistry().AddSimulatedDrivers().GetProfile(configuration);
        return new ExecutionPlanOptimizer().Optimize(plan,
            new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal) { ["dmm1"] = new(configuration, profile) });
    }

    public Task ReleaseAsync(TestPlan plan) => Plans.ReleaseAsync(plan.Id, Optimize(plan));

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await connection.DisposeAsync();
    }
}
