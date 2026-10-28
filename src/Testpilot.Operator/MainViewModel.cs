using System.Collections.ObjectModel;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Operator;

public enum RunDisplayState { None, Running, Pass, Fail, Error, Aborted }

/// <summary>
/// State and commands of the operator window: FAUF -> order and plan, serial number -> run, results.
/// Contains no WPF types, so it can be tested without a window.
/// It also answers the sequencer's single-step and operator-prompt requests (<see cref="ISequenceInteraction"/>).
/// With a configured test adapter (Operator:FixtureDevice) closing the adapter starts the run:
/// lock adapter -> read serial number -> run -> release adapter with the result lamp.
/// </summary>
public sealed class MainViewModel : ObservableObject, ISequenceInteraction
{
    /// <summary>How often the adapter state is read while waiting for the next board.</summary>
    public static readonly TimeSpan FixturePollInterval = TimeSpan.FromMilliseconds(200);

    private readonly OperatorRunService service;
    private readonly SynchronizationContext? uiContext;
    private readonly int uiThreadId;
    private PreparedRun? prepared;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? continueSource;
    private TaskCompletionSource<bool>? confirmSource;
    private FixtureControl? fixture;
    private CancellationTokenSource? monitorStop;
    private Task? monitorTask;
    // A closed adapter only starts a run after the adapter has been seen open (no restart with the same board).
    private bool fixtureArmed;
    private string? fixtureErrorText;
    // Set when opening the adapter failed; the polling retries it so a board is never left clamped.
    private FixtureLamp? pendingRelease;
    private string? fixtureStateText;
    private bool simulateReadFailure;
    private bool setupValid;
    private bool isBusy;
    private bool isRunning;
    private bool isWaitingForContinue;
    private string fauf = "";
    private string operatorName = "";
    private string serialNumber = "";
    private bool singleStepMode;
    private string? orderText;
    private string statusText = "";
    private string? errorText;
    private string? continueText;
    private string? promptMessage;
    private string? exportPath;
    private RunDisplayState resultState;
    private string? systemTypeText;
    private string? systemConfigurationText;

