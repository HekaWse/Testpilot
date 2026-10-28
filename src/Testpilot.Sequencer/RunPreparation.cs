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
    public RunContext CreateContext(string serialNumber, string operatorName, bool singleStepMode = false) =>
        new(Fauf, serialNumber, Station, operatorName, singleStepMode, Simulated);

    public override string ToString() =>
        $"FAUF {Fauf}: {Plan.ArtNr}/{Plan.Fz} version {Plan.PlanVersion} on '{Station}'{(Simulated ? " (simulation)" : "")}";
}

/// <summary>
/// UI-independent preparation of a run, shared by Testpilot.RunPlan and the operator UI:
/// FAUF -> SAP order (ArtNr/Fz) -> released plan -> frozen execution plan -> checked against the station.
/// With simulation enabled, every device is replaced by a simulated driver.
/// Messages are German because they are shown to the operator.
/// </summary>
public sealed class RunPreparation(TestpilotDbContext database, ISapClient sapClient, DriverRegistry registry,
    bool simulationEnabled)
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
        return await database.Stations.AsNoTracking().Include(station => station.Devices)
            .SingleOrDefaultAsync(station => station.Name == stationName, cancellationToken)
            ?? throw new InvalidOperationException($"Prüfsystem '{stationName}' wurde nicht gefunden. Bitte im Editor unter „Prüfsysteme“ anlegen.");
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

        Dictionary<string, string>? systemModels = null;
        if (storedPlan.TestSystemTypeId is { } systemId)
        {
            if (station.TestSystemTypeId != systemId)
            {
                var planSystem = await SystemNameAsync(systemId, cancellationToken);
                var stationSystem = station.TestSystemTypeId is { } id ? await SystemNameAsync(id, cancellationToken) : "keinem Prüfsystemtyp";
                throw new InvalidOperationException(
                    $"Der Prüfplan ist für Prüfsystemtyp '{planSystem}', Prüfsystem '{station.Name}' gehört aber zu '{stationSystem}'.");
            }
            systemModels = await database.SystemTypeDevices.AsNoTracking().Where(device => device.TestSystemTypeId == systemId)
                .ToDictionaryAsync(device => device.DeviceKey, device => device.Model, StringComparer.Ordinal, cancellationToken);
        }

        var stationDevices = station.Devices.ToDictionary(device => device.DeviceKey, StringComparer.Ordinal);
        var missingDevices = StationDevices.Missing(plan.Devices.Select(device => device.DeviceKey), station);
        if (missingDevices.Count != 0)
            throw new InvalidOperationException(
                $"An Prüfsystem '{station.Name}' sind nicht alle im Plan benötigten Geräte eingebaut, es fehlt: {string.Join(", ", missingDevices)}.");

        var boundPlan = plan with
        {
            Devices = plan.Devices.Select(device => Bind(device, stationDevices[device.DeviceKey], station.Name, systemModels)).ToArray()
        };
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
    /// </summary>
    private static ExecutionDevice Bind(ExecutionDevice device, StationDevice stationDevice, string stationName,
        IReadOnlyDictionary<string, string>? systemModels)
    {
        if (device.Model is null)
            return device;
        if (systemModels is not null
            && (!systemModels.TryGetValue(device.DeviceKey, out var systemModel) || systemModel != device.Model))
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
        return device with { Driver = stationDevice.Driver, ConnectionParametersJson = stationDevice.ConnectionParametersJson };
    }

    private async Task<string> SystemNameAsync(Guid id, CancellationToken cancellationToken) =>
        await database.TestSystemTypes.AsNoTracking().Where(system => system.Id == id).Select(system => system.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? id.ToString();
}

/// <summary>Creates the SAP client from configuration (Sap:Command for the external program, otherwise the mock).</summary>
public static class SapClientFactory
{
    /// <summary>
    /// <c>Sap:Command</c> + <c>Sap:ArgumentsJson</c> select <see cref="ExternalProgramSapClient"/>
    /// (<c>Sap:TimeoutSeconds</c>, default 30).
    /// Without a command, a <see cref="MockSapClient"/> is filled from <c>Sap:MockOrders:&lt;FAUF&gt;:ArtNr/Fz</c>.
    /// </summary>
    public static ISapClient Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var command = configuration["Sap:Command"];
        if (!string.IsNullOrWhiteSpace(command))
        {
            var argumentsJson = configuration["Sap:ArgumentsJson"] ?? "[\"{fauf}\"]";
            var arguments = JsonSerializer.Deserialize<string[]>(argumentsJson)
                ?? throw new JsonException("Sap:ArgumentsJson must be a JSON string array.");
            var timeoutText = configuration["Sap:TimeoutSeconds"];
            var timeoutSeconds = 30;
            if (!string.IsNullOrWhiteSpace(timeoutText) && (!int.TryParse(timeoutText, out timeoutSeconds) || timeoutSeconds is < 1 or > 600))
                throw new InvalidOperationException("Sap:TimeoutSeconds muss eine Zahl zwischen 1 und 600 sein.");
            return new ExternalProgramSapClient(command, arguments, TimeSpan.FromSeconds(timeoutSeconds));
        }
        var mock = new MockSapClient();
        foreach (var order in configuration.GetSection("Sap:MockOrders").GetChildren())
            mock.AddOrder(order.Key, new OrderInfo(order["ArtNr"] ?? "", order["Fz"] ?? ""));
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
        Steps = plan.Steps.Select(step => new TestStep
        {
            Order = step.Order, Name = step.Name, StepType = step.StepType, DeviceKey = step.DeviceKey,
            ParametersJson = step.ParametersJson, LimitLow = step.LimitLow, LimitHigh = step.LimitHigh,
            Unit = step.Unit, BreakpointFlag = step.BreakpointFlag
        }).ToList()
    };
}
