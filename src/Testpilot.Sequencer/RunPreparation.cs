using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;
using Testpilot.Sap;

namespace Testpilot.Sequencer;

/// <summary>A run that is ready to start: station, order and the frozen execution plan.</summary>
public sealed record PreparedRun(string Station, string Fauf, OrderInfo Order, ExecutionPlan Plan, bool Simulated)
{
    /// <param name="operatorName">Operators are not recorded; only the short name of a debug login is stored here (otherwise empty).</param>
    public RunContext CreateContext(string serialNumber, string operatorName = "", bool singleStepMode = false) =>
        new(Fauf, serialNumber, Station, operatorName, singleStepMode, Simulated);

    public override string ToString() =>
        $"FAUF {Fauf}: {Plan.ArtNr}/{Plan.Fz}{(Order.Description.Length > 0 ? $" ({Order.Description})" : "")} version {Plan.PlanVersion} on '{Station}'{(Simulated ? " (simulation)" : "")}";
}

/// <summary>
/// UI-independent preparation of a run, shared by Testpilot.RunPlan and the operator UI:
/// FAUF -> SAP order (ArtNr/Fz) -> released plan -> frozen execution plan -> checked against the station.
/// With simulation enabled, every device is replaced by a simulated driver.
/// <paramref name="deviceAddresses"/> (devices.json of this PC) replaces the VISA resources stored at the station.
/// Messages are German because they are shown to the operator.
/// </summary>
public sealed class RunPreparation(TestpilotDbContext database, ISapClient sapClient, DriverRegistry registry,
    bool simulationEnabled, IReadOnlyDictionary<string, string>? deviceAddresses = null)
{
    /// <summary>Operator flow: the released plan is found from the article number and FZ of the SAP order.</summary>
    public async Task<PreparedRun> PrepareForOrderAsync(string stationName, string fauf,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fauf);
        var station = await LoadStationAsync(stationName, cancellationToken);
        var order = await sapClient.GetOrderAsync(fauf, cancellationToken);
        var storedPlan = await database.TestPlans.AsNoTracking()
            .SingleOrDefaultAsync(plan => plan.ArtNr == order.ArtNr && plan.Fz == order.Fz
                && plan.Status == PlanStatus.Released, cancellationToken)
            ?? throw new PlanNotFoundException(
                $"Für Artikel '{order.ArtNr}' / FZ '{order.Fz}' (FAUF '{fauf}') gibt es keinen freigegebenen Prüfplan.");
        return await PrepareAsync(station, fauf, order, storedPlan, cancellationToken);
    }

    /// <summary>Desk/debug flow: a given released plan; the SAP order must still belong to it.</summary>
    public async Task<PreparedRun> PrepareForPlanAsync(string stationName, Guid planId, string fauf,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fauf);
        var station = await LoadStationAsync(stationName, cancellationToken);
        var storedPlan = await database.TestPlans.AsNoTracking()
            .SingleOrDefaultAsync(plan => plan.Id == planId && plan.Status == PlanStatus.Released, cancellationToken)
            ?? throw new PlanNotFoundException($"Freigegebener Prüfplan '{planId}' wurde nicht gefunden.");
        var order = await sapClient.GetOrderAsync(fauf, cancellationToken);
        if (!StringComparer.Ordinal.Equals(order.ArtNr, storedPlan.ArtNr)
            || !StringComparer.Ordinal.Equals(order.Fz, storedPlan.Fz))
            throw new InvalidOperationException(
                $"FAUF '{fauf}' gehört zu Artikel/FZ '{order.ArtNr}/{order.Fz}', nicht zum Prüfplan '{storedPlan.ArtNr}/{storedPlan.Fz}'.");
        return await PrepareAsync(station, fauf, order, storedPlan, cancellationToken);
    }

    private async Task<Station> LoadStationAsync(string stationName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationName);
        var station = await database.Stations.AsNoTracking().Include(station => station.Devices)
            .SingleOrDefaultAsync(station => station.Name == stationName, cancellationToken)
            ?? throw new InvalidOperationException($"Prüfsystem '{stationName}' wurde nicht gefunden. Bitte im Editor unter „Prüfsysteme“ anlegen.");
        ApplyDeviceAddresses(station, deviceAddresses);
        return station;
    }

    /// <summary>
    /// Replaces the VISA resources of the station devices listed in devices.json. Only for a station loaded without
    /// change tracking: the database keeps its addresses. Returns the aliases that match no device of the station.
    /// </summary>
    public static IReadOnlyList<string> ApplyDeviceAddresses(Station station, IReadOnlyDictionary<string, string>? addresses)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (addresses is null)
            return [];
        foreach (var device in station.Devices)
            device.ConnectionParametersJson = DeviceAddressFile.Apply(addresses, device.DeviceKey, device.Driver, device.ConnectionParametersJson);
        return addresses.Keys
            .Where(alias => !station.Devices.Any(device => StringComparer.OrdinalIgnoreCase.Equals(device.DeviceKey, alias)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<PreparedRun> PrepareAsync(Station station, string fauf, OrderInfo order, TestPlan storedPlan,
        CancellationToken cancellationToken)
    {
        var plan = ExecutionPlanSerializer.Deserialize(storedPlan.ExecutionPlanJson
            ?? throw new PlanConflictException("Der freigegebene Prüfplan enthält keinen Ausführungsplan."));
        if (plan.PlanId != storedPlan.Id || plan.PlanVersion != storedPlan.Version
            || !StringComparer.Ordinal.Equals(plan.ArtNr, storedPlan.ArtNr)
            || !StringComparer.Ordinal.Equals(plan.Fz, storedPlan.Fz))
            throw new InvalidDataException("Der gespeicherte Ausführungsplan passt nicht zum Prüfplan.");

        Dictionary<string, SystemTypeDevice>? systemDevices = null;
        if (storedPlan.TestSystemTypeId is { } systemId)
        {
            if (station.TestSystemTypeId != systemId)
            {
                var planSystem = await SystemNameAsync(systemId, cancellationToken);
                var stationSystem = station.TestSystemTypeId is { } id ? await SystemNameAsync(id, cancellationToken) : "keinem Prüfsystemtyp";
                throw new InvalidOperationException(
                    $"Der Prüfplan ist für Prüfsystemtyp '{planSystem}', Prüfsystem '{station.Name}' gehört aber zu '{stationSystem}'.");
            }
            systemDevices = await database.SystemTypeDevices.AsNoTracking().Where(device => device.TestSystemTypeId == systemId)
                .ToDictionaryAsync(device => device.DeviceKey, StringComparer.Ordinal, cancellationToken);
        }

        var stationDevices = station.Devices.ToDictionary(device => device.DeviceKey, StringComparer.Ordinal);
        var missingDevices = StationDevices.Missing(plan.Devices.Select(device => device.DeviceKey), station);
        if (missingDevices.Count != 0)
            throw new InvalidOperationException(
                $"An Prüfsystem '{station.Name}' sind nicht alle im Plan benötigten Geräte eingebaut, es fehlt: {string.Join(", ", missingDevices)}.");

        var boundPlan = plan with
        {
            Devices = plan.Devices.Select(device => Bind(device, stationDevices[device.DeviceKey], station.Name, systemDevices)).ToArray()
        };
        boundPlan = ExecutionPlanSettings.Resolve(boundPlan, NamedSettings.Deserialize(station.SettingsJson));
        var runPlan = simulationEnabled ? SimulatedDriverMapping.Apply(boundPlan) : boundPlan;
        // Creating every driver profile validates all connection parameters before any device is touched.
        var definitions = runPlan.Devices.ToDictionary(device => device.DeviceKey, device =>
        {
            var configuration = DriverConfiguration.Parse(device.DeviceKey, device.Driver!, device.ConnectionParametersJson!);
            return new DeviceDefinition(configuration, registry.GetProfile(configuration), device.Model);
        }, StringComparer.Ordinal);
        // Optimizing against the driver profiles checks capabilities, channels and functions before the run.
        // The frozen plan is executed; only the simulated profiles need their own optimization.
        ExecutionPlan optimizedForDrivers;
        try
        {
            optimizedForDrivers = new ExecutionPlanOptimizer().Optimize(SimulatedDriverMapping.ToTestPlan(runPlan), definitions);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Der Prüfplan passt nicht zu den Treibern von Prüfsystem '{station.Name}': {exception.Message}", exception);
        }
        if (simulationEnabled)
            runPlan = optimizedForDrivers;
        return new PreparedRun(station.Name, fauf, order, runPlan, simulationEnabled);
    }

    /// <summary>
    /// Plans written against a test system type only contain the device model; the station provides driver and connection.
    /// Older plans already contain a driver and are used unchanged.
    /// A mainframe (34980A) gets the module slots of the test system type unless the station sets slots or profilePath.
    /// </summary>
    private static ExecutionDevice Bind(ExecutionDevice device, StationDevice stationDevice, string stationName,
        IReadOnlyDictionary<string, SystemTypeDevice>? systemDevices)
    {
        if (device.Model is null)
            return device;
        SystemTypeDevice? systemDevice = null;
        if (systemDevices is not null
            && (!systemDevices.TryGetValue(device.DeviceKey, out systemDevice) || systemDevice.Model != device.Model))
            throw new InvalidOperationException(
                $"Gerät '{device.DeviceKey}' ({device.Model}) ist nicht mehr so im Prüfsystemtyp vorhanden. Der Prüfsystemtyp wurde nach der Freigabe geändert; bitte den Plan neu freigeben.");
        try
        {
            DeviceCatalog.ValidateBinding(device.DeviceKey, device.Model, stationDevice.Driver, stationDevice.ConnectionParametersJson);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"Prüfsystem '{stationName}': {exception.Message}", exception);
        }
        var connection = stationDevice.ConnectionParametersJson;
        var model = DeviceCatalog.Get(device.Model);
        if (systemDevice is not null && model.ModuleSlotCount > 0 && stationDevice.Driver != model.SimulatedDriver)
            connection = AddModuleSlots(connection, DeviceCatalog.ParseOptions(model.Name, systemDevice.OptionsJson));
        return device with { Driver = stationDevice.Driver, ConnectionParametersJson = connection };
    }

    private static string AddModuleSlots(string connectionParametersJson, SystemTypeDeviceOptions options)
    {
        if (JsonNode.Parse(connectionParametersJson) is not JsonObject connection
            || connection.Any(property => property.Key.Equals("slots", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("profilePath", StringComparison.OrdinalIgnoreCase)))
            return connectionParametersJson;
        connection["slots"] = JsonNode.Parse(options.ToJson())!["slots"]!.DeepClone();
        return connection.ToJsonString();
    }

    private async Task<string> SystemNameAsync(Guid id, CancellationToken cancellationToken) =>
        await database.TestSystemTypes.AsNoTracking().Where(system => system.Id == id).Select(system => system.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? id.ToString();
}

/// <summary>Reads the optional devices.json of this PC (setting <c>DevicesFile</c>).</summary>
public static class DeviceAddressConfiguration
{
    /// <summary>
    /// <c>DevicesFile</c> empty: <c>devices.json</c> next to the executable, if present. Set: this file must exist;
    /// a relative path is relative to the executable. Returns the full path and the addresses (null without file).
    /// </summary>
    public static (string Path, IReadOnlyDictionary<string, string>? Addresses) Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration["DevicesFile"];
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DeviceAddressFile.DefaultFileName : configured.Trim(),
            AppContext.BaseDirectory);
        var addresses = DeviceAddressFile.Load(path);
        if (addresses is null && !string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"DevicesFile: Datei '{path}' wurde nicht gefunden.");
        return (path, addresses);
    }
}

