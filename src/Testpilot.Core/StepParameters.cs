using System.Text.Json;
using System.Text.Json.Serialization;

namespace Testpilot.Core;

/// <summary>
/// <see cref="Samples"/> readings are taken; the <see cref="DiscardHighest"/> highest are dropped and the rest averaged.
/// </summary>
public sealed record MeasureParameters(int Channel, string Function, double? Range = null, double? Nplc = null,
    int? Samples = null, int? DiscardHighest = null);
public sealed record RelayParameters(int Channel, bool Closed);
public sealed record SetPowerParameters(double Voltage, double CurrentLimit, bool Enabled, int Channel = 1);
public sealed record ProgramParameters(string FilePath);
public sealed record ScanParameters;
public sealed record WaitParameters(double DurationMilliseconds);
public sealed record OperatorPromptParameters(string Message);
public sealed record ExternalParameters(string Command, string[] Arguments);
public sealed record CustomCodeParameters(string HandlerKey, JsonElement Parameters);

/// <summary>
/// Command to the device under test (e.g. FSA DLL). <see cref="Arguments"/> are formulas (usually plain numbers or
/// <c>{serial}</c>). The step value is <see cref="Output"/> (formula over the outputs of the command, e.g. <c>{data[0]}</c>),
/// or 1/0 for success when <see cref="Output"/> is empty. With <see cref="Text"/> and <see cref="Expected"/> the
/// formatted text (e.g. <c>{major}.{minor}.{deb}</c>) is compared with the expected text.
/// </summary>
public sealed record DutCommandParameters(string Operation, string[]? Arguments = null, string? Output = null,
    string? Text = null, string? Expected = null);

/// <summary>Calculates a value from earlier results, e.g. <c>{V103} - {V101}</c>.</summary>
public sealed record CalculateParameters(string Formula);

