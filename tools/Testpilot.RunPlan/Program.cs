using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Sap;
using Testpilot.Sequencer;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var arguments = ParseArguments(args);
    var configuration = LoadConfiguration();
    var logDirectory = configuration["Logging:Directory"] ?? "logs";
    Directory.CreateDirectory(logDirectory);
    var logPath = Path.Combine(logDirectory,
        $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}.log");
    Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().Enrich.FromLogContext()
        .WriteTo.File(logPath, shared: false).CreateLogger();
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.ClearProviders().SetMinimumLevel(LogLevel.Information)
        .AddSerilog(Log.Logger, dispose: false));
    services.AddTestpilotData(configuration);
    await using var serviceProvider = services.BuildServiceProvider();
    var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
    var database = serviceProvider.GetRequiredService<TestpilotDbContext>();

    var simulationEnabled = bool.TryParse(configuration["Simulation:Enabled"], out var configuredSimulation)
        ? configuredSimulation : true;
    var registry = new DriverRegistry().AddAllDrivers();
    var fauf = arguments.Fauf ?? ReadRequired("FAUF");
    var sapClient = SapClientFactory.Create(configuration);
    if (sapClient is MockSapClient mock && arguments.PlanId is { } selectedPlanId && !mock.Contains(fauf))
    {
        // Desk testing with --plan: the SAP mock accepts any FAUF for the selected plan.
        var planKey = await database.TestPlans.AsNoTracking().Where(item => item.Id == selectedPlanId)
            .Select(item => new OrderInfo(item.ArtNr, item.Fz)).SingleOrDefaultAsync();
        if (planKey is not null)
            mock.AddOrder(fauf, planKey);
    }
    var preparation = new RunPreparation(database, sapClient, registry, simulationEnabled);
    var prepared = arguments.PlanId is { } planId
        ? await preparation.PrepareForPlanAsync(arguments.Station, planId, fauf, cancellation.Token)
        : await preparation.PrepareForOrderAsync(arguments.Station, fauf, cancellation.Token);
    Console.WriteLine(prepared);
    var host = new DriverHost(registry, loggerFactory);

    var serial = arguments.Serial ?? ReadRequired("Seriennummer");
    var operatorName = arguments.Operator ?? ReadRequired("Bediener");
    var exporter = new RunJsonExporter(
        configuration["Export:Directory"] ?? "exports", loggerFactory.CreateLogger<RunJsonExporter>());
    var customSteps = new CustomStepRegistry();
    var stepExecutors = new StepExecutorRegistry(customSteps);
    var interaction = new ConsoleSequenceInteraction();
    var disableTimeouts = bool.TryParse(configuration["Debug:DisableTimeouts"], out var configuredDisableTimeouts)
        && configuredDisableTimeouts;
    var sequenceOptions = new SequenceOptions(
        TimeSpan.FromSeconds(ReadSeconds(configuration, "Sequencer:OperationTimeoutSeconds", 30)), disableTimeouts)
    {
        LongOperationTimeout = TimeSpan.FromSeconds(ReadSeconds(configuration, "Sequencer:LongOperationTimeoutSeconds", 600))
    };
    var runner = new SequenceRunner(host, database, exporter, loggerFactory.CreateLogger<SequenceRunner>(),
        stepExecutors, interaction, sequenceOptions);
    runner.ProgressChanged += (_, eventArgs) => Console.WriteLine(
        $"[{eventArgs.Progress.StepOrder + 1}/{eventArgs.Progress.StepCount}] {eventArgs.Progress.StepName}: {eventArgs.Progress.State}");

    var result = await runner.RunAsync(prepared.Plan,
        prepared.CreateContext(serial, operatorName, arguments.SingleStep), cancellation.Token);
    Console.WriteLine($"Lauf {result.Run.Id}: {result.Run.Result}; Export: {result.ExportPath ?? "-"}");
    if (result.SaveError is not null)
        Console.Error.WriteLine($"Achtung, Ergebnis nicht vollständig gespeichert: {result.SaveError}");
    return result.Run.Result == RunOutcome.Pass && result.SaveError is null ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    Log.CloseAndFlush();
}

static (Guid? PlanId, string Station, string? Fauf, string? Serial, string? Operator, bool SingleStep) ParseArguments(string[] args)
{
    Guid? planId = null;
    string? station = null, fauf = null, serial = null, operatorName = null;
    var singleStep = false;
    for (var index = 0; index < args.Length; index++)
    {
        var value = args[index];
        if (value == "--single-step")
        {
            singleStep = true;
            continue;
        }
        if (value is not ("--plan" or "--station" or "--fauf" or "--serial" or "--operator") || index + 1 >= args.Length)
            throw new ArgumentException("Verwendung: Testpilot.RunPlan --station <Name> [--plan <GUID>] [--fauf <FAUF>] [--serial <Nr>] [--operator <Name>] [--single-step]");
        var argument = args[++index];
        switch (value)
        {
            case "--plan":
                planId = Guid.TryParse(argument, out var parsed) ? parsed : throw new ArgumentException("--plan muss eine GUID sein.");
                break;
            case "--station": station = argument; break;
            case "--fauf": fauf = argument; break;
            case "--serial": serial = argument; break;
            case "--operator": operatorName = argument; break;
        }
    }
    if (string.IsNullOrWhiteSpace(station))
        throw new ArgumentException("--station <Name> ist erforderlich.");
    return (planId, station, fauf, serial, operatorName, singleStep);
}

static IConfiguration LoadConfiguration()
{
    var configuration = new ConfigurationBuilder();
    var publishedSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (File.Exists(publishedSettings))
        configuration.AddJsonFile(publishedSettings, optional: false);
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); !File.Exists(publishedSettings)
         && directory is not null; directory = directory.Parent)
    {
        var settings = Path.Combine(directory.FullName, "src", "Testpilot.Editor", "appsettings.json");
        if (File.Exists(settings))
        {
            configuration.AddJsonFile(settings, optional: false);
            break;
        }
    }
    return configuration.AddEnvironmentVariables().Build();
}

static int ReadSeconds(IConfiguration configuration, string key, int defaultSeconds)
{
    var text = configuration[key];
    if (string.IsNullOrWhiteSpace(text))
        return defaultSeconds;
    return int.TryParse(text, out var seconds) && seconds is >= 1 and <= 3600
        ? seconds
        : throw new ArgumentException($"{key} muss eine Zahl zwischen 1 und 3600 sein.");
}

static string ReadRequired(string label)
{
    Console.Write($"{label}: ");
    var value = Console.ReadLine();
    ArgumentException.ThrowIfNullOrWhiteSpace(value, label);
    return value;
}
