using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Testpilot.Core;

namespace Testpilot.Data;

public sealed class TestPlanService(TestpilotDbContext database, ILogger<TestPlanService> logger) : ITestPlanLookup
{
    public async Task<TestPlan> GetReleasedAsync(string artNr, string fz, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artNr);
        ArgumentException.ThrowIfNullOrWhiteSpace(fz);
        return await database.TestPlans.AsNoTracking().Include(x => x.Steps.OrderBy(step => step.Order))
            .SingleOrDefaultAsync(x => x.ArtNr == artNr && x.Fz == fz && x.Status == PlanStatus.Released, cancellationToken)
            ?? throw new PlanNotFoundException($"No released plan exists for '{artNr}' / '{fz}'.");
    }

    /// <summary>Freezes a caller-supplied execution snapshot. The optimizer is introduced in phase 3.</summary>
    public async Task ReleaseAsync(Guid planId, string executionPlanJson, CancellationToken cancellationToken = default)
    {
        TestpilotDbContext.ValidateJsonObject(executionPlanJson);
        var plan = await FindAsync(planId, cancellationToken);
        if (plan.Status != PlanStatus.Draft)
            throw new PlanConflictException("Only draft plans can be released.");
        if (plan.Steps.Count == 0)
            throw new PlanConflictException("A plan must contain at least one step before release.");
        foreach (var step in plan.Steps) StepParameters.Validate(step);
        if (await database.TestPlans.AnyAsync(x => x.ArtNr == plan.ArtNr && x.Fz == plan.Fz && x.Status == PlanStatus.Released, cancellationToken))
            throw new PlanConflictException("Another version is already released. Retire it before releasing this version.");

        plan.ExecutionPlanJson = executionPlanJson;
        plan.ReleasedAt = DateTimeOffset.UtcNow;
        plan.Status = PlanStatus.Released;
        // The filtered unique index also protects concurrent releases from different processes.
        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Released plan {PlanId} version {Version}", plan.Id, plan.Version);
    }

    public async Task RetireAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var plan = await FindAsync(planId, cancellationToken);
        if (plan.Status != PlanStatus.Released)
            throw new PlanConflictException("Only released plans can be retired.");
        plan.Status = PlanStatus.Retired;
        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Retired plan {PlanId}", plan.Id);
    }

    public async Task<TestPlan> CreateNewVersionAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        var source = await database.TestPlans.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken)
            ?? throw new PlanNotFoundException($"Plan '{sourceId}' does not exist.");
        var highestVersion = await database.TestPlans.Where(x => x.ArtNr == source.ArtNr && x.Fz == source.Fz)
            .MaxAsync(x => x.Version, cancellationToken);
        var copy = new TestPlan
        {
            ArtNr = source.ArtNr,
            Fz = source.Fz,
            Version = checked(highestVersion + 1),
            Steps = source.Steps.OrderBy(x => x.Order).Select(step => new TestStep
            {
                Order = step.Order, Name = step.Name, StepType = step.StepType, DeviceKey = step.DeviceKey,
                ParametersJson = step.ParametersJson, LimitLow = step.LimitLow, LimitHigh = step.LimitHigh,
                Unit = step.Unit, BreakpointFlag = step.BreakpointFlag
            }).ToList()
        };
        database.TestPlans.Add(copy);
        await database.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created draft {PlanId} version {Version} from {SourceId}", copy.Id, copy.Version, source.Id);
        return copy;
    }

    private async Task<TestPlan> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await database.TestPlans.Include(x => x.Steps.OrderBy(step => step.Order)).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new PlanNotFoundException($"Plan '{id}' does not exist.");
}
