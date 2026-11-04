using System.Diagnostics;
using System.Text.Json;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;

namespace Testpilot.Sequencer;

public interface IStepExecutor
{
    StepType StepType { get; }
    Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken);
}

public sealed class StepExecutorRegistry
{
    private readonly Dictionary<StepType, IStepExecutor> executors;

    public StepExecutorRegistry(CustomStepRegistry customSteps)
    {
        ArgumentNullException.ThrowIfNull(customSteps);
        var all = new IStepExecutor[]
        {
            new MeasureStepExecutor(), new RelayStepExecutor(), new SetPowerStepExecutor(),
            new ProgramStepExecutor(), new ScanStepExecutor(), new WaitStepExecutor(),
            new OperatorPromptStepExecutor(), new ExternalStepExecutor(), new CustomCodeStepExecutor(),
            new DutCommandStepExecutor(), new CalculateStepExecutor()
        };
        executors = all.ToDictionary(executor => executor.StepType);
        CustomSteps = customSteps;
    }

    public CustomStepRegistry CustomSteps { get; }

    public Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CancellationToken cancellationToken) =>
        executors.TryGetValue(step.StepType, out var executor)
            ? executor.ExecuteAsync(step, devices, runContext, interaction, CustomSteps, cancellationToken)
            : throw new NotSupportedException($"No executor is registered for step type '{step.StepType}'.");
}

public sealed class MeasureStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.Measure;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var parameters = StepParameters.Deserialize<MeasureParameters>(step.ParametersJson);
        var multimeter = devices.GetCapability<ISwitchMultimeter>(step.DeviceKey!);
        var readings = new List<double>();
        for (var sample = 0; sample < (parameters.Samples ?? 1); sample++)
            readings.Add(await multimeter.MeasureAsync(parameters.Channel, cancellationToken));
        var value = Average(readings, parameters.DiscardHighest ?? 0);
        var check = LimitChecker.Check(value, step.LimitLow, step.LimitHigh);
        return new(double.IsFinite(value) ? value : null,
            TextValue: readings.Count > 1 ? FormattableString.Invariant($"Einzelwerte: {string.Join("; ", readings)}") : null,
            Result: check.Result,
            ErrorMessage: check.Result == StepOutcome.Error ? "The measurement was not finite." : null);
    }

    /// <summary>Mean of the readings after the <paramref name="discardHighest"/> largest readings are dropped.</summary>
    public static double Average(IReadOnlyList<double> readings, int discardHighest)
    {
        if (discardHighest < 0 || discardHighest >= readings.Count)
            throw new ArgumentOutOfRangeException(nameof(discardHighest),
                $"{discardHighest} of {readings.Count} readings cannot be discarded.");
        return readings.Order().Take(readings.Count - discardHighest).Average();
    }
}

/// <summary>
/// Sends one command to a DUT or golden sample. The value is the Output formula (e.g. <c>{rssi868}</c>), otherwise
/// 1 for an acknowledged command. With Text and Expected the formatted text is compared (e.g. firmware version).
/// </summary>
public sealed class DutCommandStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.DutCommand;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var parameters = StepParameters.Deserialize<DutCommandParameters>(step.ParametersJson);
        IReadOnlyList<double> RunValue(string name) => runContext.Values.Resolve(name, runContext);
        var arguments = (parameters.Arguments ?? []).Select(argument => Formula.Evaluate(argument, RunValue)).ToArray();
        var response = await devices.GetCapability<IDutCommunication>(step.DeviceKey!)
            .ExecuteAsync(parameters.Operation, arguments, cancellationToken);
        runContext.Values.SetOutputs(step.Name, response.Outputs);
        if (!response.Success)
            return new(TextValue: response.ToString(), Result: StepOutcome.Error,
                ErrorMessage: $"The device did not acknowledge '{parameters.Operation}'.");

        // The step's own outputs come first, so "{rssi868}" means the output of this command.
        IReadOnlyList<double> Resolve(string name) =>
            response.Outputs.TryGetValue(name, out var list) ? list : RunValue(name);
        if (!string.IsNullOrWhiteSpace(parameters.Text))
        {
            var text = Formula.FormatText(parameters.Text!, Resolve);
            if (parameters.Expected is null)
                return new(TextValue: text);
            var equal = string.Equals(text, parameters.Expected, StringComparison.Ordinal);
            return new(equal ? 1 : 0, text, equal ? StepOutcome.Pass : StepOutcome.Fail,
                equal ? null : $"Expected '{parameters.Expected}', read '{text}'.");
        }
        if (string.IsNullOrWhiteSpace(parameters.Output))
            return new(1, response.ToString(), LimitChecker.Check(1, step.LimitLow, step.LimitHigh).Result);
        var value = Formula.Evaluate(parameters.Output!, Resolve);
        var check = LimitChecker.Check(value, step.LimitLow, step.LimitHigh);
        return new(double.IsFinite(value) ? value : null, response.ToString(), check.Result,
            check.Result == StepOutcome.Error ? "The value is not finite." : null);
    }
}

