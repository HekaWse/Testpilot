using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Simulated FSA device under test (or golden sample). Written configuration values are stored and read back.
/// Connection options (all optional): <c>firmware</c> ("1.6.20"), <c>rssi433</c> and <c>rssi868</c> (received
/// signal strength per band in dBm), <c>supplyInfo</c> ([temperature, vccIdle, vccLoad, loadTempComp]),
/// <c>genericData</c> (answer of SendGenericTestCommand per "taskAddress|cmd", e.g. {"9|5": [5]}) and
/// <c>toggleCommands</c> (commands whose answer alternates 0, 1, 0, ... e.g. a key that is pressed and released).
/// </summary>
internal sealed class SimulatedDutCommunication : IDutCommunication
{
    private readonly SimulationOptions options;
    private readonly int[] firmware;
    private readonly Dictionary<string, int> toggleCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double[]> stored = new(StringComparer.Ordinal);
    private bool connected;

    public SimulatedDutCommunication(DriverConfiguration driverConfiguration)
    {
        ArgumentNullException.ThrowIfNull(driverConfiguration);
        options = JsonSerializer.Deserialize<SimulationOptions>(driverConfiguration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("Simulation options must be a JSON object.");
        var parts = (options.Firmware ?? "").Split('.');
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new JsonException("The simulated firmware must look like '1.6.20'.");
        firmware = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (!double.IsFinite(options.Rssi433) || !double.IsFinite(options.Rssi868))
            throw new JsonException("Simulated RSSI values must be finite.");
        if (options.SupplyInfo is not { Length: 4 })
            throw new JsonException("supplyInfo must contain 4 values: temperature, vccIdle, vccLoad, loadTempComp.");
        foreach (var key in (options.GenericData ?? []).Keys.Concat(options.ToggleCommands ?? []))
        {
            var keyParts = key.Split('|');
            if (keyParts.Length != 2 || keyParts.Any(part => !int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
                throw new JsonException($"Generic command key '{key}' must look like 'taskAddress|cmd', e.g. '9|5'.");
        }
    }

    public DeviceProfile Profile { get; } = SimulatedProfiles.Create(
        "simulated-dut", "Simulated FSA device", DeviceCapability.DutCommunication);

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
        toggleCounters.Clear();
        stored.Clear();
        return Task.CompletedTask;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult($"TESTPILOT,SIMULATED-FSA,{string.Join(".", firmware)}");
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

    public Task<DutResponse> ExecuteAsync(string operation, IReadOnlyList<double> arguments,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        ArgumentNullException.ThrowIfNull(arguments);
        var definition = FsaOperations.GetRequired(operation, arguments.Count);
        var values = arguments.Select((value, index) =>
            (double)FsaOperations.ToInteger(value, operation, definition.Inputs[index])).ToArray();
        var outputs = new Dictionary<string, double[]>(StringComparer.Ordinal);

        switch (operation)
        {
            case "ReadType":
                outputs["type"] = [Stored("deviceType", 0)];
                break;
            case "ReadFirmwareVersion":
                outputs["major"] = [firmware[0]];
                outputs["minor"] = [firmware[1]];
                outputs["deb"] = [firmware[2]];
                break;
            case "ReadRssi":
                outputs["rssi"] = [options.Rssi868];
                break;
            case "ReadEeprom":
                outputs["data"] = new double[Math.Max(0, (int)values[1])];
                break;
            case "ReadInfoConfigBuffer":
                // Self-test buffer: all zero = no error.
                outputs["data"] = new double[Math.Max(0, (int)values[3])];
                break;
            case "SendGenericTestCommand":
                outputs["data"] = GenericAnswer((int)values[0], (int)values[1]);
                break;
            case "ReceivingData":
                var count = Math.Max(0, (int)values[2]);
                var rssi = values[0] == 0 ? options.Rssi433 : options.Rssi868;
                outputs["rssi"] = Enumerable.Repeat(rssi, count).ToArray();
                outputs["pqi"] = new double[count];
                outputs["sqi"] = new double[count];
                outputs["afc"] = new double[count];
                break;
            case "ReadSupplyInfoBuffer":
                for (var index = 0; index < definition.Outputs.Count; index++)
                    outputs[definition.Outputs[index]] = [options.SupplyInfo![index]];
                break;
            case "GetDateTime":
                var now = DateTime.Now;
                double[] parts = [now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second];
                for (var index = 0; index < parts.Length; index++)
                    outputs[definition.Outputs[index]] = [parts[index]];
                break;
            default:
                if (operation.StartsWith("Write", StringComparison.Ordinal))
                {
                    for (var index = 0; index < definition.Inputs.Count; index++)
                        stored[definition.Inputs[index]] = [values[index]];
                }
                else if (operation.StartsWith("Read", StringComparison.Ordinal))
                {
                    foreach (var name in definition.Outputs)
                        outputs[name] = [Stored(name, 0)];
                }
                break;
        }
        return Task.FromResult(DutResponse.Ok(outputs));
    }

    private double[] GenericAnswer(int taskAddress, int command)
    {
        var key = $"{taskAddress}|{command}";
        if (options.ToggleCommands?.Contains(key) == true)
        {
            var counter = toggleCounters.GetValueOrDefault(key);
            toggleCounters[key] = counter + 1;
            return [counter % 2];
        }
        return options.GenericData?.TryGetValue(key, out var data) == true ? data.ToArray() : [0];
    }

    private double Stored(string name, double fallback) =>
        stored.TryGetValue(name, out var value) ? value[0] : fallback;

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) throw new InvalidOperationException("Connect the simulated instrument first.");
    }

    private sealed class SimulationOptions
    {
        public string? Firmware { get; init; } = "1.6.20";
        public double Rssi433 { get; init; } = -60;
        public double Rssi868 { get; init; } = -60;
        public int[]? SupplyInfo { get; init; } = [25, 3000, 2900, 0];
        public Dictionary<string, double[]>? GenericData { get; init; }
        public string[]? ToggleCommands { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };
}
