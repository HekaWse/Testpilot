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
            new OperatorPromptStepExecutor(), new ExternalStepExecutor(), new CustomCodeStepExecutor()
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
        var value = await devices.GetCapability<ISwitchMultimeter>(step.DeviceKey!)
            .MeasureAsync(parameters.Channel, cancellationToken);
        var check = LimitChecker.Check(value, step.LimitLow, step.LimitHigh);
        return new(double.IsFinite(value) ? value : null, Result: check.Result,
            ErrorMessage: check.Result == StepOutcome.Error ? "The measurement was not finite." : null);
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
