using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Tests;

public sealed class DriversPhase2Tests
{
    [Theory]
    [InlineData("34921A", "agilent-34980a-34921a")]
    [InlineData("34937A", "agilent-34980a-34937a")]
    public void AgilentModuleProfilesPreserveUnknownsInsteadOfInventingSpecifications(string model, string expectedId)
    {
        var yaml = ReadEmbeddedProfile(model);
        var profile = DeviceProfileLoader.Load(yaml);
        Assert.Equal(expectedId, profile.Id);
        Assert.Contains(model, profile.Model, StringComparison.Ordinal);
        Assert.Null(profile.ChannelCount);
        Assert.Empty(profile.Channels);
        Assert.Contains("slot * 100 + channel", profile.ChannelNumbering, StringComparison.Ordinal);
        Assert.Contains(profile.Todos, todo => todo.Contains(DeviceProfile.DataSheetTodo, StringComparison.Ordinal));
        Assert.True(profile.DefaultChannelState.UnknownCommandInvalidatesAll);
        Assert.True(profile.DefaultChannelState.IsInvalidatedBy("unlisted-command", "range"));
        Assert.Empty(profile.DefaultChannelState.Settings);
    }

    [Fact]
    public void ProfileSchemaLoadsChannelRangesAndPerChannelStateOverrides()
    {
        const string yaml = """
            id: dmm
            model: Example DMM
            channelCount: 1
            channelNumbering: channel
            capabilities: [SwitchMultimeter]
            state:
              settings: [function, range, nplc]
              invalidatedByCommand:
                relay: [range]
              unknownCommandInvalidatesAll: true
            channels:
              - number: 1
                name: Input
                type: Voltage
                supportedFunctions: [VOLT:DC]
                valueRanges:
                  VOLT:DC: [0.1, 100.0]
                state:
                  settings: [function, range]
                  invalidatedByCommand:
                    relay: [function]
                  unknownCommandInvalidatesAll: false
            specialCases: []
            todos: []
            """;
        var profile = DeviceProfileLoader.Load(yaml);
        var channel = profile.GetChannel(1);
        Assert.Contains(DeviceCapability.SwitchMultimeter, profile.Capabilities);
        Assert.Equal(ChannelType.Voltage, channel.Type);
        Assert.Equal(new NumericRange(0.1, 100), channel.ValueRanges["VOLT:DC"]);
        Assert.True(channel.State.IsInvalidatedBy("relay", "function"));
        Assert.False(channel.State.IsInvalidatedBy("relay", "range"));
        Assert.False(channel.State.IsInvalidatedBy("unknown-command", "range"));
        Assert.True(channel.State.IsInvalidatedBy("relay", "nplc"));
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.GetChannel(2));
    }

    [Theory]
    [InlineData("channelCount: 0")]
    [InlineData("channelCount: 2")]
    public void InvalidProfileCountsAreRejected(string countLine)
    {
        var yaml = MinimalProfile.Replace("channelCount: 1", countLine, StringComparison.Ordinal);
        Assert.ThrowsAny<ArgumentException>(() => DeviceProfileLoader.Load(yaml));
    }

    [Fact]
    public void UnknownAndDuplicateYamlFieldsAreRejected()
    {
        Assert.ThrowsAny<Exception>(() => DeviceProfileLoader.Load(MinimalProfile + "\nunknownField: true\n"));
        Assert.ThrowsAny<Exception>(() => DeviceProfileLoader.Load(MinimalProfile + "\nid: duplicate\n"));
    }

    [Fact]
    public void InvalidRangesAndUnsupportedCapabilitiesAreRejected()
    {
        var invalidRange = MinimalProfile.Replace("[0.1, 10.0]", "[10.0, 0.1]", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => DeviceProfileLoader.Load(invalidRange));
        var invalidCapability = MinimalProfile.Replace("SwitchMultimeter", "NotARealCapability", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DeviceProfileLoader.Load(invalidCapability));
    }

    [Fact]
    public async Task ProfileFileLoaderReportsMissingAndOversizedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"testpilot-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.yaml");
        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => DeviceProfileLoader.LoadAsync(path));
            await File.WriteAllTextAsync(path, new string('x', 1_048_577));
            await Assert.ThrowsAsync<InvalidDataException>(() => DeviceProfileLoader.LoadAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ChannelStateInvalidatesOnlyDeclaredSettingsAndUnknownCommandsConservatively()
    {
        var state = new ChannelStateProfile(new HashSet<string>(["function", "range"], StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["relay"] = new HashSet<string>(["range"], StringComparer.Ordinal)
            }, UnknownCommandInvalidatesAll: true);
        Assert.True(state.IsInvalidatedBy("relay", "range"));
        Assert.False(state.IsInvalidatedBy("relay", "function"));
        Assert.True(state.IsInvalidatedBy("unknown", "function"));
        Assert.True(state.IsInvalidatedBy("relay", "nplc"));
    }

    [Fact]
    public async Task DriverRegistryRejectsDuplicatesAndUnknownNames()
    {
        var registry = new DriverRegistry().AddSimulatedDrivers();
        Assert.Equal(new[] { "SimulatedBarcodeScanner", "SimulatedFixture", "SimulatedPowerSupply", "SimulatedProgrammer", "SimulatedSwitchMultimeter" }, registry.RegisteredDrivers);
        Assert.Throws<InvalidOperationException>(() => registry.Register("SimulatedProgrammer", _ => throw new InvalidOperationException()));
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(registry, loggerFactory);
        await Assert.ThrowsAsync<DriverNotFoundException>(() => host.AcquireAsync(Configuration("Unavailable")).AsTask());
        var inconsistent = new DriverRegistry();
        inconsistent.Register("Mismatch", _ => new CountingInstrument(() => { }, () => { }, advertiseCapability: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DriverHost(inconsistent, loggerFactory)
            .AcquireAsync(Configuration("Mismatch")).AsTask());
    }

    [Fact]
    public void DriverConfigurationClonesJsonAndRequiresAnObject()
    {
        DriverConfiguration configuration;
        using (var json = JsonDocument.Parse("{\"readings\":{}}"))
            configuration = new DriverConfiguration("dmm1", "SimulatedSwitchMultimeter", json.RootElement);
        Assert.Equal(JsonValueKind.Object, configuration.ConnectionParameters.ValueKind);
        Assert.Throws<ArgumentException>(() => DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", "[]"));
        using var invalidOptions = JsonDocument.Parse("{}");
        Assert.Throws<ArgumentException>(() => new DriverConfiguration(" ", "Driver", invalidOptions.RootElement));
    }

    [Fact]
    public async Task SimulatedSwitchMultimeterReturnsDeterministicMeasurementsAndSafeState()
    {
        var configuration = DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter",
            "{\"readings\":{\"1|VOLT:DC\":3.298}}");
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(new DriverRegistry().AddSimulatedDrivers(), loggerFactory);
        await using var lease = await host.AcquireAsync(configuration);
        var dmm = lease.GetCapability<ISwitchMultimeter>();
        Assert.Equal("TESTPILOT,SIMULATED-DMM,1.0", await dmm.IdentifyAsync());
        Assert.True((await dmm.SelfCheckAsync()).Passed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dmm.MeasureAsync(1));
        await dmm.ConfigureMeasurementAsync(new MeasurementConfiguration(1, "VOLT:DC", 10, 1));
        Assert.Equal(3.298, await dmm.MeasureAsync(1));
        Assert.Equal(3.298, await dmm.MeasureAsync(1));
        // Like the real 34980A the configuration stays per channel; whether a relay command requires
        // reconfiguration is decided by the optimizer from the device profile, not by the driver.
        await dmm.SetRelayAsync(1, true);
        Assert.Equal(3.298, await dmm.MeasureAsync(1));
        await dmm.SafeStateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => dmm.MeasureAsync(1));
    }

    [Theory]
    [InlineData("{\"readings\":{\"1|VOLT:DC\":\"3.3\"}}")]
    [InlineData("{\"readings\":{\"1|VOLT:DC\":1},\"unknown\":true}")]
    [InlineData("{\"readings\":{\"1|VOLT:DC\":1,\"1|VOLT:DC\":2}}")]
    [InlineData("{\"readings\":null}")]
    [InlineData("{\"readings\":{\"typo\":1}}")]
    [InlineData("{\"readings\":{\"2|VOLT:DC\":1}}")]
    public async Task SimulatedConfigurationRejectsInvalidJsonOptions(string json)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(new DriverRegistry().AddSimulatedDrivers(), loggerFactory);
        await Assert.ThrowsAsync<JsonException>(() => host.AcquireAsync(
            DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", json)).AsTask());
    }

    [Fact]
    public async Task SimulatedMeasurementAndSupplyRejectInvalidRequests()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(new DriverRegistry().AddSimulatedDrivers(), loggerFactory);
        await using (var dmmLease = await host.AcquireAsync(Configuration("SimulatedSwitchMultimeter", "validation-dmm")))
        {
            var dmm = dmmLease.GetCapability<ISwitchMultimeter>();
            await Assert.ThrowsAsync<ArgumentException>(() => dmm.ConfigureMeasurementAsync(new MeasurementConfiguration(1, "UNKNOWN")));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dmm.ConfigureMeasurementAsync(new MeasurementConfiguration(1, "VOLT:DC", 0)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dmm.SetRelayAsync(2, true));
        }

        await Assert.ThrowsAsync<JsonException>(() => host.AcquireAsync(
            DriverConfiguration.Parse("psu1", "SimulatedPowerSupply", "{\"typo\":true}")).AsTask());
        await Assert.ThrowsAsync<JsonException>(() => host.AcquireAsync(
            DriverConfiguration.Parse("programmer", "SimulatedProgrammer", "{\"typo\":true}")).AsTask());
        await using var supplyLease = await host.AcquireAsync(Configuration("SimulatedPowerSupply", "validation-psu"));
        var supply = supplyLease.GetCapability<IPowerSupply>();
        await supply.SetOutputAsync(1, 3.3, 0.5, true);
        Assert.Equal((3.3, 0.5, true), await supply.GetOutputAsync(1));
        await supply.SafeStateAsync();
        Assert.Equal((0, 0, false), await supply.GetOutputAsync(1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.SetOutputAsync(1, 3.3, 0, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.SetOutputAsync(2, 3.3, 0.5, true));
    }

    [Fact]
    public async Task SimulatedPowerSupplyKeepsStatePerConfiguredOutput()
    {
        var host = new DriverHost(new DriverRegistry().AddSimulatedDrivers(), NullLoggerFactory.Instance);
        await using var lease = await host.AcquireAsync(
            DriverConfiguration.Parse("psu-multi", "SimulatedPowerSupply", "{\"channels\":[1,2,3]}"));
        var supply = lease.GetCapability<IPowerSupply>();
        Assert.All(lease.Instrument.Profile.Channels, channel => Assert.Equal(ChannelType.PowerOutput, channel.Type));

        await supply.SetOutputAsync(1, 5, 1, true);
        await supply.SetOutputAsync(3, 12, 0.2, false);

        Assert.Equal((5, 1, true), await supply.GetOutputAsync(1));
        Assert.Equal((0, 0, false), await supply.GetOutputAsync(2));
        Assert.Equal((12, 0.2, false), await supply.GetOutputAsync(3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.GetOutputAsync(4));
        await Assert.ThrowsAsync<JsonException>(() => host.AcquireAsync(
            DriverConfiguration.Parse("psu-dup", "SimulatedPowerSupply", "{\"channels\":[1,1]}")).AsTask());
    }

    [Fact]
    public async Task SimulatedProgrammerAndBarcodeScannerAreUsableWithoutHardware()
    {
        var provider = new CaptureLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var host = new DriverHost(new DriverRegistry().AddSimulatedDrivers(), loggerFactory);
        await using (var programmerLease = await host.AcquireAsync(Configuration("SimulatedProgrammer", "programmer")))
        {
            var programmer = programmerLease.GetCapability<IProgrammer>();
            await programmer.ProgramAsync("firmware.hex");
            Assert.Equal("TESTPILOT,SIMULATED-PROGRAMMER,1.0", await programmerLease.Instrument.IdentifyAsync());
            await programmer.SafeStateAsync();
        }
        Assert.Contains(provider.Entries, entry => entry.Message.Contains("PROGRAM file=firmware.hex", StringComparison.Ordinal)
            && entry.Message.Contains("returned OK", StringComparison.Ordinal));

        await using (var scannerLease = await host.AcquireAsync(DriverConfiguration.Parse("scanner", "SimulatedBarcodeScanner", "{\"barcode\":\"ABC-123\"}")))
            Assert.Equal("ABC-123", await scannerLease.GetCapability<IBarcodeScanner>().ScanAsync());
        Assert.Contains(provider.Entries, entry => entry.Message.Contains("SCAN?", StringComparison.Ordinal)
            && entry.Message.Contains("ABC-123", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => host.AcquireAsync(
            DriverConfiguration.Parse("scanner-invalid", "SimulatedBarcodeScanner", "{\"barcode\":\" \"}")).AsTask());
    }

    [Fact]
    public async Task HostSerializesAccessForSameDeviceAndExposesOnlyRealCapabilities()
    {
        var active = 0;
        var maximumActive = 0;
        var created = 0;
        var registry = new DriverRegistry();
        registry.Register("Counting", _ =>
        {
            Interlocked.Increment(ref created);
            return new CountingInstrument(() =>
            {
                var current = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumActive, current);
            }, () => Interlocked.Decrement(ref active));
        });
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(registry, loggerFactory);
        var otherHost = new DriverHost(registry, loggerFactory);
        var first = await host.AcquireAsync(Configuration("Counting", "counting-host"));
        var waiting = otherHost.AcquireAsync(Configuration("Counting", "counting-host")).AsTask();
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, created);
        Assert.Throws<NotSupportedException>(() => first.GetCapability<IPowerSupply>());
        Assert.IsAssignableFrom<ISwitchMultimeter>(first.GetCapability<ISwitchMultimeter>());
        await first.DisposeAsync();
        await first.DisposeAsync();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await second.DisposeAsync();
        Assert.Equal(2, created);
        Assert.Equal(1, maximumActive);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task HostReleasesDeviceLockWhenCreationOrConnectionFails()
    {
        var attempts = 0;
        var registry = new DriverRegistry();
        registry.Register("Flaky", _ => new CountingInstrument(
            () => { if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("Connect failed."); },
            () => { }));
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(registry, loggerFactory);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.AcquireAsync(Configuration("Flaky", "flaky-host")));
        await using var lease = await host.AcquireAsync(Configuration("Flaky", "flaky-host")).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task HostCancellationWhileWaitingDoesNotReleaseActiveLease()
    {
        var registry = new DriverRegistry().AddSimulatedDrivers();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(registry, loggerFactory);
        await using var first = await host.AcquireAsync(Configuration("SimulatedSwitchMultimeter", "cancel-dmm"));
        using var cancellation = new CancellationTokenSource();
        var waiting = host.AcquireAsync(Configuration("SimulatedSwitchMultimeter", "cancel-dmm"), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await using var secondDevice = await host.AcquireAsync(Configuration("SimulatedPowerSupply", "cancel-psu"));
        Assert.Equal("cancel-dmm", first.DeviceKey);
        Assert.Equal("cancel-psu", secondDevice.DeviceKey);
    }

    [Fact]
    public async Task HostTraceLogsCommandsResponsesDurationsAndFailures()
    {
        var provider = new CaptureLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        var host = new DriverHost(new DriverRegistry().AddSimulatedDrivers(), loggerFactory);
        await using var lease = await host.AcquireAsync(Configuration("SimulatedSwitchMultimeter", "trace-dmm"));
        Assert.Equal("TESTPILOT,SIMULATED-DMM,1.0", await lease.Instrument.IdentifyAsync());
        var dmm = lease.GetCapability<ISwitchMultimeter>();
        await dmm.ConfigureMeasurementAsync(new MeasurementConfiguration(1, "VOLT:DC"));
        Assert.Equal(0, await dmm.MeasureAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => dmm.MeasureAsync(2));
        var entries = provider.Entries.ToArray();
        Assert.Contains(entries, entry => entry.Message.Contains("IDENTIFY", StringComparison.Ordinal)
            && entry.Message.Contains("TESTPILOT,SIMULATED-DMM,1.0", StringComparison.Ordinal)
            && entry.Message.Contains("ms", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("MEASURE:CONFIGURE", StringComparison.Ordinal)
            && entry.Message.Contains("returned OK", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Level == LogLevel.Error
            && entry.Message.Contains("MEASURE? channel=2", StringComparison.Ordinal)
            && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task DifferentDevicesCanBeLeasedConcurrently()
    {
        var registry = new DriverRegistry().AddSimulatedDrivers();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var host = new DriverHost(registry, loggerFactory);
        await using var dmm = await host.AcquireAsync(Configuration("SimulatedSwitchMultimeter", "parallel-dmm"));
        await using var power = await host.AcquireAsync(Configuration("SimulatedPowerSupply", "parallel-psu"));
        Assert.Equal("parallel-dmm", dmm.DeviceKey);
        Assert.Equal("parallel-psu", power.DeviceKey);
    }

    private static string ReadEmbeddedProfile(string model)
    {
        Assert.Contains($"{model}.yaml", EmbeddedProfiles.FileNames);
        return EmbeddedProfiles.ReadText($"{model}.yaml");
    }

    private static DriverConfiguration Configuration(string driver, string key = "dmm1") =>
        DriverConfiguration.Parse(key, driver, "{}");

    private const string MinimalProfile = """
        id: dmm
        model: Example DMM
        channelCount: 1
        channelNumbering: channel
        capabilities: [SwitchMultimeter]
        state:
          settings: [function, range]
          invalidatedByCommand:
            relay: [range]
          unknownCommandInvalidatesAll: true
        channels:
          - number: 1
            name: Input
            type: Voltage
            supportedFunctions: [VOLT:DC]
            valueRanges:
              VOLT:DC: [0.1, 10.0]
        specialCases: []
        todos: []
        """;

    private sealed class CountingInstrument(Action connected, Action disconnected, bool advertiseCapability = true) : ISwitchMultimeter
    {
        private bool isConnected;
        public DeviceProfile Profile { get; } = SimulatedProfilesForTest.Create(advertiseCapability);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            connected();
            isConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isConnected) disconnected();
            isConnected = false;
            return Task.CompletedTask;
        }

        public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("COUNTING");
        public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SelfCheckResult(true, "ok"));
        public Task SafeStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConfigureMeasurementAsync(MeasurementConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<double> MeasureAsync(int channel, CancellationToken cancellationToken = default) => Task.FromResult(0d);
        public Task SetRelayAsync(int channel, bool closed, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static class SimulatedProfilesForTest
    {
        internal static DeviceProfile Create(bool advertiseCapability)
        {
            var state = new ChannelStateProfile(new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), UnknownCommandInvalidatesAll: true);
            var channel = new ChannelProfile(1, "Input", ChannelType.GeneralPurpose,
                new HashSet<string>(["VOLT:DC"], StringComparer.Ordinal),
                new Dictionary<string, NumericRange>(StringComparer.Ordinal), state);
            var capabilities = advertiseCapability
                ? new HashSet<DeviceCapability> { DeviceCapability.SwitchMultimeter }
                : new HashSet<DeviceCapability>();
            return new DeviceProfile("counting", "Counting instrument", 1, "channel",
                capabilities, state, [channel], [], []);
        }
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> entries = new();
        public IEnumerable<CapturedLog> Entries => entries;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(entries);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception), exception));
    }

    private sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);
}

internal static class InterlockedExtensions
{
    internal static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
