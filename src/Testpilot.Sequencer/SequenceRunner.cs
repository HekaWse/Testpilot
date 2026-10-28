using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers.Abstractions;
using Testpilot.Planning;

namespace Testpilot.Sequencer;

public sealed class SequenceRunner
{
    private readonly IDriverHost driverHost;
    private readonly TestpilotDbContext database;
    private readonly RunJsonExporter exporter;
    private readonly ILogger<SequenceRunner> logger;
    private readonly StepExecutorRegistry executors;
    private readonly ISequenceInteraction interaction;
    private readonly SequenceOptions options;

    public SequenceRunner(IDriverHost driverHost, TestpilotDbContext database, RunJsonExporter exporter,
        ILogger<SequenceRunner> logger, StepExecutorRegistry executors, ISequenceInteraction interaction,
        SequenceOptions? options = null)
    {
        this.driverHost = driverHost ?? throw new ArgumentNullException(nameof(driverHost));
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.executors = executors ?? throw new ArgumentNullException(nameof(executors));
        this.interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        this.options = options ?? SequenceOptions.Default;
        this.options.Validate();
    }

    public event EventHandler<SequenceProgressEventArgs>? ProgressChanged;

    public async Task<SequenceRunResult> RunAsync(ExecutionPlan plan, RunContext runContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runContext);
        ValidateContext(runContext);
        _ = ExecutionPlanSerializer.Serialize(plan);

        var run = new Run
        {
            Fauf = runContext.Fauf,
            ArtNr = plan.ArtNr,
            Fz = plan.Fz,
            SerialNumber = runContext.SerialNumber,
            Station = runContext.Station,
            Operator = runContext.Operator,
            PlanVersion = plan.PlanVersion,
            StartedAt = DateTimeOffset.UtcNow,
            Simulated = runContext.Simulated,
            SoftwareVersion = SoftwareVersion.Get(Assembly.GetEntryAssembly() ?? typeof(SequenceRunner).Assembly)
        };
        using var runScope = logger.BeginScope("RunId {RunId}", run.Id);
        var session = new DeviceSession(driverHost);
        var stopped = false;
        var aborted = false;
        var runError = false;
        try
        {
            // A missing handler is found before any device is switched on.
            foreach (var step in plan.Steps.Where(step => step.StepType == StepType.CustomCode))
            {
                var handlerKey = StepParameters.Deserialize<CustomCodeParameters>(step.ParametersJson).HandlerKey;
                if (!executors.CustomSteps.Contains(handlerKey))
                    throw new InvalidOperationException(
                        $"Step {step.Order + 1} '{step.Name}' needs custom step handler '{handlerKey}', which is not registered.");
            }
            await WithTimeoutAsync(token => session.OpenAsync(plan.Devices, token), cancellationToken);
            run.Devices.AddRange(await WithTimeoutAsync(session.GetDeviceSnapshotsAsync, cancellationToken));
            foreach (var action in plan.Init.Configurations)
            {
                await WithTimeoutAsync(token => session.GetCapability<ISwitchMultimeter>(action.DeviceKey)
                    .ConfigureMeasurementAsync(action.Configuration, token), cancellationToken);
            }

            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    stopped = true;
                    AddSkippedSteps(run, plan.Steps, index, "Run cancelled before this step.");
                    break;
                }

