namespace Testpilot.Core;

public enum PlanStatus { Draft = 0, Released = 1, Retired = 2 }
public enum StepType { Measure, Relay, SetPower, Program, Scan, Wait, OperatorPrompt, External, CustomCode }
public enum MeasurementUnit { Volt, Ampere, Ohm }
public enum StepOutcome { Pass, Fail, Error, Skipped }
public enum RunOutcome { Pass, Fail, Error, Aborted }

/// <summary>
/// A test system type (Prüfsystemtyp, for example "PS04") with all devices a system of this type can contain.
/// Plans are written against a type and can only use its devices. The concrete systems of the type
/// (PS04-1 ... PS04-9) are <see cref="Station"/>s; they provide driver and address of each device.
/// </summary>
public sealed class TestSystemType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<SystemTypeDevice> Devices { get; set; } = [];
}

/// <summary>A device of a test system type: key used by plan steps, catalog model and model options (slots, channels).</summary>
public sealed class SystemTypeDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TestSystemTypeId { get; set; }
    public string DeviceKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string Description { get; set; } = "";
    public string OptionsJson { get; set; } = "{}";
}

/// <summary>
/// A concrete test system (Prüfsystem, for example "PS04-3"; called station in the code and in the run data).
/// Its devices are the devices of its type that are actually installed, with driver and address.
/// A type device without station device is not installed at this system. Each test PC names its
/// system in its local configuration (<c>Operator:Station</c>).
/// </summary>
public sealed class Station
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid? TestSystemTypeId { get; set; }
    public List<StationDevice> Devices { get; set; } = [];
}

/// <summary>An installed device of a concrete test system: driver and connection parameters (VISA resource, COM port, ...).</summary>
public sealed class StationDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StationId { get; set; }
    public string DeviceKey { get; set; } = "";
    public string Driver { get; set; } = "";
    public string ConnectionParametersJson { get; set; } = "{}";
}

public sealed class TestPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ArtNr { get; set; } = "";
    public string Fz { get; set; } = "";
    public int Version { get; set; } = 1;
    public Guid? TestSystemTypeId { get; set; }
    public PlanStatus Status { get; set; }
    public string? ExecutionPlanJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DatabaseClock.UtcNow();
    public DateTimeOffset UpdatedAt { get; set; } = DatabaseClock.UtcNow();
    public DateTimeOffset? ReleasedAt { get; set; }
    public List<TestStep> Steps { get; set; } = [];
}

public sealed class TestStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TestPlanId { get; set; }
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public StepType StepType { get; set; }
    public string? DeviceKey { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public double? LimitLow { get; set; }
    public double? LimitHigh { get; set; }
    public MeasurementUnit? Unit { get; set; }
    public bool BreakpointFlag { get; set; }
}

public sealed class Run
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Fauf { get; set; } = "";
    public string ArtNr { get; set; } = "";
    public string Fz { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Station { get; set; } = "";
    public string Operator { get; set; } = "";
    public int PlanVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunOutcome? Result { get; set; }
    public string SoftwareVersion { get; set; } = "";
    /// <summary>True when the run used simulated drivers (Simulation:Enabled); such results are not real measurements.</summary>
    public bool Simulated { get; set; }
    public List<RunDevice> Devices { get; set; } = [];
    public List<RunStepResult> StepResults { get; set; } = [];

    public override string ToString() =>
        $"Run {Fauf} {ArtNr}/{Fz} v{PlanVersion} SN {SerialNumber} on {Station}: {Result?.ToString() ?? "running"}"
        + (Simulated ? " (simulated)" : "");
}

public sealed class RunDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string DeviceKey { get; set; } = "";
    public string Identification { get; set; } = "";
}

public sealed class RunStepResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public int Order { get; set; }
    public string Name { get; set; } = "";
    public StepType StepType { get; set; }
    public string? DeviceKey { get; set; }
    public int? Channel { get; set; }
    public string? Function { get; set; }
    public double? Value { get; set; }
    public string? TextValue { get; set; }
    public StepOutcome Result { get; set; }
    public double? LimitLow { get; set; }
    public double? LimitHigh { get; set; }
    public MeasurementUnit? Unit { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public override string ToString()
    {
        var unit = Unit.HasValue ? " " + Units.Symbol(Unit.Value) : "";
        var value = Value.HasValue ? Value.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture) + unit : TextValue ?? "-";
        var limits = LimitLow.HasValue || LimitHigh.HasValue
            ? $" [{LimitLow?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} .. {LimitHigh?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}]"
            : "";
        var error = ErrorMessage is null ? "" : $" ({ErrorMessage})";
        return $"#{Order} {Name}: {value}{limits} -> {Result}{error}";
    }
}
