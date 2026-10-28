using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Sequencer;

namespace Testpilot.Operator;

/// <summary>Startup: configuration, application log, database access, drivers and the main window.</summary>
public partial class App : Application
{
    private ServiceProvider? services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        MainViewModel viewModel;
        try
        {
            var configuration = OperatorSettings.LoadConfiguration();
            var settings = OperatorSettings.FromConfiguration(configuration);
            Directory.CreateDirectory(settings.LogDirectory);
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(Path.Combine(settings.LogDirectory, "operator-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
                .CreateLogger();
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging(builder => builder.ClearProviders().AddSerilog(Log.Logger, dispose: false));
            serviceCollection.AddTestpilotData(configuration);
            services = serviceCollection.BuildServiceProvider();

            var runService = new OperatorRunService(services.GetRequiredService<IServiceScopeFactory>(),
                SapClientFactory.Create(configuration), new DriverRegistry().AddAllDrivers(), settings,
                new CustomStepRegistry(), services.GetRequiredService<ILoggerFactory>());
            var version = Testpilot.Core.SoftwareVersion.Get(Assembly.GetEntryAssembly() ?? typeof(App).Assembly);
            Log.Information("Operator started: station {Station}, simulation {Simulation}, fixture {Fixture}, reader {Reader}, version {Version}",
                settings.Station, settings.SimulationEnabled, settings.FixtureDevice, settings.SerialReaderDevice, version);
            viewModel = new MainViewModel(runService, version);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Operator startup failed");
            MessageBox.Show(exception.Message, "Testpilot – Startfehler", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        await viewModel.InitializeAsync();
        viewModel.StartFixtureMonitoring();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        services?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception in the operator window");
        MessageBox.Show(e.Exception.Message, "Testpilot – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
