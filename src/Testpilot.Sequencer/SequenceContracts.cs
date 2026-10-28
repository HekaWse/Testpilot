using System.Text.Json;
using Testpilot.Core;
using Testpilot.Planning;

namespace Testpilot.Sequencer;

public sealed record RunContext(string Fauf, string SerialNumber, string Station, string Operator,
    bool SingleStepMode = false, bool Simulated = false);
/// <summary>Progress of one step. <see cref="Result"/> is set when the step has finished (completed, failed, error).</summary>
public sealed record SequenceProgress(int StepOrder, int StepCount, string StepName, string State,
    RunStepResult? Result = null);
public sealed class SequenceProgressEventArgs(SequenceProgress progress) : EventArgs
{
    public SequenceProgress Progress { get; } = progress;
}
public sealed record StepExecutionData(double? Value = null, string? TextValue = null,
    StepOutcome? Result = null, string? ErrorMessage = null);
/// <summary>
/// Result of a run. <see cref="SaveError"/> is set when the database write or the JSON export failed; the run itself
/// is complete and its results are also written to the log. <see cref="ExportPath"/> is null when the export failed.
/// </summary>
public sealed record SequenceRunResult(Run Run, string? ExportPath, string? SaveError = null);

/// <summary>
/// Timeouts of the sequencer. <see cref="OperationTimeout"/> applies to every device operation and step;
/// <see cref="LongOperationTimeout"/> to Program, External and CustomCode steps. Wait and OperatorPrompt have none.
/// </summary>
public sealed record SequenceOptions(TimeSpan OperationTimeout, bool DisableTimeouts)
{
    public static SequenceOptions Default { get; } = new(TimeSpan.FromSeconds(30), false);

    public TimeSpan LongOperationTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        if (DisableTimeouts)
            return;
        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
        if (LongOperationTimeout <= TimeSpan.Zero || LongOperationTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(LongOperationTimeout));
    }
}

public interface ISequenceInteraction
{
    Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken);
}

/// <summary>
/// C# code for special cases, registered under a handler key in <see cref="CustomStepRegistry"/>.
/// The device session gives access to the devices of the run (e.g. <c>devices.GetCapability&lt;IPowerSupply&gt;("psu1")</c>).
/// </summary>
public interface ICustomStep
{
    Task<StepExecutionData> ExecuteAsync(JsonElement parameters, DeviceSession devices, RunContext runContext,
        CancellationToken cancellationToken);
}

public sealed class CustomStepRegistry
{
    private readonly Dictionary<string, ICustomStep> handlers = new(StringComparer.Ordinal);

    public void Register(string key, ICustomStep handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(handler);
        if (!handlers.TryAdd(key, handler))
            throw new InvalidOperationException($"Custom step handler '{key}' is already registered.");
    }

    public bool Contains(string key) => handlers.ContainsKey(key);

    public ICustomStep GetRequired(string key) => handlers.TryGetValue(key, out var handler)
        ? handler
        : throw new InvalidOperationException($"No custom step handler named '{key}' is registered.");
}

public sealed class ConsoleSequenceInteraction : ISequenceInteraction
{
    public async Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Schritt {stepOrder + 1}: {stepName} — Eingabetaste zum Fortfahren.");
        var answer = await Console.In.ReadLineAsync(cancellationToken);
        if (answer is null)
        {
            // Ctrl+C ends the pending ReadLine with null; that is an abort, not an input error.
            cancellationToken.ThrowIfCancellationRequested();
            throw new EndOfStreamException("The console input stream ended during single-step execution.");
        }
    }

    public async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        while (true)
        {
            Console.Write($"{message} (j/n): ");
            var answer = await Console.In.ReadLineAsync(cancellationToken);
            if (answer is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new EndOfStreamException("The console input stream ended during an operator prompt.");
            }
            if (answer.Equals("j", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("ja", StringComparison.OrdinalIgnoreCase)) return true;
            if (answer.Equals("n", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("nein", StringComparison.OrdinalIgnoreCase)) return false;
            Console.WriteLine("Bitte j oder n eingeben.");
        }
    }
}
