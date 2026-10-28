using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Testpilot.Core;

public sealed record RunExportDocument(RunExportHeader Run, IReadOnlyList<DeviceExport> Devices, IReadOnlyList<CharacteristicExport> Characteristics);
public sealed record RunExportHeader(string Fauf, [property: JsonPropertyName("artnr")] string ArtNr, string Fz,
    string Serial, string Station, string Operator, int PlanVersion, string SoftwareVersion,
    DateTimeOffset Started, DateTimeOffset Finished, RunOutcome Result, bool Simulated = false);
public sealed record DeviceExport(string Id, string Idn);
public sealed record CharacteristicExport(string Name, string? Device, int? Channel, string? Function,
    double? Value, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextValue,
    string? Unit, double? Low, double? High, StepOutcome Result,
    DateTimeOffset Started, DateTimeOffset Finished, string? Error);

public static class SoftwareVersion
{
    public static string Get(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("The assembly has no software version.");
}

public sealed class RunJsonExporter(string directory, ILogger<RunJsonExporter> logger)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Keeps Ω, µ and umlauts readable for other programs; the file is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static RunExportDocument CreateDocument(Run run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.FinishedAt is null || run.Result is null || run.FinishedAt < run.StartedAt)
            throw new InvalidOperationException("Only completed runs with valid timestamps can be exported.");
        ArgumentException.ThrowIfNullOrWhiteSpace(run.SoftwareVersion);
        foreach (var step in run.StepResults)
        {
            LimitChecker.Validate(step.LimitLow, step.LimitHigh);
            if (step.Value.HasValue && step.TextValue is not null)
                throw new InvalidOperationException("A step cannot contain both a numeric and text result.");
            if (step.Value.HasValue && !double.IsFinite(step.Value.Value))
                throw new InvalidOperationException("Non-finite measurements must be stored as null with an error result.");
            if (step.FinishedAt < step.StartedAt)
                throw new InvalidOperationException("A step cannot finish before it starts.");
        }

        return new(
            new(run.Fauf, run.ArtNr, run.Fz, run.SerialNumber, run.Station, run.Operator,
                run.PlanVersion, run.SoftwareVersion, run.StartedAt, run.FinishedAt.Value, run.Result.Value, run.Simulated),
            run.Devices.OrderBy(device => device.DeviceKey, StringComparer.Ordinal)
                .Select(device => new DeviceExport(device.DeviceKey, device.Identification)).ToArray(),
            run.StepResults.OrderBy(step => step.Order)
                .Select(step => new CharacteristicExport(step.Name, step.DeviceKey, step.Channel, step.Function,
                    step.Value, step.TextValue, step.Unit.HasValue ? Units.Symbol(step.Unit.Value) : null,
                    step.LimitLow, step.LimitHigh, step.Result, step.StartedAt, step.FinishedAt, step.ErrorMessage)).ToArray());
    }

    /// <summary>Writes a complete snapshot atomically. Existing exports are never overwritten.</summary>
    public async Task<string> ExportAsync(Run run, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var document = CreateDocument(run);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{run.Id:N}.json");
        var temporary = Path.Combine(directory, $".{run.Id:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target);
            logger.LogInformation("Exported run {RunId} to {ExportPath}", run.Id, target);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
