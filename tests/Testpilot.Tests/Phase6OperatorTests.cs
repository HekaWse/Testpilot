using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Operator;
using Testpilot.Planning;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class Phase6OperatorTests
{
    private const string MeterParameters = "{\"channels\":[1003],\"readings\":{\"1003|VOLT:DC\":3.3}}";

    public Phase6OperatorTests()
    {
        // Without a synchronization context the view model runs its UI updates directly (no window in tests).
        SynchronizationContext.SetSynchronizationContext(null);
    }

    [Fact]
    public async Task OrderIsLoadedAndRunShowsPassWithFormattedValue()
    {
        await using var bench = await OperatorBench.CreateAsync();
        var viewModel = await bench.CreateViewModelAsync();

        viewModel.Fauf = "FAUF-1";
        await viewModel.LoadOrderAsync();

        Assert.Null(viewModel.ErrorText);
        Assert.Contains("Artikel 4711 · FZ A · Prüfplan Version 1", viewModel.OrderText, StringComparison.Ordinal);
        var row = Assert.Single(viewModel.Steps);
        Assert.Equal("3,2 V", row.LowText);
        Assert.Equal("3,4 V", row.HighText);
        Assert.True(viewModel.StartCommand.CanExecute(null));

        viewModel.OperatorName = "Bediener";
        viewModel.SerialNumber = "1234567890";
        await viewModel.StartAsync();

        Assert.Null(viewModel.ErrorText);
        Assert.Equal(RunDisplayState.Pass, viewModel.ResultState);
        Assert.Equal("i.O.", viewModel.ResultText);
        Assert.Equal(StepRowState.Pass, viewModel.Steps.Single().State);
        Assert.Equal("3,3 V", viewModel.Steps.Single().ValueText);
        Assert.Equal("", viewModel.SerialNumber);
        Assert.True(File.Exists(viewModel.ExportPath));
        Assert.Single(Directory.GetFiles(bench.LogDirectory, "run-*-1234567890.log"));
        await using var database = bench.Database.NewContext();
        var run = Assert.Single(database.Runs);
        Assert.Equal(("FAUF-1", "1234567890", "Bench 1", "Bediener", RunOutcome.Pass),
            (run.Fauf, run.SerialNumber, run.Station, run.Operator, run.Result));
    }

    [Fact]
    public async Task StartupShowsTheSystemConfigurationReadFromTheDatabase()
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true);
        var viewModel = await bench.CreateViewModelAsync();

        Assert.Null(viewModel.ErrorText);
        Assert.Null(viewModel.SystemTypeText);
        Assert.Contains("Prüfsystem Bench 1 (ohne Prüfsystemtyp)", viewModel.SystemConfigurationText, StringComparison.Ordinal);
        Assert.Contains("Ps05Fixture @ GPIB0::9::INSTR", viewModel.SystemConfigurationText, StringComparison.Ordinal);
        Assert.Contains("SensopartTcp @ 192.0.2.10", viewModel.SystemConfigurationText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SN-1")]
    [InlineData("123456789")]
    [InlineData("12345678901")]
    public async Task InvalidSerialNumberDoesNotStartARun(string serial)
    {
        await using var bench = await OperatorBench.CreateAsync();
        var viewModel = await bench.CreateViewModelAsync();
        viewModel.Fauf = "FAUF-1";
        await viewModel.LoadOrderAsync();
        viewModel.OperatorName = "Bediener";
        viewModel.SerialNumber = serial;

        await viewModel.StartAsync();

        Assert.Contains("genau 10 Ziffern", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.Equal(RunDisplayState.None, viewModel.ResultState);
        await using var database = bench.Database.NewContext();
        Assert.Empty(database.Runs);
    }

    [Fact]
    public async Task MissingOperatorDoesNotStartARun()
    {
        await using var bench = await OperatorBench.CreateAsync();
        var viewModel = await bench.CreateViewModelAsync();
        viewModel.Fauf = "FAUF-1";
        await viewModel.LoadOrderAsync();
        viewModel.SerialNumber = "1234567890";

        await viewModel.StartAsync();

        Assert.Equal("Bitte Bediener eintragen.", viewModel.ErrorText);
        Assert.Equal(RunDisplayState.None, viewModel.ResultState);
    }

    [Fact]
    public async Task UnknownFaufShowsGermanSapMessage()
    {
        await using var bench = await OperatorBench.CreateAsync();
        var viewModel = await bench.CreateViewModelAsync();

        viewModel.Fauf = "FAUF-X";
        await viewModel.LoadOrderAsync();

        Assert.Equal("FAUF 'FAUF-X' wurde in SAP nicht gefunden.", viewModel.ErrorText);
        Assert.Null(viewModel.PreparedRun);
        Assert.Empty(viewModel.Steps);
        Assert.False(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingTheFaufClearsTheLoadedOrder()
    {
        await using var bench = await OperatorBench.CreateAsync();
        var viewModel = await bench.CreateViewModelAsync();
        viewModel.Fauf = "FAUF-1";
        await viewModel.LoadOrderAsync();

        viewModel.Fauf = "FAUF-2";

        Assert.Null(viewModel.PreparedRun);
        Assert.Null(viewModel.OrderText);
        Assert.False(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task SetupCheckReportsAnUnknownStationAndDisablesInputs()
    {
        await using var bench = await OperatorBench.CreateAsync(station: "Bench 9");
        var viewModel = await bench.CreateViewModelAsync();

        Assert.Contains("Prüfsystem 'Bench 9' ist in der Datenbank nicht angelegt", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.LoadOrderCommand.CanExecute(null));
    }

    [Fact]
    public async Task SingleStepAndOperatorPromptAreAnsweredInTheWindow()
    {
        await using var bench = await OperatorBench.CreateAsync(withPrompt: true);
        var viewModel = await bench.CreateViewModelAsync();
        viewModel.Fauf = "FAUF-1";
        await viewModel.LoadOrderAsync();
        viewModel.OperatorName = "Bediener";
        viewModel.SerialNumber = "1234567890";
        viewModel.SingleStepMode = true;

        var run = viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.IsWaitingForContinue);
        Assert.Contains("Schritt 1 „VCC_3V3“", viewModel.ContinueText, StringComparison.Ordinal);
        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.CanEdit);
        viewModel.ContinueCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.IsWaitingForContinue && viewModel.Steps[0].State == StepRowState.Pass);
        Assert.Equal("3,3 V", viewModel.Steps[0].ValueText);
        viewModel.ContinueCommand.Execute(null);

        await WaitUntilAsync(() => viewModel.IsPromptVisible);
        Assert.Equal("LED leuchtet grün?", viewModel.PromptMessage);
        viewModel.NoCommand.Execute(null);
        await run;

        Assert.Equal(RunDisplayState.Fail, viewModel.ResultState);
        Assert.Equal(StepRowState.Fail, viewModel.Steps[1].State);
        Assert.Equal("Nein", viewModel.Steps[1].ValueText);
        Assert.False(viewModel.IsPromptVisible);
        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.CanEdit);
    }

    [Fact]
    public async Task CancelWhileWaitingAbortsTheRun()
    {
        await using var bench = await OperatorBench.CreateAsync();
        var viewModel = await bench.CreateViewModelAsync();
        viewModel.Fauf = "FAUF-1";
        await viewModel.LoadOrderAsync();
        viewModel.OperatorName = "Bediener";
        viewModel.SerialNumber = "1234567890";
        viewModel.SingleStepMode = true;
        var finished = 0;
        viewModel.RunFinished += (_, _) => finished++;

        var run = viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.IsWaitingForContinue);
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        viewModel.CancelCommand.Execute(null);
        await run;

        Assert.Equal(RunDisplayState.Aborted, viewModel.ResultState);
        Assert.Equal(StepRowState.Skipped, viewModel.Steps[0].State);
        Assert.False(viewModel.IsWaitingForContinue);
        Assert.Equal(1, finished);
        Assert.Equal("1234567890", viewModel.SerialNumber);
        await using var database = bench.Database.NewContext();
        Assert.Equal(RunOutcome.Aborted, Assert.Single(database.Runs).Result);
    }

    [Fact]
    public async Task OperatorPromptIsNotLimitedByTheDeviceTimeout()
    {
        await using var bench = await OperatorBench.CreateAsync(withPrompt: true, operationTimeout: TimeSpan.FromMilliseconds(200));
        var viewModel = await bench.CreateViewModelAsync();
        viewModel.Fauf = "FAUF-1";
        await viewModel.LoadOrderAsync();
        viewModel.OperatorName = "Bediener";
        viewModel.SerialNumber = "1234567890";

        var run = viewModel.StartAsync();
        await WaitUntilAsync(() => viewModel.IsPromptVisible);
        await Task.Delay(500);
        viewModel.YesCommand.Execute(null);
        await run;

        Assert.Equal(RunDisplayState.Pass, viewModel.ResultState);
        Assert.Equal("Ja", viewModel.Steps[1].ValueText);
    }

    [Fact]
    public void SettingsRequireAStationAndDefaultToRealHardware()
    {
        var missingStation = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var minimal = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operator:Station"] = " Bench 1 "
        }).Build();
        var invalidPattern = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operator:Station"] = "Bench 1",
            ["Operator:SerialNumberPattern"] = "[0-9"
        }).Build();

        Assert.Contains("Operator:Station", Assert.Throws<InvalidOperationException>(
            () => OperatorSettings.FromConfiguration(missingStation)).Message, StringComparison.Ordinal);
        var settings = OperatorSettings.FromConfiguration(minimal);
        Assert.Equal("Bench 1", settings.Station);
        Assert.False(settings.SimulationEnabled);
        Assert.False(settings.DisableTimeouts);
        Assert.Equal(OperatorSettings.DefaultSerialNumberPattern, settings.SerialNumberPattern);
        Assert.True(settings.IsValidSerialNumber("0123456789"));
        Assert.False(settings.IsValidSerialNumber("０１２３４５６７８９"));
        Assert.Throws<InvalidOperationException>(() => OperatorSettings.FromConfiguration(invalidPattern));
    }

    [Theory]
    [InlineData(0.0033, MeasurementUnit.Volt, "3,3 mV")]
    [InlineData(3.298, MeasurementUnit.Volt, "3,298 V")]
    [InlineData(4700, MeasurementUnit.Ohm, "4,7 kΩ")]
    [InlineData(2.2e6, MeasurementUnit.Ohm, "2,2 MΩ")]
    [InlineData(0, MeasurementUnit.Volt, "0 V")]
    [InlineData(1.5e-7, MeasurementUnit.Ampere, "150 nA")]
    [InlineData(-0.012, MeasurementUnit.Volt, "-12 mV")]
    [InlineData(25e-6, MeasurementUnit.Ampere, "25 µA")]
    public void FormatAutoChoosesThePrefixForDisplay(double value, MeasurementUnit unit, string expected)
    {
        Assert.Equal(expected, Units.FormatAuto(value, unit, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void FormatAutoRejectsNonFiniteValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Units.FormatAuto(double.NaN, MeasurementUnit.Volt));
    }

    [Fact]
    public async Task ClosingTheAdapterReadsTheSerialNumberRunsAndReleasesWithPassLamp()
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true);
        var viewModel = await bench.CreateReadyViewModelAsync();
        Assert.True(viewModel.IsAutoStart);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        // Startup released the adapter into a defined state.
        Assert.Equal(new SimulatedFixtureState(false, false, FixtureLamp.None), SimulatedFixtures.GetState(bench.FixtureKey));

        await viewModel.PollFixtureOnceAsync();
        Assert.Equal("Adapter offen", viewModel.FixtureStateText);
        viewModel.SimulateFixtureCloseCommand.Execute(null);
        await viewModel.PollFixtureOnceAsync();

        Assert.Null(viewModel.ErrorText);
        Assert.Equal(RunDisplayState.Pass, viewModel.ResultState);
        Assert.Equal("", viewModel.SerialNumber);
        Assert.Equal(new SimulatedFixtureState(false, false, FixtureLamp.Pass), SimulatedFixtures.GetState(bench.FixtureKey));
        Assert.Contains("Adapter schließen", viewModel.StatusText, StringComparison.Ordinal);
        await using var database = bench.Database.NewContext();
        var run = Assert.Single(database.Runs);
        Assert.Equal((OperatorSettings.DefaultSimulatedSerialNumber, RunOutcome.Pass), (run.SerialNumber, run.Result));
    }

    [Fact]
    public async Task ReadFailureReleasesTheAdapterAndTheHandScannedNumberIsUsedOnTheNextClose()
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true);
        var viewModel = await bench.CreateReadyViewModelAsync();
        viewModel.SimulateReadFailure = true;

        await viewModel.PollFixtureOnceAsync();
        SimulatedFixtures.Close(bench.FixtureKey);
        await viewModel.PollFixtureOnceAsync();

        Assert.Contains("Handscanner", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.Equal(RunDisplayState.None, viewModel.ResultState);
        Assert.Equal(new SimulatedFixtureState(false, false, FixtureLamp.None), SimulatedFixtures.GetState(bench.FixtureKey));
        await using (var database = bench.Database.NewContext())
            Assert.Empty(database.Runs);

        // The hand scanner types like a keyboard and ends with Enter.
        viewModel.SerialNumber = "0987654321";
        viewModel.SerialEnteredCommand.Execute(null);
        Assert.Null(viewModel.ErrorText);
        await viewModel.PollFixtureOnceAsync();
        SimulatedFixtures.Close(bench.FixtureKey);
        await viewModel.PollFixtureOnceAsync();

        Assert.Equal(RunDisplayState.Pass, viewModel.ResultState);
        await using (var database = bench.Database.NewContext())
            Assert.Equal("0987654321", Assert.Single(database.Runs).SerialNumber);
    }

    [Fact]
    public async Task FailedReleaseIsRetriedUntilTheAdapterIsOpen()
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true);
        var viewModel = await bench.CreateReadyViewModelAsync();

        await viewModel.PollFixtureOnceAsync();
        SimulatedFixtures.FailNextRelease(bench.FixtureKey);
        SimulatedFixtures.Close(bench.FixtureKey);
        await viewModel.PollFixtureOnceAsync();

        var state = SimulatedFixtures.GetState(bench.FixtureKey);
        Assert.True(state.Closed && state.Locked);
        Assert.Contains("Adapter konnte nicht geöffnet werden", viewModel.ErrorText, StringComparison.Ordinal);

        await viewModel.PollFixtureOnceAsync();

        Assert.Equal(new SimulatedFixtureState(false, false, FixtureLamp.Pass), SimulatedFixtures.GetState(bench.FixtureKey));
        Assert.Null(viewModel.ErrorText);
        Assert.Equal("Adapter offen", viewModel.FixtureStateText);
        await using var database = bench.Database.NewContext();
        Assert.Single(database.Runs);
    }

    [Fact]
    public async Task AdapterThatWasNeverSeenOpenDoesNotStartARun()
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true);
        var viewModel = await bench.CreateReadyViewModelAsync();

        SimulatedFixtures.Close(bench.FixtureKey);
        await viewModel.PollFixtureOnceAsync();
        await viewModel.PollFixtureOnceAsync();

        Assert.Equal(RunDisplayState.None, viewModel.ResultState);
        Assert.True(SimulatedFixtures.GetState(bench.FixtureKey).Closed);
        await using var database = bench.Database.NewContext();
        Assert.Empty(database.Runs);
    }

    [Fact]
    public async Task ClosingWithoutOperatorReleasesTheAdapterWithAHint()
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true);
        var viewModel = await bench.CreateReadyViewModelAsync();
        viewModel.OperatorName = "";

        await viewModel.PollFixtureOnceAsync();
        SimulatedFixtures.Close(bench.FixtureKey);
        await viewModel.PollFixtureOnceAsync();

        Assert.Contains("Bediener", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.False(SimulatedFixtures.GetState(bench.FixtureKey).Closed);
        await using var database = bench.Database.NewContext();
        Assert.Empty(database.Runs);
    }

    [Fact]
    public async Task WithoutCodeReaderTheSerialNumberMustBeScannedBeforeClosing()
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true, withReader: false);
        var viewModel = await bench.CreateReadyViewModelAsync();

        await viewModel.PollFixtureOnceAsync();
        SimulatedFixtures.Close(bench.FixtureKey);
        await viewModel.PollFixtureOnceAsync();
        Assert.Contains("Seriennummer scannen", viewModel.ErrorText, StringComparison.Ordinal);

        viewModel.SerialNumber = "1112223334";
        viewModel.SerialEnteredCommand.Execute(null);
        await viewModel.PollFixtureOnceAsync();
        SimulatedFixtures.Close(bench.FixtureKey);
        await viewModel.PollFixtureOnceAsync();

        Assert.Equal(RunDisplayState.Pass, viewModel.ResultState);
        await using var database = bench.Database.NewContext();
        Assert.Equal("1112223334", Assert.Single(database.Runs).SerialNumber);
    }

    [Theory]
    [InlineData("missing-device", "nicht eingetragen")]
    [InlineData("dmm1", "Fähigkeit TestFixture")]
    public async Task InvalidFixtureDeviceIsASetupError(string fixtureDevice, string expected)
    {
        await using var bench = await OperatorBench.CreateAsync(withFixture: true, fixtureDevice: fixtureDevice);
        var viewModel = await bench.CreateViewModelAsync();

        Assert.Contains("Operator:FixtureDevice", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.Contains(expected, viewModel.ErrorText, StringComparison.Ordinal);
        Assert.False(viewModel.CanEdit);
    }

    [Fact]
    public void FixtureSettingsAreOptionalAndValidated()
    {
        static IConfiguration Build(params (string Key, string Value)[] values) => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value))
                .Append(new("Operator:Station", "Bench 1")))
            .Build();

        var manual = OperatorSettings.FromConfiguration(Build(("Operator:FixtureDevice", " ")));
        Assert.False(manual.IsAutoStart);
        Assert.Equal(TimeSpan.FromMilliseconds(500), manual.FixtureSettleTime);

        var automatic = OperatorSettings.FromConfiguration(Build(("Operator:FixtureDevice", "adapter"),
            ("Operator:SerialReaderDevice", "reader"), ("Operator:FixtureSettleMilliseconds", "250"),
            ("Simulation:SerialNumber", "5555555555")));
        Assert.True(automatic.IsAutoStart);
        Assert.Equal(("adapter", "reader", TimeSpan.FromMilliseconds(250), "5555555555"),
            (automatic.FixtureDevice, automatic.SerialReaderDevice, automatic.FixtureSettleTime, automatic.SimulatedSerialNumber));

        Assert.Throws<InvalidOperationException>(() => OperatorSettings.FromConfiguration(Build(("Operator:SerialReaderDevice", "reader"))));
        Assert.Throws<InvalidOperationException>(() => OperatorSettings.FromConfiguration(Build(("Operator:FixtureSettleMilliseconds", "-1"))));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not reached in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// In-memory database with station "Bench 1", a released plan for 4711/A and the SAP mock order FAUF-1.
    /// With <c>withFixture</c> the station also has a simulated test adapter and code reader (unique device keys,
    /// because device locks and the simulated adapter state are process-wide).
    /// </summary>
    private sealed class OperatorBench : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"testpilot-operator-{Guid.NewGuid():N}");
        private readonly List<MainViewModel> viewModels = [];
        private ServiceProvider services = null!;
        private OperatorSettings settings = null!;

        public TestDatabase Database { get; private set; } = null!;
        public string LogDirectory => Path.Combine(directory, "logs");
        public string FixtureKey { get; } = $"fixture-{Guid.NewGuid():N}";
        public string ReaderKey { get; } = $"reader-{Guid.NewGuid():N}";

        public static async Task<OperatorBench> CreateAsync(string station = "Bench 1", bool withPrompt = false,
            TimeSpan? operationTimeout = null, bool withFixture = false, bool withReader = true,
            string? fixtureDevice = null)
        {
            var bench = new OperatorBench { Database = await TestDatabase.CreateAsync() };
            var plan = TestDatabase.Draft();
            if (withPrompt)
                plan.Steps.Add(new TestStep
                {
                    Order = 1, Name = "LED prüfen", StepType = StepType.OperatorPrompt,
                    ParametersJson = StepParameters.Serialize(new OperatorPromptParameters("LED leuchtet grün?"))
                });
            var context = bench.Database.Context;
            context.TestPlans.Add(plan);
            var benchStation = new Station
            {
                Name = "Bench 1",
                Devices = [new StationDevice { DeviceKey = "dmm1", Driver = "SimulatedSwitchMultimeter", ConnectionParametersJson = MeterParameters }]
            };
            if (withFixture)
            {
                benchStation.Devices.Add(new StationDevice { DeviceKey = bench.FixtureKey, Driver = "Ps05Fixture", ConnectionParametersJson = "{\"resource\":\"GPIB0::9::INSTR\"}" });
                benchStation.Devices.Add(new StationDevice { DeviceKey = bench.ReaderKey, Driver = "SensopartTcp", ConnectionParametersJson = "{\"host\":\"192.0.2.10\"}" });
            }
            context.Stations.Add(benchStation);
            await context.SaveChangesAsync();
            var configuration = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", MeterParameters);
            var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal)
            {
                ["dmm1"] = new(configuration, new DriverRegistry().AddSimulatedDrivers().GetProfile(configuration))
            };
            await bench.Database.Plans.ReleaseAsync(plan.Id, new ExecutionPlanOptimizer().Optimize(plan, definitions));

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddScoped<TestpilotDbContext>(_ => bench.Database.NewContext());
            bench.services = serviceCollection.BuildServiceProvider();
            bench.settings = new OperatorSettings(station, SimulationEnabled: true, DisableTimeouts: false,
                operationTimeout ?? TimeSpan.FromSeconds(5), Path.Combine(bench.directory, "exports"),
                bench.LogDirectory, OperatorSettings.DefaultSerialNumberPattern)
            {
                FixtureDevice = withFixture ? fixtureDevice ?? bench.FixtureKey : null,
                SerialReaderDevice = withFixture && withReader ? bench.ReaderKey : null,
                FixtureSettleTime = TimeSpan.Zero
            };
            return bench;
        }

        public async Task<MainViewModel> CreateViewModelAsync()
        {
            var service = new OperatorRunService(services.GetRequiredService<IServiceScopeFactory>(),
                new MockSapClient([new("FAUF-1", new OrderInfo("4711", "A"))]), new DriverRegistry().AddAllDrivers(),
                settings, new CustomStepRegistry(), NullLoggerFactory.Instance);
            var viewModel = new MainViewModel(service, "test");
            viewModels.Add(viewModel);
            await viewModel.InitializeAsync();
            return viewModel;
        }

        /// <summary>FAUF loaded and operator entered: the next adapter close starts the run.</summary>
        public async Task<MainViewModel> CreateReadyViewModelAsync()
        {
            var viewModel = await CreateViewModelAsync();
            viewModel.Fauf = "FAUF-1";
            await viewModel.LoadOrderAsync();
            viewModel.OperatorName = "Bediener";
            return viewModel;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var viewModel in viewModels)
                await viewModel.ShutdownAsync();
            await services.DisposeAsync();
            await Database.DisposeAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
