using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace Testpilot.Operator;

/// <summary>Operator window. All logic is in <see cref="MainViewModel"/>; this class only handles focus and closing.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool closeAfterRun;
    private bool shuttingDown;
    private bool shutdownDone;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SerialNumberFocusRequested += (_, _) => FocusLater(SerialBox);
        viewModel.RunFinished += OnRunFinished;
        Loaded += (_, _) => FocusLater(string.IsNullOrWhiteSpace(viewModel.OperatorName) ? OperatorBox : FaufBox);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The window must not close during a run: cancel it, let the sequencer bring the devices
        // into the safe state, then close (see OnRunFinished).
        if (viewModel.IsRunning)
        {
            e.Cancel = true;
            closeAfterRun = true;
            viewModel.Cancel();
        }
        else if (!shutdownDone)
        {
            // Stop the adapter polling and release the adapter before the window closes.
            e.Cancel = true;
            if (!shuttingDown)
            {
                shuttingDown = true;
                _ = ShutdownAndCloseAsync();
            }
        }
        base.OnClosing(e);
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            await viewModel.ShutdownAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Test adapter could not be brought into its safe state while closing");
        }
        shutdownDone = true;
        // Close may not be called from inside OnClosing (ShutdownAsync can complete synchronously).
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void OnRunFinished(object? sender, EventArgs e)
    {
        if (closeAfterRun)
            Dispatcher.BeginInvoke(Close);
    }

    // The text box is disabled while a run is active; focus it after the binding has enabled it again.
    private void FocusLater(UIElement element) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            element.Focus();
            if (element is System.Windows.Controls.TextBox textBox)
                textBox.SelectAll();
        });
}
