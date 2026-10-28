using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Planning;

public static class ExecutionPlanSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Validate(plan);
        return JsonSerializer.Serialize(plan, Options);
    }

    public static ExecutionPlan Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var plan = JsonSerializer.Deserialize<ExecutionPlan>(json, Options)
            ?? throw new JsonException("Execution plan must not be null.");
        Validate(plan);
        return plan;
    }

    private static void Validate(ExecutionPlan plan)
    {
        if (plan.PlanId == Guid.Empty || plan.PlanVersion <= 0)
            throw new JsonException("Execution plan identity and version are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ArtNr);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Fz);
        ArgumentNullException.ThrowIfNull(plan.Devices);
        ArgumentNullException.ThrowIfNull(plan.Init);
        ArgumentNullException.ThrowIfNull(plan.Init.Configurations);
        ArgumentNullException.ThrowIfNull(plan.Steps);
        ArgumentNullException.ThrowIfNull(plan.Cleanup);
        ArgumentNullException.ThrowIfNull(plan.Cleanup.SafeStateDeviceKeys);
        ArgumentNullException.ThrowIfNull(plan.Cleanup.DisconnectDeviceKeys);
        if (plan.Steps.Count == 0)
            throw new JsonException("An execution plan must contain at least one step.");

        var deviceKeys = plan.Devices.Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
        if (deviceKeys.Count != plan.Devices.Count)
            throw new JsonException("Execution plan device keys must be unique.");
        foreach (var device in plan.Devices)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(device.DeviceKey);
            if (device.Model is not null && DeviceCatalog.Find(device.Model) is null)
                throw new JsonException($"Device '{device.DeviceKey}' has unknown model '{device.Model}'.");
            if (device.Driver is null)
            {
                if (device.Model is null || device.ConnectionParametersJson is not null)
                    throw new JsonException($"Device '{device.DeviceKey}' needs either a driver with connection parameters or a model.");
                continue;
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(device.Driver);
            ArgumentException.ThrowIfNullOrWhiteSpace(device.ConnectionParametersJson);
            using var connectionParameters = JsonDocument.Parse(device.ConnectionParametersJson);
            if (connectionParameters.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Device connection parameters must be a JSON object.");
        }
        var initialConfigurations = new Dictionary<(string Device, int Channel), MeasurementConfiguration>();
        foreach (var action in plan.Init.Configurations)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentException.ThrowIfNullOrWhiteSpace(action.DeviceKey);
            if (!deviceKeys.Contains(action.DeviceKey))
                throw new JsonException("An initialization configuration references an unknown device.");
            ValidateMeasurementConfiguration(action.Configuration);
            var key = (action.DeviceKey, action.Configuration.Channel);
            if (!initialConfigurations.TryAdd(key, action.Configuration))
                throw new JsonException("An execution plan cannot initialize one channel more than once.");
        }
        var usedDeviceKeys = plan.Steps.Where(step => !string.IsNullOrWhiteSpace(step.DeviceKey))
            .Select(step => step.DeviceKey!).ToHashSet(StringComparer.Ordinal);
        if (!usedDeviceKeys.SetEquals(deviceKeys))
            throw new JsonException("Execution plan devices must match the devices referenced by its steps.");
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            if (step.Order != index || !Enum.IsDefined(step.StepType))
                throw new JsonException("Execution steps must have unique, contiguous order values and known types.");
            if (step.DeviceKey is not null && !deviceKeys.Contains(step.DeviceKey))
                throw new JsonException($"Step {step.Order} references unknown device '{step.DeviceKey}'.");
            var snapshot = new Testpilot.Core.TestStep
            {
                Order = step.Order, Name = step.Name, StepType = step.StepType, DeviceKey = step.DeviceKey,
                ParametersJson = step.ParametersJson, LimitLow = step.LimitLow, LimitHigh = step.LimitHigh,
                Unit = step.Unit, BreakpointFlag = step.BreakpointFlag
            };
            Testpilot.Core.StepParameters.Validate(snapshot);
            if (step.ConfigureMeasurement is not null && step.StepType != Testpilot.Core.StepType.Measure)
                throw new JsonException("Only measurement steps can include a measurement configuration.");
            if (step.StepType == Testpilot.Core.StepType.Measure)
            {
                var parameters = Testpilot.Core.StepParameters.Deserialize<Testpilot.Core.MeasureParameters>(step.ParametersJson);
                var requested = new MeasurementConfiguration(parameters.Channel, parameters.Function, parameters.Range, parameters.Nplc);
                var key = (step.DeviceKey!, requested.Channel);
                if (step.ConfigureMeasurement is not null)
                {
                    ValidateMeasurementConfiguration(step.ConfigureMeasurement);
                    if (step.ConfigureMeasurement != requested)
                        throw new JsonException($"Step {step.Order} has a configuration that does not match its measurement parameters.");
                    initialConfigurations[key] = step.ConfigureMeasurement;
                }
                else if (!initialConfigurations.TryGetValue(key, out var current) || current != requested)
                    throw new JsonException($"Step {step.Order} is missing the required measurement configuration.");
            }
        }
        var safeStateKeys = plan.Cleanup.SafeStateDeviceKeys.ToHashSet(StringComparer.Ordinal);
        var disconnectKeys = plan.Cleanup.DisconnectDeviceKeys.ToHashSet(StringComparer.Ordinal);
        if (safeStateKeys.Count != plan.Cleanup.SafeStateDeviceKeys.Count
            || disconnectKeys.Count != plan.Cleanup.DisconnectDeviceKeys.Count
            || !safeStateKeys.SetEquals(deviceKeys) || !disconnectKeys.SetEquals(deviceKeys))
            throw new JsonException("Cleanup must safe-state and disconnect every execution device exactly once.");
    }

    private static void ValidateMeasurementConfiguration(MeasurementConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Channel <= 0)
            throw new JsonException("Measurement configuration requires a positive channel number.");
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Function);
        if (configuration.Range.HasValue && (!double.IsFinite(configuration.Range.Value) || configuration.Range.Value <= 0))
            throw new JsonException("Measurement range must be finite and positive.");
        if (configuration.Nplc.HasValue && (!double.IsFinite(configuration.Nplc.Value) || configuration.Nplc.Value <= 0))
            throw new JsonException("Measurement NPLC must be finite and positive.");
    }
}
