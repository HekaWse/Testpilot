using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class Phase6RunPreparationTests
{
    private const string MeterParameters = "{\"channels\":[1003],\"readings\":{\"1003|VOLT:DC\":3.3}}";

    [Fact]
    public async Task OperatorFlowFindsReleasedPlanThroughSapAndRunsIt()
    {
        await using var testDatabase = await CreateReleasedPlanAsync();
        var plan = testDatabase.Context.TestPlans.Single();
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A"))]);
        var registry = new DriverRegistry().AddSimulatedDrivers();
        var preparation = new RunPreparation(testDatabase.Context, sap, registry, simulationEnabled: true);

        var prepared = await preparation.PrepareForOrderAsync("Bench 1", "FAUF-1");

        Assert.Equal(plan.Id, prepared.Plan.PlanId);
        Assert.Equal(new OrderInfo("4711", "A"), prepared.Order);
        Assert.True(prepared.Simulated);
        Assert.Equal("SimulatedSwitchMultimeter", prepared.Plan.Devices.Single().Driver);
        Assert.Contains("FAUF FAUF-1: 4711/A version 1", prepared.ToString(), StringComparison.Ordinal);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"testpilot-phase6-{Guid.NewGuid():N}");
        try
        {
            var runner = new SequenceRunner(new DriverHost(registry, NullLoggerFactory.Instance), testDatabase.Context,
                new RunJsonExporter(outputDirectory, NullLogger<RunJsonExporter>.Instance),
                NullLogger<SequenceRunner>.Instance, new StepExecutorRegistry(new CustomStepRegistry()),
                new ContinueInteraction(), new SequenceOptions(TimeSpan.FromSeconds(5), false));

            var result = await runner.RunAsync(prepared.Plan, prepared.CreateContext("1234567890", "Bediener"));

            Assert.Equal(RunOutcome.Pass, result.Run.Result);
            Assert.Equal("FAUF-1", result.Run.Fauf);
            Assert.Equal("Bench 1", result.Run.Station);
            Assert.Equal(3.3, result.Run.StepResults.Single().Value);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownOrderIsReportedBySap()
    {
        await using var testDatabase = await CreateReleasedPlanAsync();
        var preparation = new RunPreparation(testDatabase.Context, new MockSapClient(),
            new DriverRegistry().AddSimulatedDrivers(), simulationEnabled: true);

        await Assert.ThrowsAsync<SapOrderNotFoundException>(() => preparation.PrepareForOrderAsync("Bench 1", "FAUF-X"));
    }

    [Fact]
    public async Task OrderWithoutReleasedPlanIsRejected()
    {
        await using var testDatabase = await CreateReleasedPlanAsync();
        testDatabase.Context.TestPlans.Add(TestDatabase.Draft(artNr: "4712"));
        await testDatabase.Context.SaveChangesAsync();
        var sap = new MockSapClient([new("FAUF-2", new OrderInfo("4712", "A"))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddSimulatedDrivers(), true);

        var error = await Assert.ThrowsAsync<PlanNotFoundException>(() => preparation.PrepareForOrderAsync("Bench 1", "FAUF-2"));

        Assert.Contains("4712", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bench 2", "Prüfsystem 'Bench 2' wurde nicht gefunden")]
    [InlineData("Bench without meter", "dmm1")]
    public async Task StationMustExistAndContainAllPlanDevices(string station, string expectedMessage)
    {
        await using var testDatabase = await CreateReleasedPlanAsync();
        testDatabase.Context.Stations.Add(new Station { Name = "Bench without meter" });
        await testDatabase.Context.SaveChangesAsync();
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A"))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddSimulatedDrivers(), true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareForOrderAsync(station, "FAUF-1"));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedPlanMustMatchTheSapOrder()
    {
        await using var testDatabase = await CreateReleasedPlanAsync();
        var planId = testDatabase.Context.TestPlans.Single().Id;
        var sap = new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A")), new("FAUF-9", new OrderInfo("9999", "B"))]);
        var preparation = new RunPreparation(testDatabase.Context, sap, new DriverRegistry().AddSimulatedDrivers(), false);

        var prepared = await preparation.PrepareForPlanAsync("Bench 1", planId, "FAUF-1");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareForPlanAsync("Bench 1", planId, "FAUF-9"));

        Assert.False(prepared.Simulated);
        Assert.Equal(ExecutionPlanSerializer.Serialize(ExecutionPlanSerializer.Deserialize(testDatabase.Context.TestPlans.Single().ExecutionPlanJson!)),
            ExecutionPlanSerializer.Serialize(prepared.Plan));
        Assert.Contains("9999/B", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SapClientFactoryUsesMockOrdersOrExternalProgram()
    {
        var mockConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:MockOrders:FAUF-1:ArtNr"] = "4711",
            ["Sap:MockOrders:FAUF-1:Fz"] = "A"
        }).Build();
        var externalConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:Command"] = "sap-reader.exe"
        }).Build();

        var mock = Assert.IsType<MockSapClient>(SapClientFactory.Create(mockConfiguration));

        Assert.Equal(new OrderInfo("4711", "A"), await mock.GetOrderAsync("FAUF-1"));
        Assert.IsType<ExternalProgramSapClient>(SapClientFactory.Create(externalConfiguration));
    }

    private static async Task<TestDatabase> CreateReleasedPlanAsync()
    {
        var testDatabase = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        testDatabase.Context.TestPlans.Add(plan);
        testDatabase.Context.Stations.Add(new Station
        {
            Name = "Bench 1",
            Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "SimulatedSwitchMultimeter", ConnectionParametersJson = MeterParameters }]
        });
        await testDatabase.Context.SaveChangesAsync();
        var configuration = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", MeterParameters);
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
        {
            ["dmm1"] = new(configuration, new DriverRegistry().AddSimulatedDrivers().GetProfile(configuration))
        };
        await testDatabase.Plans.ReleaseAsync(plan.Id, new ExecutionPlanOptimizer().Optimize(plan, definitions));
        return testDatabase;
    }

    private sealed class ContinueInteraction : ISequenceInteraction
    {
        public Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
