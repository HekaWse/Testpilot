using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;
using Testpilot.Sap;
using Testpilot.Sequencer;

namespace Testpilot.Operator;

/// <summary>
/// Connects the operator window to the sequencer: prepares a run for a FAUF and executes it.
/// Every call uses its own database scope; every run writes its own trace file (logs\run-*.log).
/// </summary>
public sealed class OperatorRunService(IServiceScopeFactory scopes, ISapClient sapClient, DriverRegistry registry,
    OperatorSettings settings, CustomStepRegistry customSteps, ILoggerFactory loggerFactory)
{
    public OperatorSettings Settings => settings;

    /// <summary>
    /// Creates the adapter control for the automatic start (null when Operator:FixtureDevice is empty).
    /// The devices come from the station in the database; with Simulation:Enabled they are replaced by
    /// <c>SimulatedFixture</c> and a <c>SimulatedBarcodeScanner</c> that returns Simulation:SerialNumber.
    /// Nothing is connected here; the first adapter call connects.
    /// </summary>
    public async Task<FixtureControl?> CreateFixtureAsync(CancellationToken cancellationToken = default)
    {
        if (settings.FixtureDevice is null)
            return null;
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TestpilotDbContext>();
        var station = await database.Stations.AsNoTracking().Include(item => item.Devices)
            .SingleOrDefaultAsync(item => item.Name == settings.Station, cancellationToken)
            ?? throw new InvalidOperationException($"Prüfsystem '{settings.Station}' ist in der Datenbank nicht angelegt.");

        var fixture = ReadDevice(station, settings.FixtureDevice, DeviceCapability.TestFixture, "Operator:FixtureDevice");
        var reader = settings.SerialReaderDevice is null
            ? null
            : ReadDevice(station, settings.SerialReaderDevice, DeviceCapability.BarcodeScanner, "Operator:SerialReaderDevice");
        if (settings.SimulationEnabled)
        {
            fixture = DriverConfiguration.Parse(fixture.DeviceKey, "SimulatedFixture", "{}");
            if (reader is not null)
                reader = DriverConfiguration.Parse(reader.DeviceKey, "SimulatedBarcodeScanner",
                    JsonSerializer.Serialize(new { barcode = settings.SimulatedSerialNumber }));
        }
        return new FixtureControl(new DriverHost(registry, loggerFactory), fixture, reader,
            settings.DisableTimeouts ? null : settings.OperationTimeout);
    }

    private DriverConfiguration ReadDevice(Station station, string deviceKey, DeviceCapability capability, string settingName)
    {
        var device = station.Devices.SingleOrDefault(item => item.DeviceKey == deviceKey)
            ?? throw new InvalidOperationException(
                $"{settingName}: Gerät '{deviceKey}' ist an Prüfsystem '{station.Name}' nicht eingetragen (Editor > Prüfsysteme).");
        DriverConfiguration configuration;
        DeviceProfile profile;
        try
        {
            configuration = DriverConfiguration.Parse(device.DeviceKey, device.Driver, device.ConnectionParametersJson);
            profile = registry.GetProfile(configuration);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{settingName}: Gerät '{deviceKey}' ist ungültig konfiguriert: {exception.Message}", exception);
        }
        if (!profile.Capabilities.Contains(capability))
            throw new InvalidOperationException(
                $"{settingName}: Gerät '{deviceKey}' (Treiber {device.Driver}) hat nicht die Fähigkeit {capability}.");
        return configuration;
    }

    /// <summary>
    /// Checks the database schema and reads the configuration of the test system named in Operator:Station
    /// (type, devices, drivers, addresses) before the first order is entered. The result is also logged.
    /// </summary>
    public async Task<SystemConfiguration> CheckSetupAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TestpilotDbContext>();
        var pending = await database.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pending.Any())
            throw new InvalidOperationException(
                "Die Datenbank ist nicht auf dem aktuellen Stand. Bitte zuerst 'Testpilot.Editor --migrate' mit derselben Datenbank ausführen.");
        var station = await database.Stations.AsNoTracking().Include(item => item.Devices)
            .SingleOrDefaultAsync(item => item.Name == settings.Station, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Prüfsystem '{settings.Station}' ist in der Datenbank nicht angelegt (Editor > Prüfsysteme, Einstellung Operator:Station).");
        var type = station.TestSystemTypeId is Guid typeId
            ? await database.TestSystemTypes.AsNoTracking().Include(item => item.Devices)
                .SingleOrDefaultAsync(item => item.Id == typeId, cancellationToken)
            : null;
        var devices = station.Devices.OrderBy(device => device.DeviceKey, StringComparer.Ordinal)
            .Select(device => new SystemDeviceInfo(device.DeviceKey,
                type?.Devices.SingleOrDefault(item => item.DeviceKey == device.DeviceKey)?.Model,
                device.Driver, ConnectionAddress.Describe(device.ConnectionParametersJson)))
            .ToList();
        var configuration = new SystemConfiguration(station.Name, type?.Name, devices);
        loggerFactory.CreateLogger<OperatorRunService>().LogInformation("System-Konfiguration: {Configuration}", configuration);
        return configuration;
    }

    public async Task<PreparedRun> PrepareAsync(string fauf, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TestpilotDbContext>();
        var preparation = new RunPreparation(database, sapClient, registry, settings.SimulationEnabled);
        return await preparation.PrepareForOrderAsync(settings.Station, fauf, cancellationToken);
    }

    /// <summary>
    /// Runs the prepared plan on a worker thread so that the window stays responsive.
    /// <paramref name="progress"/> and <paramref name="interaction"/> are called on that worker thread.
    /// </summary>
    public Task<SequenceRunResult> RunAsync(PreparedRun prepared, RunContext context, ISequenceInteraction interaction,
        Action<SequenceProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(progress);
        return Task.Run(() => RunOnWorkerAsync(prepared, context, interaction, progress, cancellationToken),
            CancellationToken.None);
    }

    private async Task<SequenceRunResult> RunOnWorkerAsync(PreparedRun prepared, RunContext context,
        ISequenceInteraction interaction, Action<SequenceProgress> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.LogDirectory);
        var logPath = Path.Combine(settings.LogDirectory,
            $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{ToFileName(context.SerialNumber)}.log");
        using var runLog = new LoggerConfiguration().MinimumLevel.Debug().Enrich.FromLogContext()
            .WriteTo.File(logPath, shared: false).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(runLog);
        var logger = loggerFactory.CreateLogger<OperatorRunService>();
        logger.LogInformation("Operator run: {PreparedRun}, serial {Serial}, operator {Operator}",
            prepared, context.SerialNumber, context.Operator);

        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TestpilotDbContext>();
        var runner = new SequenceRunner(new DriverHost(registry, loggerFactory), database,
            new RunJsonExporter(settings.ExportDirectory, loggerFactory.CreateLogger<RunJsonExporter>()),
            loggerFactory.CreateLogger<SequenceRunner>(), new StepExecutorRegistry(customSteps), interaction,
            new SequenceOptions(settings.OperationTimeout, settings.DisableTimeouts)
                {
                    LongOperationTimeout = settings.LongOperationTimeout
                });
        runner.ProgressChanged += (_, eventArgs) => progress(eventArgs.Progress);
        return await runner.RunAsync(prepared.Plan, context, cancellationToken);
    }

    private static string ToFileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = text.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(characters);
    }
}
