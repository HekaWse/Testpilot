using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Testpilot.Core;
using Testpilot.Planning;

namespace Testpilot.Data;

public sealed class TestPlanService(TestpilotDbContext database, ILogger<TestPlanService> logger) : ITestPlanLookup
{
    public async Task<TestPlan> GetReleasedAsync(string artNr, string fz, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artNr);
        ArgumentException.ThrowIfNullOrWhiteSpace(fz);
        return await database.TestPlans.AsNoTracking().Include(x => x.Steps.OrderBy(step => step.Order))
            .SingleOrDefaultAsync(x => x.ArtNr == artNr && x.Fz == fz && x.Status == PlanStatus.Released, cancellationToken)
            ?? throw new PlanNotFoundException($"Für '{artNr}' / '{fz}' ist kein Prüfplan freigegeben.");
    }

    /// <summary>Releases a draft with the execution plan the optimizer produced for exactly this draft.</summary>
    public async Task ReleaseAsync(Guid planId, ExecutionPlan executionPlan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        var plan = await FindAsync(planId, cancellationToken);
        ThrowIfNotReleasable(plan);
        if (executionPlan.PlanId != plan.Id || executionPlan.PlanVersion != plan.Version
            || !StringComparer.Ordinal.Equals(executionPlan.ArtNr, plan.ArtNr)
            || !StringComparer.Ordinal.Equals(executionPlan.Fz, plan.Fz))
            throw new PlanConflictException("Der Ausführungsplan gehört nicht zu diesem Entwurf. Bitte neu laden.");
        var steps = plan.Steps.OrderBy(step => step.Order).ToArray();
        if (steps.Length != executionPlan.Steps.Count || steps.Where((step, index) => !Matches(step, executionPlan.Steps[index])).Any())
            throw new PlanConflictException("Der Ausführungsplan passt nicht mehr zu den Schritten des Entwurfs. Bitte neu laden.");
        if (plan.TestSystemTypeId is { } systemId)
        {
            var systemDevices = await database.SystemTypeDevices.AsNoTracking().Where(device => device.TestSystemTypeId == systemId)
                .ToDictionaryAsync(device => device.DeviceKey, device => device.Model, StringComparer.Ordinal, cancellationToken);
            foreach (var device in executionPlan.Devices)
                if (!systemDevices.TryGetValue(device.DeviceKey, out var model) || !StringComparer.Ordinal.Equals(model, device.Model))
                    throw new PlanConflictException(
                        $"Gerät '{device.DeviceKey}' des Ausführungsplans passt nicht zum Prüfsystemtyp des Plans.");
        }
        await FreezeAsync(plan, ExecutionPlanSerializer.Serialize(executionPlan), cancellationToken);
    }

    private async Task FreezeAsync(TestPlan plan, string executionPlanJson, CancellationToken cancellationToken)
    {
        TestpilotDbContext.ValidateJsonObject(executionPlanJson);
        ThrowIfNotReleasable(plan);
        foreach (var step in plan.Steps) StepParameters.Validate(step);
        if (await database.TestPlans.AnyAsync(x => x.ArtNr == plan.ArtNr && x.Fz == plan.Fz && x.Status == PlanStatus.Released, cancellationToken))
            throw new PlanConflictException("Es ist bereits eine andere Version freigegeben. Bitte diese zuerst zurückziehen.");

        plan.ExecutionPlanJson = executionPlanJson;
        plan.ReleasedAt = DatabaseClock.UtcNow();
        plan.Status = PlanStatus.Released;
        try
        {
            // The filtered unique index also protects concurrent releases from different processes.
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new PlanConflictException(
                "Der Plan wurde gleichzeitig geändert oder eine andere Version wurde freigegeben. Bitte neu laden.", exception);
        }
        logger.LogInformation("Released plan {PlanId} version {Version}", plan.Id, plan.Version);
    }

    private static void ThrowIfNotReleasable(TestPlan plan)
    {
        if (plan.Status != PlanStatus.Draft)
            throw new PlanConflictException("Nur Entwürfe können freigegeben werden.");
        if (plan.Steps.Count == 0)
            throw new PlanConflictException("Ein Plan braucht mindestens einen Schritt, bevor er freigegeben werden kann.");
    }

    public async Task RetireAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var plan = await FindAsync(planId, cancellationToken);
        if (plan.Status != PlanStatus.Released)
            throw new PlanConflictException("Nur freigegebene Pläne können zurückgezogen werden.");
        plan.Status = PlanStatus.Retired;
        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Retired plan {PlanId}", plan.Id);
    }

    public async Task<TestPlan> CreateNewVersionAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        var source = await database.TestPlans.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken)
            ?? throw new PlanNotFoundException("Der Prüfplan existiert nicht mehr.");
        var highestVersion = await database.TestPlans.Where(x => x.ArtNr == source.ArtNr && x.Fz == source.Fz)
            .MaxAsync(x => x.Version, cancellationToken);
        var copy = new TestPlan
        {
            ArtNr = source.ArtNr,
            Fz = source.Fz,
            Version = checked(highestVersion + 1),
            TestSystemTypeId = source.TestSystemTypeId,
            Steps = source.Steps.OrderBy(x => x.Order).Select(step => new TestStep
            {
                Order = step.Order, Name = step.Name, StepType = step.StepType, DeviceKey = step.DeviceKey,
                ParametersJson = step.ParametersJson, LimitLow = step.LimitLow, LimitHigh = step.LimitHigh,
                Unit = step.Unit, BreakpointFlag = step.BreakpointFlag
            }).ToList()
        };
        database.TestPlans.Add(copy);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            database.Entry(copy).State = EntityState.Detached;
            throw new PlanConflictException("Gleichzeitig wurde bereits eine neue Version angelegt. Bitte neu laden.", exception);
        }
        logger.LogInformation("Created draft {PlanId} version {Version} from {SourceId}", copy.Id, copy.Version, source.Id);
        return copy;
    }

    private static bool Matches(TestStep source, ExecutionStep snapshot) =>
        source.Order == snapshot.Order && source.Name == snapshot.Name && source.StepType == snapshot.StepType
        && source.DeviceKey == snapshot.DeviceKey && source.ParametersJson == snapshot.ParametersJson
        && source.LimitLow == snapshot.LimitLow && source.LimitHigh == snapshot.LimitHigh
        && source.Unit == snapshot.Unit && source.BreakpointFlag == snapshot.BreakpointFlag;

    private async Task<TestPlan> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await database.TestPlans.Include(x => x.Steps.OrderBy(step => step.Order)).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new PlanNotFoundException("Der Prüfplan existiert nicht mehr.");
}
