using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Data;

public class TestpilotDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<TestSystemType> TestSystemTypes => Set<TestSystemType>();
    public DbSet<SystemTypeDevice> SystemTypeDevices => Set<SystemTypeDevice>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<StationDevice> StationDevices => Set<StationDevice>();
    public DbSet<TestPlan> TestPlans => Set<TestPlan>();
    public DbSet<TestStep> TestSteps => Set<TestStep>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunDevice> RunDevices => Set<RunDevice>();
    public DbSet<RunStepResult> RunStepResults => Set<RunStepResult>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TestSystemType>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasMany(x => x.Devices).WithOne().HasForeignKey(x => x.TestSystemTypeId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SystemTypeDevice>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.DeviceKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
            entity.Property(x => x.OptionsJson).IsRequired();
            entity.HasIndex(x => new { x.TestSystemTypeId, x.DeviceKey }).IsUnique();
        });
        model.Entity<Station>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasOne<TestSystemType>().WithMany().HasForeignKey(x => x.TestSystemTypeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Devices).WithOne().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<StationDevice>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.DeviceKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Driver).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ConnectionParametersJson).IsRequired();
            entity.HasIndex(x => new { x.StationId, x.DeviceKey }).IsUnique();
        });
        model.Entity<TestPlan>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.ArtNr).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Fz).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Status).IsConcurrencyToken();
            entity.Property(x => x.UpdatedAt).IsConcurrencyToken();
            entity.HasIndex(x => new { x.ArtNr, x.Fz, x.Version }).IsUnique();
            var filter = Database.IsSqlServer() ? "[Status] = 1" : "\"Status\" = 1";
            entity.HasIndex(x => new { x.ArtNr, x.Fz }).IsUnique().HasFilter(filter)
                .HasDatabaseName("IX_TestPlans_OneReleasedVersion");
            entity.HasOne<TestSystemType>().WithMany().HasForeignKey(x => x.TestSystemTypeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.TestPlanId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<TestStep>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.DeviceKey).HasMaxLength(100);
            entity.Property(x => x.ParametersJson).IsRequired();
            entity.HasIndex(x => new { x.TestPlanId, x.Order }).IsUnique();
        });
        model.Entity<Run>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Fauf).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ArtNr).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Fz).HasMaxLength(100).IsRequired();
            entity.Property(x => x.SerialNumber).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Station).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Operator).HasMaxLength(200).IsRequired();
            entity.Property(x => x.SoftwareVersion).HasMaxLength(200).IsRequired();
            entity.HasMany(x => x.Devices).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.StepResults).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<RunDevice>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.DeviceKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Identification).HasMaxLength(1000).IsRequired();
            entity.HasIndex(x => new { x.RunId, x.DeviceKey }).IsUnique();
        });
        model.Entity<RunStepResult>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.DeviceKey).HasMaxLength(100);
            entity.Property(x => x.Function).HasMaxLength(20);
            entity.Property(x => x.TextValue).HasMaxLength(4000);
            entity.HasIndex(x => new { x.RunId, x.Order }).IsUnique();
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await ValidateChangesAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private async Task ValidateChangesAsync(CancellationToken cancellationToken)
    {
        ChangeTracker.DetectChanges();
        var plans = ChangeTracker.Entries<TestPlan>().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        foreach (var entry in plans)
        {
            var plan = entry.Entity;
            var stored = entry.State == EntityState.Added ? null
                : await TestPlans.AsNoTracking().SingleAsync(x => x.Id == plan.Id, cancellationToken).ConfigureAwait(false);
            if (stored is not null && stored.Status != PlanStatus.Draft)
            {
                var retirement = stored.Status == PlanStatus.Released && plan.Status == PlanStatus.Retired;
                if (entry.State == EntityState.Deleted || (!retirement && plan.Status != stored.Status)
                    || entry.Properties.Any(property => property.Metadata.Name is not nameof(TestPlan.Status) and not nameof(TestPlan.UpdatedAt)
                        && !Equals(property.CurrentValue, property.Metadata.PropertyInfo!.GetValue(stored))))
                    throw new PlanConflictException("Freigegebene und zurückgezogene Pläne sind nicht änderbar. Bitte eine neue Version daraus erstellen.");
            }
            if (entry.State == EntityState.Deleted) continue;
            ArgumentException.ThrowIfNullOrWhiteSpace(plan.ArtNr);
            ArgumentException.ThrowIfNullOrWhiteSpace(plan.Fz);
            if (plan.Version <= 0 || !Enum.IsDefined(plan.Status)) throw new ArgumentException("Invalid plan version or status.");
            if (entry.State == EntityState.Added && plan.Status != PlanStatus.Draft)
                throw new PlanConflictException("Neue Pläne müssen Entwürfe sein.");
            if (plan.Status == PlanStatus.Draft && (plan.ExecutionPlanJson is not null || plan.ReleasedAt is not null))
                throw new PlanConflictException("Ein Entwurf darf keinen eingefrorenen Ausführungsplan enthalten.");
            if (plan.Status == PlanStatus.Retired && stored?.Status is not (PlanStatus.Released or PlanStatus.Retired))
                throw new PlanConflictException("Nur freigegebene Pläne können zurückgezogen werden.");
            if (plan.Status != PlanStatus.Draft)
            {
                ValidateJsonObject(plan.ExecutionPlanJson);
                if (plan.ReleasedAt is null) throw new PlanConflictException("Ein freigegebener Plan braucht einen Freigabezeitpunkt.");
            }
            plan.UpdatedAt = DatabaseClock.UtcNow();
        }

        var steps = ChangeTracker.Entries<TestStep>().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
        var existingStepIds = steps.Where(x => x.State != EntityState.Added).Select(x => x.Entity.Id).ToArray();
        var storedParentIds = await TestSteps.AsNoTracking().Where(x => existingStepIds.Contains(x.Id))
            .Select(x => x.TestPlanId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var planIds = steps.Select(x => x.Entity.TestPlanId).Concat(storedParentIds).Distinct().ToArray();
        foreach (var planId in planIds)
        {
            var stored = await TestPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, cancellationToken).ConfigureAwait(false);
            if (stored is null) continue;
            if (stored.Status != PlanStatus.Draft)
                throw new PlanConflictException("Schritte freigegebener oder zurückgezogener Pläne sind nicht änderbar.");

            // Updating the parent token makes a concurrent release conflict with a step edit.
            var parent = ChangeTracker.Entries<TestPlan>().SingleOrDefault(x => x.Entity.Id == planId)
                ?? Entry(stored);
            if (parent.State == EntityState.Detached) parent.State = EntityState.Unchanged;
            if (parent.State != EntityState.Deleted) parent.Entity.UpdatedAt = DatabaseClock.UtcNow();
        }
        foreach (var entry in steps.Where(x => x.State != EntityState.Deleted)) StepParameters.Validate(entry.Entity);
        foreach (var entry in ChangeTracker.Entries<TestSystemType>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Entity.Name);
        }
        foreach (var entry in ChangeTracker.Entries<SystemTypeDevice>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Entity.DeviceKey);
            // Throws for unknown models and invalid slot or channel options; the model name is stored as in the catalog.
            DeviceCatalog.ParseOptions(entry.Entity.Model, entry.Entity.OptionsJson);
            entry.Entity.Model = DeviceCatalog.Get(entry.Entity.Model).Name;
        }
        foreach (var entry in ChangeTracker.Entries<Station>().Where(x => x.State is EntityState.Added or EntityState.Modified))
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Entity.Name);
        foreach (var entry in ChangeTracker.Entries<StationDevice>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Entity.DeviceKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Entity.Driver);
            ValidateJsonObject(entry.Entity.ConnectionParametersJson);
        }
        foreach (var entry in ChangeTracker.Entries<RunStepResult>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            LimitChecker.Validate(entry.Entity.LimitLow, entry.Entity.LimitHigh);
            if (entry.Entity.Value.HasValue && !double.IsFinite(entry.Entity.Value.Value))
                throw new ArgumentException("Non-finite measurements must be stored as null with an error result.");
            if (entry.Entity.Value.HasValue && entry.Entity.TextValue is not null)
                throw new ArgumentException("A step result cannot contain both a numeric and text value.");
            if (entry.Entity.TextValue?.Length > 4000)
                throw new ArgumentOutOfRangeException(nameof(entry.Entity.TextValue), "Text step results are limited to 4,000 characters.");
        }
    }

    internal static void ValidateJsonObject(string? json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("A JSON object is required.");
    }
}

public sealed class SqliteTestpilotDbContext(DbContextOptions<SqliteTestpilotDbContext> options) : TestpilotDbContext(options);
public sealed class SqlServerTestpilotDbContext(DbContextOptions<SqlServerTestpilotDbContext> options) : TestpilotDbContext(options);
public sealed class PostgreSqlTestpilotDbContext(DbContextOptions<PostgreSqlTestpilotDbContext> options) : TestpilotDbContext(options);