/// <summary>Creates the SAP client from configuration: REST API, external program or mock.</summary>
public static class SapClientFactory
{
    // One client for the process; the per-request timeout is applied by SapRestClient.
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// <c>Sap:BaseUrl</c> (for example <c>https://prod.heka.de/sapapi/</c>) selects <see cref="SapRestClient"/>.
    /// Otherwise <c>Sap:Command</c> + <c>Sap:ArgumentsJson</c> select <see cref="ExternalProgramSapClient"/>.
    /// Both use <c>Sap:TimeoutSeconds</c> (default 30). Without both, a <see cref="MockSapClient"/> is filled from
    /// <c>Sap:MockOrders:&lt;FAUF&gt;:ArtNr/Fz/Description</c>.
    /// </summary>
    public static ISapClient Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var timeoutText = configuration["Sap:TimeoutSeconds"];
        var timeoutSeconds = 30;
        if (!string.IsNullOrWhiteSpace(timeoutText) && (!int.TryParse(timeoutText, out timeoutSeconds) || timeoutSeconds is < 1 or > 600))
            throw new InvalidOperationException("Sap:TimeoutSeconds muss eine Zahl zwischen 1 und 600 sein.");
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        var baseUrl = configuration["Sap:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Sap:BaseUrl '{baseUrl}' ist keine gültige Adresse.");
            return new SapRestClient(SharedHttpClient, uri, timeout);
        }
        var command = configuration["Sap:Command"];
        if (!string.IsNullOrWhiteSpace(command))
        {
            var argumentsJson = configuration["Sap:ArgumentsJson"] ?? "[\"{fauf}\"]";
            var arguments = JsonSerializer.Deserialize<string[]>(argumentsJson)
                ?? throw new JsonException("Sap:ArgumentsJson must be a JSON string array.");
            return new ExternalProgramSapClient(command, arguments, timeout);
        }
        var mock = new MockSapClient();
        foreach (var order in configuration.GetSection("Sap:MockOrders").GetChildren())
            mock.AddOrder(order.Key, new OrderInfo(order["ArtNr"] ?? "", order["Fz"] ?? "", order["Description"] ?? ""));
        return mock;
    }
}

