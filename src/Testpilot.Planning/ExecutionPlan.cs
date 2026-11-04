using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Planning;

/// <summary>
/// Input of the optimizer for one device. Plans written against a test system only know the catalog
/// <paramref name="Model"/>; the driver and connection are bound at run time from the station.
/// Older plans carry the station <paramref name="Configuration"/> instead.
/// </summary>
public sealed record DeviceDefinition(DriverConfiguration? Configuration, DeviceProfile Profile, string? Model = null)
{
    public static DeviceDefinition ForSystemDevice(string model, DeviceProfile profile) => new(null, profile, model);
}

/// <summary>
/// Device of a frozen execution plan: either a catalog <paramref name="Model"/> (bound to a station driver at run time)
/// or a fixed <paramref name="Driver"/> with connection parameters (station-bound plans and bound run plans).
/// </summary>
public sealed record ExecutionDevice(string DeviceKey, string? Driver, string? ConnectionParametersJson, string? Model = null)
{
    public bool IsBound => Driver is not null;
}
public sealed record MeasurementConfigurationAction(string DeviceKey, MeasurementConfiguration Configuration);
public sealed record ExecutionStep(int Order, string Name, StepType StepType, string? DeviceKey,
    string ParametersJson, double? LimitLow, double? LimitHigh, MeasurementUnit? Unit, bool BreakpointFlag,
    MeasurementConfiguration? ConfigureMeasurement)
{
    /// <summary>Additional attempts after Fail or Error (see <see cref="TestStep.RetryCount"/>).</summary>
    public int RetryCount { get; init; }

    /// <summary>Wait time before each repetition in milliseconds.</summary>
    public double RetryDelayMilliseconds { get; init; }
}
public sealed record ExecutionInitPhase(IReadOnlyList<MeasurementConfigurationAction> Configurations);
public sealed record ExecutionCleanupPhase(IReadOnlyList<string> SafeStateDeviceKeys,
    IReadOnlyList<string> DisconnectDeviceKeys);
public sealed record ExecutionPlan(Guid PlanId, int PlanVersion, string ArtNr, string Fz,
    IReadOnlyList<ExecutionDevice> Devices, ExecutionInitPhase Init, IReadOnlyList<ExecutionStep> Steps,
    ExecutionCleanupPhase Cleanup)
{
    public IReadOnlyList<NamedSetting> Settings { get; init; } = [];
}
