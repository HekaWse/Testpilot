using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testpilot.Core;
using Testpilot.Data;

namespace Testpilot.Tests;

public sealed class DatabaseConfigurationTests
{
    [Theory]
    [InlineData("Sqlite", "Data Source=:memory:", "Microsoft.EntityFrameworkCore.Sqlite", "\"Status\" = 1")]
    [InlineData("SqlServer", "Server=(localdb)\\MSSQLLocalDB;Database=Testpilot;Trusted_Connection=True", "Microsoft.EntityFrameworkCore.SqlServer", "[Status] = 1")]
    [InlineData("PostgreSql", "Host=localhost;Database=testpilot;Username=testpilot", "Npgsql.EntityFrameworkCore.PostgreSQL", "\"Status\" = 1")]
    public void ProviderAndMigrationsAreSelectedOnlyByConfiguration(string provider, string connection, string expectedName, string filter)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider, ["ConnectionStrings:Testpilot"] = connection
        }).Build();
        var services = new ServiceCollection().AddLogging().AddTestpilotData(configuration);
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<TestpilotDbContext>();
        Assert.Same(scope.ServiceProvider.GetRequiredService<TestPlanService>(), scope.ServiceProvider.GetRequiredService<ITestPlanLookup>());
        Assert.Equal(expectedName, database.Database.ProviderName);
        var migrations = database.Database.GetMigrations().ToArray();
        Assert.Contains(migrations, id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Contains(migrations, id => id.EndsWith("_Phase3RunStepTextValue", StringComparison.Ordinal));
        var index = database.Model.FindEntityType(typeof(TestPlan))!.GetIndexes().Single(x => x.GetDatabaseName() == "IX_TestPlans_OneReleasedVersion");
        Assert.True(index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
        Assert.False(database.Database.HasPendingModelChanges());
        var script = database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().GenerateScript();
        Assert.Contains("IX_TestPlans_OneReleasedVersion", script);
        Assert.Contains(filter, script);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unknown")]
    public void InvalidProviderFailsExplicitly(string? provider)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider, ["ConnectionStrings:Testpilot"] = "Data Source=testpilot.db"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTestpilotData(configuration));
    }

    [Fact]
    public async Task StationDeviceKeysAreUniquePerStationNotGlobal()
    {
        await using var db = await TestDatabase.CreateAsync();
        var first = new Station { Name = "Bench1", Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "SimulatedDmm" }] };
        var second = new Station { Name = "Bench2", Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "SimulatedDmm" }] };
        db.Context.AddRange(first, second);
        await db.Context.SaveChangesAsync();
        Assert.Equal(2, await db.Context.StationDevices.CountAsync());
        first.Devices.Add(new StationDevice { DeviceKey = "dmm1", Driver = "Other" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task VersionAndStepOrderUniquenessAreEnforced()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Context.AddRange(TestDatabase.Draft(), TestDatabase.Draft());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        db.Context.ChangeTracker.Clear();
        var plan = TestDatabase.Draft();
        plan.Steps.Add(new TestStep { Order = 0, Name = "Wait", StepType = StepType.Wait,
            ParametersJson = StepParameters.Serialize(new WaitParameters(1)) });
        db.Context.Add(plan);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
    }
}