    public MainViewModel(OperatorRunService service, string softwareVersion)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        uiContext = SynchronizationContext.Current;
        uiThreadId = Environment.CurrentManagedThreadId;
        SoftwareVersion = softwareVersion;
        LoadOrderCommand = new RelayCommand(() => _ = LoadOrderAsync(), () => CanEdit);
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanEdit && prepared is not null && !IsAutoStart);
        SerialEnteredCommand = new RelayCommand(OnSerialEntered, () => CanEdit && (IsAutoStart || prepared is not null));
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ContinueCommand = new RelayCommand(Continue, () => IsWaitingForContinue);
        YesCommand = new RelayCommand(() => Answer(true), () => IsPromptVisible);
        NoCommand = new RelayCommand(() => Answer(false), () => IsPromptVisible);
        SimulateFixtureCloseCommand = new RelayCommand(() => fixture?.SimulateClose(), () => CanSimulateFixture && fixture is not null);
        statusText = "Einrichtung wird geprüft …";
        fixtureStateText = IsAutoStart ? "Adapter: unbekannt" : null;
    }

    /// <summary>Asks the window to focus the serial number box (after loading an order and after each run).</summary>
    public event EventHandler? SerialNumberFocusRequested;

    /// <summary>Raised when a run has finished, also after cancellation.</summary>
    public event EventHandler? RunFinished;

    public string Station => service.Settings.Station;

    /// <summary>"(PS04)" after a successful startup check; empty for stations without a type.</summary>
    public string? SystemTypeText
    {
        get => systemTypeText;
        private set => SetProperty(ref systemTypeText, value);
    }

    /// <summary>Type, devices, drivers and addresses read at startup (tooltip of the header).</summary>
    public string? SystemConfigurationText
    {
        get => systemConfigurationText;
        private set => SetProperty(ref systemConfigurationText, value);
    }
    public bool Simulated => service.Settings.SimulationEnabled;
    public string SoftwareVersion { get; }
    public ObservableCollection<StepRow> Steps { get; } = [];

    public RelayCommand LoadOrderCommand { get; }
    public RelayCommand StartCommand { get; }

    /// <summary>Enter in the serial number box: manual mode starts the run, automatic mode only takes over the number.</summary>
    public RelayCommand SerialEnteredCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ContinueCommand { get; }
    public RelayCommand YesCommand { get; }
    public RelayCommand NoCommand { get; }
    public RelayCommand SimulateFixtureCloseCommand { get; }

    /// <summary>A test adapter is configured: closing it starts the run, there is no start button.</summary>
    public bool IsAutoStart => service.Settings.IsAutoStart;
    public bool IsManualStart => !IsAutoStart;
    public bool CanSimulateFixture => Simulated && IsAutoStart;

    public string? FixtureStateText
    {
        get => fixtureStateText;
        private set => SetProperty(ref fixtureStateText, value);
    }

    /// <summary>Simulation only: the code reader finds no code, so the hand scanner has to be used.</summary>
    public bool SimulateReadFailure
    {
        get => simulateReadFailure;
        set
        {
            if (SetProperty(ref simulateReadFailure, value) && fixture is not null)
                fixture.SimulateReadFailure = value;
        }
    }

    public PreparedRun? PreparedRun => prepared;

    /// <summary>Inputs can be changed: the setup is valid and nothing is loading or running.</summary>
    public bool CanEdit => setupValid && !IsBusy;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
                RefreshCommands();
        }
    }

    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (SetProperty(ref isRunning, value))
                RefreshCommands();
        }
    }

    public bool IsWaitingForContinue
    {
        get => isWaitingForContinue;
        private set
        {
            if (SetProperty(ref isWaitingForContinue, value))
                RefreshCommands();
        }
    }

    public string Fauf
    {
        get => fauf;
        set
        {
            // A changed FAUF invalidates the loaded order.
            if (SetProperty(ref fauf, value ?? "") && prepared is not null && !IsBusy)
                ClearOrder();
        }
    }

    public string OperatorName
    {
        get => operatorName;
        set => SetProperty(ref operatorName, value ?? "");
    }

    public string SerialNumber
    {
        get => serialNumber;
        set => SetProperty(ref serialNumber, value ?? "");
    }

    public bool SingleStepMode
    {
        get => singleStepMode;
        set => SetProperty(ref singleStepMode, value);
    }

    public string? OrderText
    {
        get => orderText;
        private set => SetProperty(ref orderText, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string? ErrorText
    {
        get => errorText;
        private set => SetProperty(ref errorText, value);
    }

    public string? ContinueText
    {
        get => continueText;
        private set => SetProperty(ref continueText, value);
    }

    public string? PromptMessage
    {
        get => promptMessage;
        private set
        {
            if (SetProperty(ref promptMessage, value))
            {
                OnPropertyChanged(nameof(IsPromptVisible));
                RefreshCommands();
            }
        }
    }

    public bool IsPromptVisible => PromptMessage is not null;

    public string? ExportPath
    {
        get => exportPath;
        private set => SetProperty(ref exportPath, value);
    }

    public RunDisplayState ResultState
    {
        get => resultState;
        private set
        {
            if (SetProperty(ref resultState, value))
                OnPropertyChanged(nameof(ResultText));
        }
    }

    public string ResultText => ResultState switch
    {
        RunDisplayState.Running => "Prüfung läuft …",
        RunDisplayState.Pass => "i.O.",
        RunDisplayState.Fail => "n.i.O.",
        RunDisplayState.Error => "FEHLER",
        RunDisplayState.Aborted => "ABGEBROCHEN",
        _ => ""
    };

    /// <summary>
    /// Checks database and station once at startup; on failure all inputs stay disabled.
    /// With a test adapter it is released once, so it starts in a defined state (open, no lamp).
    /// </summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var configuration = await service.CheckSetupAsync();
            SystemTypeText = configuration.SystemType is null ? null : $"({configuration.SystemType})";
            SystemConfigurationText = configuration.ToString();
            fixture = await service.CreateFixtureAsync();
            setupValid = true;
            StatusText = IsAutoStart ? "FAUF eingeben oder scannen, dann Adapter schließen." : "FAUF eingeben oder scannen.";
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
            StatusText = "Einrichtung prüfen und Programm neu starten.";
        }
        finally
        {
            IsBusy = false;
        }
        if (fixture is not null)
        {
            fixture.SimulateReadFailure = SimulateReadFailure;
            await ReleaseFixtureAsync(FixtureLamp.None);
        }
    }

    /// <summary>Starts polling the adapter on the current (window) thread. Does nothing without an adapter.</summary>
    public void StartFixtureMonitoring()
    {
        if (fixture is null || monitorTask is not null)
            return;
        monitorStop = new CancellationTokenSource();
        monitorTask = MonitorFixtureAsync(monitorStop.Token);
    }

    /// <summary>Stops the adapter polling and brings the adapter into its safe state. Called when the window closes.</summary>
    public async Task ShutdownAsync()
    {
        if (monitorStop is not null)
        {
            await monitorStop.CancelAsync();
            if (monitorTask is not null)
                await monitorTask;
            monitorStop.Dispose();
            monitorStop = null;
            monitorTask = null;
        }
        var current = fixture;
        fixture = null;
        RefreshCommands();
        if (current is not null)
            await current.DisposeAsync();
    }

    /// <summary>
    /// Reads the adapter once. A closed adapter (after it was seen open) starts the cycle when FAUF and operator are
    /// entered; otherwise the adapter is released again with a hint. Public for tests; the window uses the monitoring loop.
    /// </summary>
    public async Task PollFixtureOnceAsync(CancellationToken cancellationToken = default)
    {
        if (fixture is null || !CanEdit)
            return;
        if (pendingRelease is { } lamp)
        {
            await ReleaseFixtureAsync(lamp);
            if (pendingRelease is not null)
                return;
        }
        bool closed;
        try
        {
            closed = await fixture.IsClosedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportFixtureErrorAsync("Adapterzustand konnte nicht gelesen werden", exception);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        ClearFixtureError();
        FixtureStateText = closed ? "Adapter geschlossen" : "Adapter offen";
        if (!closed)
        {
            fixtureArmed = true;
            return;
        }
        // Inputs may have become busy (order loading) while the adapter was read.
        if (!fixtureArmed || !CanEdit)
            return;
        fixtureArmed = false;
        var missing = MissingFixtureInput();
        if (missing is not null)
        {
            ErrorText = $"{missing}, dann den Adapter erneut schließen.";
            StatusText = "Adapter wird geöffnet.";
            await ReleaseFixtureAsync(FixtureLamp.None);
            return;
        }
        await RunFixtureCycleAsync();
    }

    public async Task LoadOrderAsync()
    {
        if (!CanEdit)
            return;
        var faufText = Fauf.Trim();
        if (faufText.Length == 0)
        {
            ErrorText = "Bitte FAUF eingeben.";
            return;
        }
        ClearOrder();
        ErrorText = null;
        IsBusy = true;
        StatusText = $"Auftrag {faufText} wird geladen …";
        try
        {
            ShowPrepared(await service.PrepareAsync(faufText));
            StatusText = ReadyText();
            SerialNumberFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorText = ToOperatorMessage(exception, faufText);
            StatusText = "Auftrag konnte nicht geladen werden.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Manual mode (no test adapter configured): starts the run with the entered serial number.</summary>
    public async Task StartAsync()
    {
        if (!CanEdit || prepared is null || IsAutoStart)
            return;
        var serial = SerialNumber.Trim();
        var operatorText = OperatorName.Trim();
        if (operatorText.Length == 0)
        {
            ErrorText = "Bitte Bediener eintragen.";
            return;
        }
        if (!service.Settings.IsValidSerialNumber(serial))
        {
            ErrorText = InvalidSerialMessage(serial);
            SerialNumberFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var token = BeginRun();
        try
        {
            // After a complete run the next board follows; after an error or abort the same board is usually tested again.
            if (await ExecuteRunAsync(serial, operatorText, token) is RunDisplayState.Pass or RunDisplayState.Fail)
                SerialNumber = "";
        }
        finally
        {
            EndRun();
        }
    }

    /// <summary>Automatic mode: Enter in the serial number box takes over a hand-scanned number for the next adapter close.</summary>
    public void AcceptSerialNumber()
    {
        if (!CanEdit)
            return;
        var serial = SerialNumber.Trim();
        if (!service.Settings.IsValidSerialNumber(serial))
        {
            ErrorText = InvalidSerialMessage(serial);
            SerialNumberFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        SerialNumber = serial;
        ErrorText = null;
        StatusText = $"Seriennummer {serial} übernommen. Adapter schließen, um die Prüfung zu starten.";
    }

    private void OnSerialEntered()
    {
        if (IsAutoStart)
            AcceptSerialNumber();
        else
            _ = StartAsync();
    }

    /// <summary>
    /// One board in the adapter: lock, wait for the contacts to settle, take the serial number (hand-scanned or read
    /// by the code reader), run, release with the result lamp. Without a valid serial number the adapter is released
    /// at once and the operator scans the number with the hand scanner.
    /// </summary>
    private async Task RunFixtureCycleAsync()
    {
        var device = fixture!;
        var operatorText = OperatorName.Trim();
        var handScanned = SerialNumber.Trim();
        var lamp = FixtureLamp.None;
        var token = BeginRun();
        try
        {
            StatusText = "Adapter wird verriegelt …";
            await device.LockAsync(token);
            await Task.Delay(service.Settings.FixtureSettleTime, token);
            var serial = handScanned.Length > 0 ? handScanned : await ReadSerialNumberAsync(device, token);
            if (serial is null)
                return;
            if (!service.Settings.IsValidSerialNumber(serial))
            {
                ResultState = RunDisplayState.None;
                ErrorText = $"{InvalidSerialMessage(serial)} Bitte erneut scannen und den Adapter wieder schließen.";
                StatusText = "Seriennummer mit dem Handscanner scannen, dann Adapter schließen.";
                return;
            }
            SerialNumber = serial;
            var state = await ExecuteRunAsync(serial, operatorText, token);
            lamp = state == RunDisplayState.Pass ? FixtureLamp.Pass : FixtureLamp.Fail;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ResultState = RunDisplayState.Aborted;
            StatusText = "Abgebrochen. Zum Prüfen den Adapter wieder schließen.";
        }
        catch (Exception exception)
        {
            ResultState = RunDisplayState.Error;
            ErrorText = $"Adapter-Steuerung: {exception.Message}";
            StatusText = "Prüfung wurde nicht ausgeführt. Zum erneuten Prüfen den Adapter wieder schließen.";
            await ResetFixtureConnectionAsync();
        }
        finally
        {
            await ReleaseFixtureAsync(lamp);
            // The board leaves the adapter; the next one gets its serial number read or scanned again.
            SerialNumber = "";
            EndRun();
        }
    }

    /// <summary>Reads the serial number with the code reader; null (and a hint for the hand scanner) when that fails.</summary>
    private async Task<string?> ReadSerialNumberAsync(FixtureControl device, CancellationToken cancellationToken)
    {
        StatusText = "Seriennummer wird gelesen …";
        string reason;
        try
        {
            var code = await device.ReadSerialAsync(cancellationToken);
            if (service.Settings.IsValidSerialNumber(code))
                return code;
            reason = $"ungültiger Code '{code}'";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
        }
        ResultState = RunDisplayState.None;
        ErrorText = $"Seriennummer konnte nicht automatisch gelesen werden ({reason}). "
            + "Bitte mit dem Handscanner scannen und den Adapter wieder schließen.";
        StatusText = "Seriennummer mit dem Handscanner scannen, dann Adapter schließen.";
        return null;
    }

    private CancellationToken BeginRun()
    {
        ErrorText = null;
        ExportPath = null;
        IsBusy = true;
        IsRunning = true;
        ResultState = RunDisplayState.Running;
        cancellation = new CancellationTokenSource();
        return cancellation.Token;
    }

    /// <summary>Prepares the order again and runs the plan. Sets result, step rows and texts; never throws.</summary>
    private async Task<RunDisplayState> ExecuteRunAsync(string serial, string operatorText, CancellationToken cancellationToken)
    {
        var faufText = prepared!.Fauf;
        try
        {
            // Prepare again before every run: the plan may have been retired or replaced since the order was loaded.
            ShowPrepared(await service.PrepareAsync(faufText, cancellationToken));
            StatusText = $"Prüfung läuft – Seriennummer {serial}";
            var current = prepared!;
            var result = await service.RunAsync(current,
                current.CreateContext(serial, operatorText, SingleStepMode), this, OnProgress, cancellationToken);

            foreach (var stepResult in result.Run.StepResults)
                FindRow(stepResult.Order)?.Apply(stepResult);
            ResultState = result.Run.Result switch
            {
                RunOutcome.Pass => RunDisplayState.Pass,
                RunOutcome.Fail => RunDisplayState.Fail,
                RunOutcome.Aborted => RunDisplayState.Aborted,
                _ => RunDisplayState.Error
            };
            ExportPath = result.ExportPath;
            if (ResultState == RunDisplayState.Error)
            {
                var failedStep = result.Run.StepResults.FirstOrDefault(step => step.Result == StepOutcome.Error);
                ErrorText = failedStep is not null
                    ? $"Schritt {failedStep.Order + 1} „{failedStep.Name}“: {failedStep.ErrorMessage}"
                    : $"Gerätefehler außerhalb eines Schritts, Details im Protokoll ({service.Settings.LogDirectory}).";
            }
            if (result.SaveError is not null)
            {
                var warning = $"Achtung: Ergebnis wurde nicht vollständig gespeichert ({result.SaveError}). Die Messwerte stehen im Protokoll ({service.Settings.LogDirectory}).";
                ErrorText = ErrorText is null ? warning : ErrorText + Environment.NewLine + warning;
            }
            var completed = ResultState is RunDisplayState.Pass or RunDisplayState.Fail;
            StatusText = (completed, IsAutoStart) switch
            {
                (true, true) => $"Seriennummer {serial}: {ResultText}. Nächste Baugruppe einlegen und Adapter schließen.",
                (true, false) => $"Seriennummer {serial}: {ResultText}. Nächste Seriennummer scannen.",
                (false, true) => $"Seriennummer {serial}: {ResultText}. Zum erneuten Prüfen den Adapter wieder schließen.",
                (false, false) => $"Seriennummer {serial}: {ResultText}. Mit Start erneut prüfen."
            };
        }
        catch (Exception exception)
        {
            ResultState = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? RunDisplayState.Aborted : RunDisplayState.Error;
            ErrorText = ResultState == RunDisplayState.Aborted ? null : ToOperatorMessage(exception, faufText);
            StatusText = "Prüfung wurde nicht ausgeführt.";
        }
        return ResultState;
    }

    private void EndRun()
    {
        cancellation?.Dispose();
        cancellation = null;
        continueSource = null;
        confirmSource = null;
        IsWaitingForContinue = false;
        ContinueText = null;
        PromptMessage = null;
        IsRunning = false;
        IsBusy = false;
        RunFinished?.Invoke(this, EventArgs.Empty);
        SerialNumberFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task MonitorFixtureAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await PollFixtureOnceAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Keep polling; a stopped loop would silently disable the automatic start.
                    ErrorText = $"Adapterüberwachung: {exception.Message}";
                }
                await Task.Delay(FixturePollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ShutdownAsync stops the loop.
        }
    }

    /// <summary>Releases the adapter (opens it, shows the lamp). Errors are shown, the polling connects again.</summary>
    private async Task ReleaseFixtureAsync(FixtureLamp lamp)
    {
        if (fixture is null)
            return;
        try
        {
            await fixture.ReleaseAsync(lamp);
            fixtureArmed = false;
            pendingRelease = null;
        }
        catch (Exception exception)
        {
            pendingRelease = lamp;
            await ReportFixtureErrorAsync("Adapter konnte nicht geöffnet werden", exception);
        }
    }

    private async Task ReportFixtureErrorAsync(string context, Exception exception)
    {
        fixtureArmed = false;
        fixtureErrorText = $"{context}: {exception.Message}";
        ErrorText = fixtureErrorText;
        FixtureStateText = "Adapter: Fehler";
        await ResetFixtureConnectionAsync();
    }

    private async Task ResetFixtureConnectionAsync()
    {
        if (fixture is null)
            return;
        try
        {
            await fixture.ResetConnectionAsync();
        }
        catch (Exception)
        {
            // The connection is dropped anyway; the next poll connects again and reports a persisting error.
        }
    }

    private void ClearFixtureError()
    {
        if (fixtureErrorText is null)
            return;
        if (ErrorText == fixtureErrorText)
            ErrorText = null;
        fixtureErrorText = null;
    }

    private string? MissingFixtureInput()
    {
        if (OperatorName.Trim().Length == 0)
            return "Bitte zuerst den Bediener eintragen";
        if (prepared is null)
            return "Bitte zuerst den Auftrag (FAUF) laden";
        if (!fixture!.HasReader && SerialNumber.Trim().Length == 0)
            return "Bitte zuerst die Seriennummer scannen";
        return null;
    }

    private string ReadyText() => (IsAutoStart, fixture?.HasReader ?? false) switch
    {
        (true, true) => "Baugruppe einlegen und Adapter schließen.",
        (true, false) => "Seriennummer scannen, Baugruppe einlegen und Adapter schließen.",
        _ => "Seriennummer scannen oder eingeben und Prüfung starten."
    };

    private string InvalidSerialMessage(string serial) =>
        service.Settings.SerialNumberPattern == OperatorSettings.DefaultSerialNumberPattern
            ? $"Ungültige Seriennummer '{serial}': Es werden genau 10 Ziffern erwartet."
            : $"Ungültige Seriennummer '{serial}' (Muster {service.Settings.SerialNumberPattern}).";

    public void Cancel()
    {
        if (cancellation is null)
            return;
        StatusText = "Prüfung wird abgebrochen, Geräte werden in den sicheren Zustand gebracht …";
        cancellation.Cancel();
    }

    public async Task WaitForContinueAsync(int stepOrder, string stepName, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            continueSource = source;
            ContinueText = $"Einzelschritt: Schritt {stepOrder + 1} „{stepName}“ mit „Weiter“ ausführen.";
            IsWaitingForContinue = true;
        });
        try
        {
            await source.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            OnUi(() =>
            {
                if (continueSource != source)
                    return;
                continueSource = null;
                ContinueText = null;
                IsWaitingForContinue = false;
            });
        }
    }

    public async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            confirmSource = source;
            PromptMessage = message;
        });
        try
        {
            return await source.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            OnUi(() =>
            {
                if (confirmSource != source)
                    return;
                confirmSource = null;
                PromptMessage = null;
            });
        }
    }

    private void Continue() => continueSource?.TrySetResult();

    private void Answer(bool answer) => confirmSource?.TrySetResult(answer);

    private void OnProgress(SequenceProgress progress) => OnUi(() =>
    {
        var row = FindRow(progress.StepOrder);
        if (row is null)
            return;
        if (progress.Result is not null)
        {
            row.Apply(progress.Result);
        }
        else if (progress.State == "started")
        {
            row.MarkRunning();
            StatusText = $"Schritt {progress.StepOrder + 1} von {progress.StepCount}: {progress.StepName}";
        }
    });

    private void ShowPrepared(PreparedRun run)
    {
        prepared = run;
        OrderText = $"Artikel {run.Order.ArtNr} · FZ {run.Order.Fz} · Prüfplan Version {run.Plan.PlanVersion} · {run.Plan.Steps.Count} Schritte";
        Steps.Clear();
        foreach (var step in run.Plan.Steps)
            Steps.Add(new StepRow(step));
        OnPropertyChanged(nameof(PreparedRun));
        RefreshCommands();
    }

    private void ClearOrder()
    {
        prepared = null;
        OrderText = null;
        ExportPath = null;
        ResultState = RunDisplayState.None;
        Steps.Clear();
        OnPropertyChanged(nameof(PreparedRun));
        RefreshCommands();
    }

    private StepRow? FindRow(int order) => Steps.FirstOrDefault(row => row.Order == order);

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        LoadOrderCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        SerialEnteredCommand.RaiseCanExecuteChanged();
        SimulateFixtureCloseCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
        YesCommand.RaiseCanExecuteChanged();
        NoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Runs on the window thread; the sequencer calls progress and interaction from a worker thread.</summary>
    private void OnUi(Action action)
    {
        if (uiContext is null || Environment.CurrentManagedThreadId == uiThreadId)
            action();
        else
            uiContext.Post(_ => action(), null);
    }

    private static string ToOperatorMessage(Exception exception, string faufText) => exception switch
    {
        SapOrderNotFoundException => $"FAUF '{faufText}' wurde in SAP nicht gefunden.",
        SapException => $"SAP-Abfrage fehlgeschlagen: {exception.Message}",
        _ => exception.Message
    };
}
