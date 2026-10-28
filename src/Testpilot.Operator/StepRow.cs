using System.Globalization;
using Testpilot.Core;
using Testpilot.Planning;

namespace Testpilot.Operator;

public enum StepRowState { Pending, Running, Pass, Fail, Error, Skipped }

/// <summary>One line of the step table in the operator window.</summary>
public sealed class StepRow : ObservableObject
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private StepRowState state;
    private string valueText = "";
    private string? message;

    public StepRow(ExecutionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Order = step.Order;
        Name = step.Name;
        TypeText = TypeName(step.StepType);
        LowText = FormatValue(step.LimitLow, step.Unit);
        HighText = FormatValue(step.LimitHigh, step.Unit);
    }

    public int Order { get; }
    public int Number => Order + 1;
    public string Name { get; }
    public string TypeText { get; }
    public string LowText { get; }
    public string HighText { get; }

    public string ValueText
    {
        get => valueText;
        private set => SetProperty(ref valueText, value);
    }

    public string? Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public StepRowState State
    {
        get => state;
        private set
        {
            if (SetProperty(ref state, value))
                OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => State switch
    {
        StepRowState.Running => "läuft …",
        StepRowState.Pass => "i.O.",
        StepRowState.Fail => "n.i.O.",
        StepRowState.Error => "Fehler",
        StepRowState.Skipped => "übersprungen",
        _ => ""
    };

    public void Reset()
    {
        ValueText = "";
        Message = null;
        State = StepRowState.Pending;
    }

    public void MarkRunning() => State = StepRowState.Running;

    public void Apply(RunStepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValueText = result.Value is double value ? FormatValue(value, result.Unit)
            : result.StepType == StepType.OperatorPrompt && result.TextValue is "yes" ? "Ja"
            : result.StepType == StepType.OperatorPrompt && result.TextValue is "no" ? "Nein"
            : result.TextValue ?? "";
        Message = result.ErrorMessage;
        State = result.Result switch
        {
            StepOutcome.Pass => StepRowState.Pass,
            StepOutcome.Fail => StepRowState.Fail,
            StepOutcome.Error => StepRowState.Error,
            _ => StepRowState.Skipped
        };
    }

    public override string ToString() => $"{Number} {Name}: {StateText} {ValueText}".TrimEnd();

    internal static string FormatValue(double? value, MeasurementUnit? unit) => value switch
    {
        null => "",
        double number when unit is { } measurementUnit => Units.FormatAuto(number, measurementUnit, German),
        double number => number.ToString("G6", German)
    };

    private static string TypeName(StepType stepType) => stepType switch
    {
        StepType.Measure => "Messung",
        StepType.Relay => "Relais",
        StepType.SetPower => "Netzteil",
        StepType.Program => "Programmieren",
        StepType.Scan => "Code lesen",
        StepType.Wait => "Warten",
        StepType.OperatorPrompt => "Bedienerabfrage",
        StepType.External => "Externes Programm",
        StepType.CustomCode => "Sonderschritt",
        _ => stepType.ToString()
    };
}
