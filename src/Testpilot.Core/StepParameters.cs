using System.Text.Json;
using System.Text.Json.Serialization;

namespace Testpilot.Core;

public sealed record MeasureParameters(int Channel, string Function, double? Range = null, double? Nplc = null);
public sealed record RelayParameters(int Channel, bool Closed);
public sealed record SetPowerParameters(double Voltage, double CurrentLimit, bool Enabled, int Channel = 1);
public sealed record ProgramParameters(string FilePath);
public sealed record ScanParameters;
public sealed record WaitParameters(double DurationMilliseconds);
public sealed record OperatorPromptParameters(string Message);
public sealed record ExternalParameters(string Command, string[] Arguments);
public sealed record CustomCodeParameters(string HandlerKey, JsonElement Parameters);

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

    public static void Validate(TestStep step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
        if (step.Order < 0)
            throw new ArgumentOutOfRangeException(nameof(step), "Step order must not be negative.");
        if (step.StepType is StepType.Measure or StepType.Relay or StepType.SetPower or StepType.Program or StepType.Scan)
            ArgumentException.ThrowIfNullOrWhiteSpace(step.DeviceKey);
        LimitChecker.Validate(step.LimitLow, step.LimitHigh);
        if (step.StepType != StepType.Measure && (step.Unit.HasValue || step.LimitLow.HasValue || step.LimitHigh.HasValue))
            throw new ArgumentException("Only measurement steps have a unit and limits.");

        switch (step.StepType)
        {
            case StepType.Measure:
                var measure = Deserialize<MeasureParameters>(step.ParametersJson);
                Positive(measure.Channel, "Channel");
                var expectedUnit = measure.Function switch
                {
                    "VOLT:DC" or "VOLT:AC" => MeasurementUnit.Volt,
                    "CURR:DC" or "CURR:AC" => MeasurementUnit.Ampere,
                    "RES" or "FRES" => MeasurementUnit.Ohm,
                    _ => throw new ArgumentException("Unsupported measurement function.")
                };
                if (step.Unit != expectedUnit)
                    throw new ArgumentException("The unit does not match the measurement function.");
                if (measure.Range.HasValue) Positive(measure.Range.Value, "Range");
                if (measure.Nplc.HasValue) Positive(measure.Nplc.Value, "Nplc");
                if (measure.Nplc.HasValue && measure.Function is "VOLT:AC" or "CURR:AC")
                    throw new ArgumentException("NPLC applies only to DC and resistance measurements.");
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
            default:
                throw new ArgumentOutOfRangeException(nameof(step), "Unsupported step type.");
        }
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "The value must be finite and positive.");
    }
}