                var started = DateTimeOffset.UtcNow;
                try
                {
                    if (step.BreakpointFlag && Debugger.IsAttached)
                        Debugger.Break();
                    // Waiting for the operator is not a device operation: no timeout, only cancellation.
                    if (runContext.SingleStepMode)
                        await interaction.WaitForContinueAsync(step.Order, step.Name, cancellationToken);
                    ProgressChanged?.Invoke(this, new(new(step.Order, plan.Steps.Count, step.Name, "started")));
                    if (step.ConfigureMeasurement is not null)
                        await WithTimeoutAsync(token => session.GetCapability<ISwitchMultimeter>(step.DeviceKey!)
                            .ConfigureMeasurementAsync(step.ConfigureMeasurement, token), cancellationToken);

                    var data = step.StepType switch
                    {
                        // Operator answers and waits are bounded by the operator or the step itself.
                        StepType.OperatorPrompt or StepType.Wait =>
                            await executors.ExecuteAsync(step, session, runContext, interaction, cancellationToken),
                        StepType.Program or StepType.External or StepType.CustomCode =>
                            await WithTimeoutAsync(token => executors.ExecuteAsync(step, session, runContext, interaction, token),
                                cancellationToken, options.LongOperationTimeout),
                        _ => await WithTimeoutAsync(token => executors.ExecuteAsync(step, session, runContext,
                            interaction, token), cancellationToken)
                    };
                    var outcome = data.Result ?? StepOutcome.Pass;
                    var stepResult = CreateStepResult(step, started, DateTimeOffset.UtcNow, data, outcome);
                    run.StepResults.Add(stepResult);
                    ProgressChanged?.Invoke(this, new(new(step.Order, plan.Steps.Count, step.Name,
                        outcome switch { StepOutcome.Fail => "failed", StepOutcome.Error => "error", _ => "completed" }, stepResult)));
                    if (outcome == StepOutcome.Error)
                    {
                        // An error result (e.g. a non-finite measurement) stops the sequence like an exception.
                        runError = true;
                        stopped = true;
                        AddSkippedSteps(run, plan.Steps, index + 1, "Skipped after an earlier step error.");
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    stopped = true;
                    run.StepResults.Add(CreateStepResult(step, started, DateTimeOffset.UtcNow,
                        new(ErrorMessage: "Run cancelled during this step."), StepOutcome.Skipped));
                    AddSkippedSteps(run, plan.Steps, index + 1, "Run cancelled before this step.");
                    break;
                }
                catch (Exception exception)
                {
                    runError = true;
                    stopped = true;
                    logger.LogError(exception, "Step {StepOrder} ({StepName}) failed", step.Order, step.Name);
                    var stepResult = CreateStepResult(step, started, DateTimeOffset.UtcNow,
                        new(ErrorMessage: exception.Message), StepOutcome.Error);
                    run.StepResults.Add(stepResult);
                    ProgressChanged?.Invoke(this, new(new(step.Order, plan.Steps.Count, step.Name, "error", stepResult)));
                    AddSkippedSteps(run, plan.Steps, index + 1, "Skipped after an earlier step error.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            aborted = true;
            stopped = true;
            AddSkippedSteps(run, plan.Steps, run.StepResults.Count, "Run cancelled before this step.");
        }
        catch (Exception exception)
        {
            runError = true;
            stopped = true;
            logger.LogError(exception, "Sequence run {RunId} failed before or between steps", run.Id);
            AddSkippedSteps(run, plan.Steps, run.StepResults.Count, "Skipped because the sequence could not continue.");
        }
        finally
        {
            foreach (var deviceKey in plan.Cleanup.SafeStateDeviceKeys)
            {
                if (!session.Contains(deviceKey))
                    continue;
                try
                {
                    await WithTimeoutAsync(token => session.GetInstrument(deviceKey).SafeStateAsync(token), CancellationToken.None);
                }
                catch (Exception exception)
                {
                    runError = true;
                    logger.LogError(exception, "Safe state failed for device {DeviceKey}", deviceKey);
                }
            }
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                runError = true;
                logger.LogError(exception, "Device session cleanup failed for run {RunId}", run.Id);
            }
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Result = runError || run.StepResults.Any(step => step.Result == StepOutcome.Error) ? RunOutcome.Error
            : aborted ? RunOutcome.Aborted
            : run.StepResults.Any(step => step.Result == StepOutcome.Fail) ? RunOutcome.Fail : RunOutcome.Pass;
        var saveError = await SaveAsync(run);
        string? exportPath = null;
        try
        {
            exportPath = await exporter.ExportAsync(run, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Run {RunId} could not be exported", run.Id);
            saveError = (saveError is null ? "" : saveError + "; ") + $"JSON-Export: {exception.Message}";
        }
        if (saveError is not null)
        {
            // Keep the measured data at least in the log file.
            foreach (var stepResult in run.StepResults)
                logger.LogError("Unsaved result of run {RunId} ({Serial}): {StepResult}", run.Id, run.SerialNumber, stepResult);
        }
        logger.LogInformation("Sequence run {RunId} finished with {Result}; stopped={Stopped}", run.Id, run.Result, stopped);
        return new(run, exportPath, saveError);
    }

    /// <summary>Writes the run to the database. Returns an error text instead of throwing, so the export still runs.</summary>
    private async Task<string?> SaveAsync(Run run)
    {
        try
        {
            database.Runs.Add(run);
            await database.SaveChangesAsync(CancellationToken.None);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Run {RunId} could not be saved to the database", run.Id);
            // Otherwise the next SaveChanges on this context would try to insert the run again.
            foreach (var entry in database.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).ToArray())
                entry.State = EntityState.Detached;
            return $"Datenbank: {exception.Message}";
        }
    }

    private Task WithTimeoutAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        WithTimeoutAsync(async token =>
        {
            await action(token);
            return true;
        }, cancellationToken, timeout);

    /// <summary>Runs an operation with a timeout. An expired timeout is reported as <see cref="TimeoutException"/>.</summary>
    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (options.DisableTimeouts)
            return await action(cancellationToken);
        var limit = timeout ?? options.OperationTimeout;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(limit);
        try
        {
            return await action(source.Token);
        }
        catch (OperationCanceledException exception)
            when (source.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Timeout after {limit.TotalSeconds:0.###} s."), exception);
        }
    }

    private static RunStepResult CreateStepResult(ExecutionStep step, DateTimeOffset started,
        DateTimeOffset finished, StepExecutionData data, StepOutcome result)
    {
        int? channel = null;
        string? function = null;
        if (step.StepType == StepType.Measure)
        {
            var parameters = StepParameters.Deserialize<MeasureParameters>(step.ParametersJson);
            channel = parameters.Channel;
            function = parameters.Function;
        }
        else if (step.StepType == StepType.Relay)
        {
            channel = StepParameters.Deserialize<RelayParameters>(step.ParametersJson).Channel;
        }
        return new RunStepResult
        {
            Order = step.Order,
            Name = step.Name,
            StepType = step.StepType,
            DeviceKey = step.DeviceKey,
            Channel = channel,
            Function = function,
            Value = data.Value is double value && double.IsFinite(value) ? value : null,
            TextValue = data.TextValue,
            Result = result,
            LimitLow = step.LimitLow,
            LimitHigh = step.LimitHigh,
            Unit = step.Unit,
            StartedAt = started,
            FinishedAt = finished,
            ErrorMessage = data.ErrorMessage
        };
    }

    private static void AddSkippedSteps(Run run, IReadOnlyList<ExecutionStep> steps, int start,
        string reason)
    {
        for (var index = start; index < steps.Count; index++)
        {
            if (run.StepResults.Any(result => result.Order == steps[index].Order))
                continue;
            var now = DateTimeOffset.UtcNow;
            run.StepResults.Add(CreateStepResult(steps[index], now, now,
                new(ErrorMessage: reason), StepOutcome.Skipped));
        }
    }

    private static void ValidateContext(RunContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Fauf);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SerialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Station);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Operator);
    }
}
