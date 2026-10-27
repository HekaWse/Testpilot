namespace Testpilot.Core;

public enum PlanStatus { Draft, Released, Retired }
public enum StepType { Measure, Relay, SetPower, Program, Scan, Wait, OperatorPrompt, External, CustomCode }
public enum MeasurementUnit { Volt, Ampere, Ohm }
public enum StepOutcome { Pass, Fail, Error, Skipped }
public enum RunOutcome { Pass, Fail, Error, Aborted }

public sealed class Station
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<StationDevice> Devices { get; set; } = [];
}

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
    public PlanStatus Status { get; set; }
    public string? ExecutionPlanJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
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
    public List<RunDevice> Devices { get; set; } = [];
    public List<RunStepResult> StepResults { get; set; } = [];
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
    public StepOutcome Result { get; set; }
    public double? LimitLow { get; set; }
    public double? LimitHigh { get; set; }
    public MeasurementUnit? Unit { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
