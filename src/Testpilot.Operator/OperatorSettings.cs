using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Testpilot.Operator;

/// <summary>Settings of one operator workstation, read from appsettings.json and environment variables.</summary>
public sealed record OperatorSettings(string Station, bool SimulationEnabled, bool DisableTimeouts,
    TimeSpan OperationTimeout, string ExportDirectory, string LogDirectory, string SerialNumberPattern)
{
    public const string DefaultSerialNumberPattern = "^[0-9]{10}$";
    public const string DefaultSimulatedSerialNumber = "1234567890";

    /// <summary>
    /// Device key of the test adapter (station device with capability TestFixture). Empty: manual start with F5.
    /// Set: closing the adapter starts the run automatically.
    /// </summary>
    public string? FixtureDevice { get; init; }

    /// <summary>Device key of the fixed code reader that reads the serial number when the adapter closes. Empty: hand scanner only.</summary>
    public string? SerialReaderDevice { get; init; }

    /// <summary>Wait time after locking the adapter (legacy Seriennummer.c: 500 ms for the later resistance measurements).</summary>
    public TimeSpan FixtureSettleTime { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Serial number returned by the simulated code reader.</summary>
    public string SimulatedSerialNumber { get; init; } = DefaultSimulatedSerialNumber;

    /// <summary>Timeout of Program, External and CustomCode steps (Sequencer:LongOperationTimeoutSeconds, default 600).</summary>
    public TimeSpan LongOperationTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public bool IsAutoStart => FixtureDevice is not null;

    /// <summary>
    /// Reads the settings. A missing <c>Simulation:Enabled</c> means real hardware (false), so a production
    /// station cannot record simulated results by accident.
    /// </summary>
    public static OperatorSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var station = configuration["Operator:Station"];
        if (string.IsNullOrWhiteSpace(station))
            throw new InvalidOperationException("Operator:Station ist nicht konfiguriert (Name des Prüfsystems aus dem Editor, z. B. PS04-3).");
        var pattern = configuration["Operator:SerialNumberPattern"];
        if (string.IsNullOrWhiteSpace(pattern))
            pattern = DefaultSerialNumberPattern;
        try
        {
            _ = new Regex(pattern);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"Operator:SerialNumberPattern ist kein gültiger regulärer Ausdruck: {exception.Message}");
        }
        var fixtureDevice = ReadOptional(configuration, "Operator:FixtureDevice");
        var serialReaderDevice = ReadOptional(configuration, "Operator:SerialReaderDevice");
        if (serialReaderDevice is not null && fixtureDevice is null)
            throw new InvalidOperationException("Operator:SerialReaderDevice braucht Operator:FixtureDevice (der Leser liest beim Schließen des Adapters).");
        var settleText = ReadOptional(configuration, "Operator:FixtureSettleMilliseconds");
        var settleMilliseconds = 500;
        if (settleText is not null && (!int.TryParse(settleText, out settleMilliseconds) || settleMilliseconds is < 0 or > 10_000))
            throw new InvalidOperationException("Operator:FixtureSettleMilliseconds muss eine Zahl zwischen 0 und 10000 sein.");
        var simulatedSerial = ReadOptional(configuration, "Simulation:SerialNumber") ?? DefaultSimulatedSerialNumber;
        var operationTimeout = ReadSeconds(configuration, "Sequencer:OperationTimeoutSeconds", 30);
        var longOperationTimeout = ReadSeconds(configuration, "Sequencer:LongOperationTimeoutSeconds", 600);
        return new OperatorSettings(
            station.Trim(),
            ReadBool(configuration, "Simulation:Enabled"),
            ReadBool(configuration, "Debug:DisableTimeouts"),
            operationTimeout,
            configuration["Export:Directory"] ?? "exports",
            configuration["Logging:Directory"] ?? "logs",
            pattern)
        {
            FixtureDevice = fixtureDevice,
            SerialReaderDevice = serialReaderDevice,
            FixtureSettleTime = TimeSpan.FromMilliseconds(settleMilliseconds),
            SimulatedSerialNumber = simulatedSerial,
            LongOperationTimeout = longOperationTimeout
        };
    }

    public bool IsValidSerialNumber(string serialNumber) =>
        Regex.IsMatch(serialNumber, SerialNumberPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>appsettings.json next to the executable, overridable by environment variables (e.g. Operator__Station).</summary>
    public static IConfiguration LoadConfiguration() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
        .AddEnvironmentVariables()
        .Build();

    private static string? ReadOptional(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static TimeSpan ReadSeconds(IConfiguration configuration, string key, int defaultSeconds)
    {
        var text = ReadOptional(configuration, key);
        if (text is null)
            return TimeSpan.FromSeconds(defaultSeconds);
        return int.TryParse(text, out var seconds) && seconds is >= 1 and <= 3600
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException($"{key} muss eine Zahl zwischen 1 und 3600 sein.");
    }

    private static bool ReadBool(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"{key} muss true oder false sein.");
    }
}
