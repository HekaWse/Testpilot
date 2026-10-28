using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

internal sealed class SimulatedPowerSupply : IPowerSupply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };
    private readonly Dictionary<int, (double Voltage, double CurrentLimit, bool Enabled)> outputs = [];
    private bool connected;

    public SimulatedPowerSupply(DriverConfiguration driverConfiguration)
    {
        ArgumentNullException.ThrowIfNull(driverConfiguration);
        var options = JsonSerializer.Deserialize<SimulationOptions>(driverConfiguration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("Simulation options must be a JSON object.");
        if (options.Channels is not null && options.Channels.Distinct().Count() != options.Channels.Length)
            throw new JsonException("Simulated channel numbers must be unique.");
        Profile = SimulatedProfiles.Create("simulated-power-supply", "Simulated power supply",
            DeviceCapability.PowerSupply, options.Channels);
    }

    public DeviceProfile Profile { get; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connected) throw new InvalidOperationException("The simulated instrument is already connected.");
        connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        outputs.Clear();
        connected = false;
        return Task.CompletedTask;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult("TESTPILOT,SIMULATED-PSU,1.0");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(new SelfCheckResult(true, "Simulated self-check passed."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        outputs.Clear();
        return Task.CompletedTask;
    }

    public Task SetOutputAsync(int channel, double voltage, double currentLimit, bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        PowerOutputLimits.Validate(Profile, channel, voltage, currentLimit);
        outputs[channel] = (voltage, currentLimit, enabled);
        return Task.CompletedTask;
    }

    public Task<(double Voltage, double CurrentLimit, bool Enabled)> GetOutputAsync(int channel,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        _ = Profile.GetChannel(channel);
        return Task.FromResult(outputs.GetValueOrDefault(channel));
    }

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) throw new InvalidOperationException("Connect the simulated instrument first.");
    }

    private sealed class SimulationOptions
    {
        public int[]? Channels { get; init; }
    }
}

internal sealed class SimulatedProgrammer : IProgrammer
{
    private readonly DriverConfiguration driverConfiguration;
    private bool connected;
    private string? lastProgrammedFile;

    public SimulatedProgrammer(DriverConfiguration driverConfiguration)
    {
        SimulatedProfiles.ValidateNoOptions(driverConfiguration);
        this.driverConfiguration = driverConfiguration;
    }

    public DeviceProfile Profile { get; } = SimulatedProfiles.Create(
        "simulated-programmer", "Simulated programmer", DeviceCapability.Programmer);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driverConfiguration);
        cancellationToken.ThrowIfCancellationRequested();
        if (connected) throw new InvalidOperationException("The simulated instrument is already connected.");
        connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connected = false;
        return Task.CompletedTask;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult("TESTPILOT,SIMULATED-PROGRAMMER,1.0");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(new SelfCheckResult(true, "Simulated self-check passed."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.CompletedTask;
    }

    public Task ProgramAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        lastProgrammedFile = imagePath;
        return Task.CompletedTask;
    }

    public string? LastProgrammedFile => lastProgrammedFile;

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) throw new InvalidOperationException("Connect the simulated instrument first.");
    }
}

internal sealed class SimulatedBarcodeScanner : IBarcodeScanner
{
    private readonly string barcode;
    private bool connected;

    public SimulatedBarcodeScanner(DriverConfiguration driverConfiguration)
    {
        ArgumentNullException.ThrowIfNull(driverConfiguration);
        barcode = ReadBarcode(driverConfiguration.ConnectionParameters);
    }

    public DeviceProfile Profile { get; } = SimulatedProfiles.Create(
        "simulated-barcode-scanner", "Simulated barcode scanner", DeviceCapability.BarcodeScanner);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connected) throw new InvalidOperationException("The simulated instrument is already connected.");
        connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connected = false;
        return Task.CompletedTask;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult("TESTPILOT,SIMULATED-BARCODE-SCANNER,1.0");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(new SelfCheckResult(true, "Simulated self-check passed."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.CompletedTask;
    }

    public Task<string> ScanAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(barcode);
    }

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) throw new InvalidOperationException("Connect the simulated instrument first.");
    }

    private static string ReadBarcode(JsonElement parameters)
    {
        var options = JsonSerializer.Deserialize<ScannerOptions>(parameters, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        }) ?? throw new JsonException("Scanner options must be a JSON object.");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Barcode);
        return options.Barcode;
    }

    private sealed class ScannerOptions
    {
        // A valid serial number has exactly 10 digits, like the real code readers deliver.
        public string Barcode { get; init; } = "1234567890";
    }
}

public static class SimulatedDriverRegistration
{
    public static DriverRegistry AddSimulatedDrivers(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("SimulatedSwitchMultimeter", configuration => new SimulatedSwitchMultimeter(configuration));
        registry.Register("SimulatedPowerSupply", configuration => new SimulatedPowerSupply(configuration));
        registry.Register("SimulatedProgrammer", configuration => new SimulatedProgrammer(configuration));
        registry.Register("SimulatedBarcodeScanner", configuration => new SimulatedBarcodeScanner(configuration));
        registry.Register("SimulatedFixture", configuration => new SimulatedFixture(configuration));
        return registry;
    }
}