public static class StepParameters
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    public static string Serialize<T>(T parameters) => JsonSerializer.Serialize(parameters, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException("Step parameters must not be null.");

    /// <summary>Measurement functions and the unit of their result.</summary>
    public static MeasurementUnit UnitOf(string function) => function switch
    {
        "VOLT:DC" or "VOLT:AC" => MeasurementUnit.Volt,
        "CURR:DC" or "CURR:AC" => MeasurementUnit.Ampere,
        "RES" or "FRES" => MeasurementUnit.Ohm,
        "FREQ" => MeasurementUnit.Hertz,
        _ => throw new ArgumentException("Unsupported measurement function.")
    };

    /// <summary>NPLC (integration time) exists only for DC and resistance measurements.</summary>
    public static bool SupportsNplc(string function) => function is "VOLT:DC" or "CURR:DC" or "RES" or "FRES";

    /// <summary>Step types whose result is a value checked against limits.</summary>
    public static bool HasValue(StepType type) => type is StepType.Measure or StepType.DutCommand or StepType.Calculate;

    public static void Validate(TestStep step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
        if (step.Order < 0)
            throw new ArgumentOutOfRangeException(nameof(step), "Step order must not be negative.");
        if (step.StepType is StepType.Measure or StepType.Relay or StepType.SetPower or StepType.Program or StepType.Scan
            or StepType.DutCommand)
            ArgumentException.ThrowIfNullOrWhiteSpace(step.DeviceKey);
        LimitChecker.Validate(step.LimitLow, step.LimitHigh);
        if (!HasValue(step.StepType) && (step.Unit.HasValue || step.LimitLow.HasValue || step.LimitHigh.HasValue))
            throw new ArgumentException("Only measurement, DUT command and calculation steps have a unit and limits.");
        if (step.RetryCount is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(step), "RetryCount must be between 0 and 1000.");
        if (!double.IsFinite(step.RetryDelayMilliseconds) || step.RetryDelayMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(step), "RetryDelayMilliseconds must be finite and nonnegative.");

        switch (step.StepType)
        {
            case StepType.Measure:
                var measure = Deserialize<MeasureParameters>(step.ParametersJson);
                Positive(measure.Channel, "Channel");
                if (step.Unit != UnitOf(measure.Function))
                    throw new ArgumentException("The unit does not match the measurement function.");
                if (measure.Range.HasValue) Positive(measure.Range.Value, "Range");
                if (measure.Nplc.HasValue) Positive(measure.Nplc.Value, "Nplc");
                if (measure.Nplc.HasValue && !SupportsNplc(measure.Function))
                    throw new ArgumentException("NPLC applies only to DC and resistance measurements.");
                if (measure.Samples is < 1 or > 100)
                    throw new ArgumentOutOfRangeException(nameof(step), "Samples must be between 1 and 100.");
                if (measure.DiscardHighest.HasValue
                    && (measure.DiscardHighest < 0 || measure.DiscardHighest >= (measure.Samples ?? 1)))
                    throw new ArgumentOutOfRangeException(nameof(step), "DiscardHighest must be at least 0 and less than Samples.");
                break;
            case StepType.Relay:
                Positive(Deserialize<RelayParameters>(step.ParametersJson).Channel, "Channel");
                break;
            case StepType.SetPower:
                var power = Deserialize<SetPowerParameters>(step.ParametersJson);
                if (!double.IsFinite(power.Voltage)) throw new ArgumentException("Voltage must be finite.");
                Positive(power.CurrentLimit, "CurrentLimit");
                Positive(power.Channel, "Channel");
                break;
            case StepType.Program:
                ArgumentException.ThrowIfNullOrWhiteSpace(Deserialize<ProgramParameters>(step.ParametersJson).FilePath);
                break;
            case StepType.Scan:
                _ = Deserialize<ScanParameters>(step.ParametersJson);
                break;
            case StepType.Wait:
                var duration = Deserialize<WaitParameters>(step.ParametersJson).DurationMilliseconds;
                if (!double.IsFinite(duration) || duration < 0) throw new ArgumentException("Duration must be finite and nonnegative.");
                break;
            case StepType.OperatorPrompt:
                ArgumentException.ThrowIfNullOrWhiteSpace(Deserialize<OperatorPromptParameters>(step.ParametersJson).Message);
                break;
            case StepType.External:
                var external = Deserialize<ExternalParameters>(step.ParametersJson);
                ArgumentException.ThrowIfNullOrWhiteSpace(external.Command);
                if (external.Arguments is null || external.Arguments.Any(argument => argument is null))
                    throw new ArgumentException("Arguments must be an array of non-null strings.");
                break;
            case StepType.CustomCode:
                var custom = Deserialize<CustomCodeParameters>(step.ParametersJson);
                ArgumentException.ThrowIfNullOrWhiteSpace(custom.HandlerKey);
                if (custom.Parameters.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Custom parameters must be a JSON object.");
                break;
            case StepType.DutCommand:
                var dut = Deserialize<DutCommandParameters>(step.ParametersJson);
                ArgumentException.ThrowIfNullOrWhiteSpace(dut.Operation);
                foreach (var argument in dut.Arguments ?? [])
                    ValidateFormula(argument, "argument");
                if (!string.IsNullOrWhiteSpace(dut.Output))
                    ValidateFormula(dut.Output, "output");
                if (dut.Expected is not null)
                {
                    if (string.IsNullOrWhiteSpace(dut.Text))
                        throw new ArgumentException("An expected text requires a text template.");
                    if (!string.IsNullOrWhiteSpace(dut.Output) || step.LimitLow.HasValue || step.LimitHigh.HasValue)
                        throw new ArgumentException("An expected text cannot be combined with an output formula or limits.");
                }
                break;
            case StepType.Calculate:
                ValidateFormula(Deserialize<CalculateParameters>(step.ParametersJson).Formula, "formula");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step), "Unsupported step type.");
        }
    }

    private static void ValidateFormula(string? formula, string name)
    {
        if (string.IsNullOrWhiteSpace(formula))
            throw new ArgumentException($"The {name} must not be empty.");
        try
        {
            Formula.Validate(formula);
        }
        catch (FormulaException exception)
        {
            throw new ArgumentException($"Invalid {name}: {exception.Message}", exception);
        }
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "The value must be finite and positive.");
    }
}