/// <summary>Replaces the drivers of an execution plan by simulated drivers (Simulation:Enabled).</summary>
internal static class SimulatedDriverMapping
{
    public static ExecutionPlan Apply(ExecutionPlan plan)
    {
        var driverNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var channels = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var step in plan.Steps.Where(step => step.DeviceKey is not null))
        {
            var driver = step.StepType switch
            {
                StepType.Measure or StepType.Relay => "SimulatedSwitchMultimeter",
                StepType.SetPower => "SimulatedPowerSupply",
                StepType.Program => "SimulatedProgrammer",
                StepType.Scan => "SimulatedBarcodeScanner",
                _ => null
            };
            if (driver is null)
                continue;
            if (driverNames.TryGetValue(step.DeviceKey!, out var previous) && previous != driver)
                throw new InvalidOperationException($"Gerät '{step.DeviceKey}' benötigt mehrere Treiberfähigkeiten; die Simulation kann es nicht eindeutig zuordnen.");
            driverNames[step.DeviceKey!] = driver;
            if (driver is "SimulatedSwitchMultimeter" or "SimulatedPowerSupply")
            {
                var channel = step.StepType switch
                {
                    StepType.Measure => StepParameters.Deserialize<MeasureParameters>(step.ParametersJson).Channel,
                    StepType.Relay => StepParameters.Deserialize<RelayParameters>(step.ParametersJson).Channel,
                    _ => StepParameters.Deserialize<SetPowerParameters>(step.ParametersJson).Channel
                };
                if (!channels.TryGetValue(step.DeviceKey!, out var deviceChannels))
                    channels.Add(step.DeviceKey!, deviceChannels = []);
                deviceChannels.Add(channel);
            }
        }
        var devices = plan.Devices.Select(device =>
        {
            if (!driverNames.TryGetValue(device.DeviceKey, out var driver))
                throw new InvalidOperationException($"Für Gerät '{device.DeviceKey}' kann kein simulierter Treiber abgeleitet werden.");
            var parameters = device.Driver == driver ? device.ConnectionParametersJson ?? "{}" : "{}";
            if (driver is "SimulatedSwitchMultimeter" or "SimulatedPowerSupply")
            {
                var json = device.Driver == driver
                    ? JsonNode.Parse(parameters) as JsonObject ?? throw new JsonException("Simulated connection parameters must be an object.")
                    : new JsonObject();
                if (json.ContainsKey("channels") && json["channels"] is not JsonArray)
                    throw new JsonException("Simulated channel numbers must be a JSON array.");
                var configuredChannels = json["channels"]?.Deserialize<int[]>() ?? [];
                var allChannels = configuredChannels.Concat(channels[device.DeviceKey]).Distinct().Order().ToArray();
                json["channels"] = JsonSerializer.SerializeToNode(allChannels);
                parameters = json.ToJsonString();
            }
            return device with { Driver = driver, ConnectionParametersJson = parameters };
        }).ToArray();
        return plan with { Devices = devices };
    }

    public static TestPlan ToTestPlan(ExecutionPlan plan) => new()
    {
        Id = plan.PlanId, ArtNr = plan.ArtNr, Fz = plan.Fz, Version = plan.PlanVersion,
        SettingsJson = NamedSettings.Serialize(plan.Settings),
        Steps = plan.Steps.Select(step => new TestStep
        {
            Order = step.Order, Name = step.Name, StepType = step.StepType, DeviceKey = step.DeviceKey,
            ParametersJson = step.ParametersJson, LimitLow = step.LimitLow, LimitHigh = step.LimitHigh,
            Unit = step.Unit, BreakpointFlag = step.BreakpointFlag
        }).ToList()
    };
}