/// <summary>Calculates a value from earlier step values, e.g. <c>{V114} / {V103}</c>, and checks the limits.</summary>
public sealed class CalculateStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.Calculate;

    public Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var formula = StepParameters.Deserialize<CalculateParameters>(step.ParametersJson).Formula;
        var value = Formula.Evaluate(formula, name => runContext.Values.Resolve(name, runContext));
        var check = LimitChecker.Check(value, step.LimitLow, step.LimitHigh);
        return Task.FromResult(new StepExecutionData(double.IsFinite(value) ? value : null, formula, check.Result,
            check.Result == StepOutcome.Error ? "The result is not finite (e.g. division by zero)." : null));
    }
}

public sealed class RelayStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.Relay;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var parameters = StepParameters.Deserialize<RelayParameters>(step.ParametersJson);
        await devices.GetCapability<ISwitchMultimeter>(step.DeviceKey!)
            .SetRelayAsync(parameters.Channel, parameters.Closed, cancellationToken);
        return new(TextValue: parameters.Closed ? "closed" : "open");
    }
}

public sealed class SetPowerStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.SetPower;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var parameters = StepParameters.Deserialize<SetPowerParameters>(step.ParametersJson);
        await devices.GetCapability<IPowerSupply>(step.DeviceKey!)
            .SetOutputAsync(parameters.Channel, parameters.Voltage, parameters.CurrentLimit, parameters.Enabled, cancellationToken);
        return new(TextValue: parameters.Enabled ? "enabled" : "disabled");
    }
}

public sealed class ProgramStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.Program;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var parameters = StepParameters.Deserialize<ProgramParameters>(step.ParametersJson);
        await devices.GetCapability<IProgrammer>(step.DeviceKey!)
            .ProgramAsync(parameters.FilePath, cancellationToken);
        return new(TextValue: parameters.FilePath);
    }
}

public sealed class ScanStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.Scan;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken) =>
        new(TextValue: await devices.GetCapability<IBarcodeScanner>(step.DeviceKey!).ScanAsync(cancellationToken));
}

public sealed class WaitStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.Wait;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var duration = StepParameters.Deserialize<WaitParameters>(step.ParametersJson).DurationMilliseconds;
        await Task.Delay(TimeSpan.FromMilliseconds(duration), cancellationToken);
        return new();
    }
}

public sealed class OperatorPromptStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.OperatorPrompt;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var message = StepParameters.Deserialize<OperatorPromptParameters>(step.ParametersJson).Message;
        var confirmed = await interaction.ConfirmAsync(message, cancellationToken);
        return new(TextValue: confirmed ? "yes" : "no", Result: confirmed ? StepOutcome.Pass : StepOutcome.Fail);
    }
}

public sealed class ExternalStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.External;

    public async Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var parameters = StepParameters.Deserialize<ExternalParameters>(step.ParametersJson);
        if (IsBatchFile(parameters.Command))
            throw new InvalidOperationException(
                $"External command '{parameters.Command}' is a batch file; cmd.exe would interpret the run data in its arguments. Call an .exe instead.");
        var startInfo = new ProcessStartInfo(parameters.Command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in parameters.Arguments)
            startInfo.ArgumentList.Add(Expand(argument, runContext));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"External command '{parameters.Command}' did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            // A non-zero exit code is the test verdict of the external program; start errors and timeouts are errors.
            if (process.ExitCode != 0)
                return new(TextValue: stdout, Result: StepOutcome.Fail,
                    ErrorMessage: $"Exit code {process.ExitCode}: {stderr.Trim()}");
            return new(TextValue: stdout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>Batch files run through cmd.exe, which re-parses arguments; such commands are rejected.</summary>
    public static bool IsBatchFile(string command) =>
        Path.GetExtension(command.Trim().Trim('"')) is var extension
        && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase));

    private static string Expand(string argument, RunContext context) => argument
        .Replace("{fauf}", context.Fauf, StringComparison.Ordinal)
        .Replace("{serial}", context.SerialNumber, StringComparison.Ordinal)
        .Replace("{station}", context.Station, StringComparison.Ordinal)
        .Replace("{operator}", context.Operator, StringComparison.Ordinal);
}

public sealed class CustomCodeStepExecutor : IStepExecutor
{
    public StepType StepType => StepType.CustomCode;

    public Task<StepExecutionData> ExecuteAsync(ExecutionStep step, DeviceSession devices, RunContext runContext,
        ISequenceInteraction interaction, CustomStepRegistry customSteps, CancellationToken cancellationToken)
    {
        var parameters = StepParameters.Deserialize<CustomCodeParameters>(step.ParametersJson);
        return customSteps.GetRequired(parameters.HandlerKey)
            .ExecuteAsync(parameters.Parameters, devices, runContext, cancellationToken);
    }
}
