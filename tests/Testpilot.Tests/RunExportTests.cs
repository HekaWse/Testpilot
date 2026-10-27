using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;

namespace Testpilot.Tests;

public sealed class RunExportTests
{
    private static Run CompletedRun() => new()
    {
        Fauf = "123456", ArtNr = "4711", Fz = "A", SerialNumber = "SN001", Station = "Bench1", Operator = "Tester",
        PlanVersion = 3, SoftwareVersion = SoftwareVersion.Get(typeof(Run).Assembly),
        StartedAt = DateTimeOffset.Parse("2026-10-27T09:00:00Z"), FinishedAt = DateTimeOffset.Parse("2026-10-27T09:00:02Z"), Result = RunOutcome.Pass,
        Devices = [new RunDevice { DeviceKey = "dmm1", Identification = "Simulated DMM" }],
        StepResults = [new RunStepResult { Order = 0, Name = "VCC_3V3", StepType = StepType.Measure,
            DeviceKey = "dmm1", Channel = 1003, Function = "VOLT:DC", Value = 3.298, Unit = MeasurementUnit.Volt,
            LimitLow = 3.2, LimitHigh = 3.4, Result = StepOutcome.Pass,
            StartedAt = DateTimeOffset.Parse("2026-10-27T09:00:01Z"), FinishedAt = DateTimeOffset.Parse("2026-10-27T09:00:02Z") }]
    };

    [Fact]
    public async Task ExportMatchesContractAndPersistedSiSnapshots()
    {
        await using var db = await TestDatabase.CreateAsync();
        var run = CompletedRun();
        db.Context.Add(run);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        var persisted = await db.Context.Runs.Include(x => x.Devices).Include(x => x.StepResults).SingleAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"testpilot-export-{Guid.NewGuid():N}");
        try
        {
            var exporter = new RunJsonExporter(directory, NullLogger<RunJsonExporter>.Instance);
            var path = await exporter.ExportAsync(persisted);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = json.RootElement;
            Assert.Equal(new[] { "run", "devices", "characteristics" }, root.EnumerateObject().Select(x => x.Name));
            var header = root.GetProperty("run");
            Assert.Equal("123456", header.GetProperty("fauf").GetString());
            Assert.Equal("4711", header.GetProperty("artnr").GetString());
            Assert.Equal("A", header.GetProperty("fz").GetString());
            Assert.Equal("SN001", header.GetProperty("serial").GetString());
            Assert.Equal("Bench1", header.GetProperty("station").GetString());
            Assert.Equal("Tester", header.GetProperty("operator").GetString());
            Assert.Equal(3, header.GetProperty("planVersion").GetInt32());
            Assert.Equal(run.SoftwareVersion, header.GetProperty("softwareVersion").GetString());
            Assert.Equal(run.StartedAt, header.GetProperty("started").GetDateTimeOffset());
            Assert.Equal(run.FinishedAt, header.GetProperty("finished").GetDateTimeOffset());
            Assert.Equal("pass", header.GetProperty("result").GetString());
            Assert.Equal("dmm1", root.GetProperty("devices")[0].GetProperty("id").GetString());
            Assert.Equal("Simulated DMM", root.GetProperty("devices")[0].GetProperty("idn").GetString());
            var characteristic = root.GetProperty("characteristics")[0];
            Assert.Equal("VCC_3V3", characteristic.GetProperty("name").GetString());
            Assert.Equal("dmm1", characteristic.GetProperty("device").GetString());
            Assert.Equal(1003, characteristic.GetProperty("channel").GetInt32());
            Assert.Equal("VOLT:DC", characteristic.GetProperty("function").GetString());
            Assert.Equal(3.298, characteristic.GetProperty("value").GetDouble());
            Assert.Equal("V", characteristic.GetProperty("unit").GetString());
            Assert.Equal(3.2, characteristic.GetProperty("low").GetDouble());
            Assert.Equal(3.4, characteristic.GetProperty("high").GetDouble());
            Assert.Equal("pass", characteristic.GetProperty("result").GetString());
            await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(persisted));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DocumentIsAnOrderedSnapshotIndependentOfEntities()
    {
        var run = CompletedRun();
        run.StepResults.Insert(0, new RunStepResult { Order = 1, Name = "Later", StepType = StepType.Wait, Result = StepOutcome.Skipped });
        var document = RunJsonExporter.CreateDocument(run);
        run.StepResults.Single(x => x.Order == 0).LimitHigh = 100;
        Assert.Equal(3.4, document.Characteristics[0].High);
        Assert.Equal("Later", document.Characteristics[1].Name);
        Assert.Contains("Pass", document.Characteristics[0].ToString());
    }

    [Fact]
    public void UnfinishedOrNonFiniteRunsAreRejected()
    {
        var run = CompletedRun();
        run.FinishedAt = null;
        Assert.Throws<InvalidOperationException>(() => RunJsonExporter.CreateDocument(run));
        run = CompletedRun();
        run.StepResults[0].Value = double.NaN;
        Assert.Throws<InvalidOperationException>(() => RunJsonExporter.CreateDocument(run));
    }

    [Fact]
    public async Task CancelledExportLeavesNoPartialFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"testpilot-cancel-{Guid.NewGuid():N}");
        try
        {
            var exporter = new RunJsonExporter(directory, NullLogger<RunJsonExporter>.Instance);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(CompletedRun(), cancellation.Token));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
