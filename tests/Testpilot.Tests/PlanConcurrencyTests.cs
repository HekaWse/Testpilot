using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Core;
using Testpilot.Data;

namespace Testpilot.Tests;

public sealed class PlanConcurrencyTests
{
    private const string Snapshot = "{\"init\":[],\"steps\":[],\"cleanup\":[]}";

    [Fact]
    public async Task StaleDraftCannotEditStepsAfterAnotherContextReleasedIt()
    {
        await using var db = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        db.Context.Add(plan);
        await db.Context.SaveChangesAsync();
        await using var other = db.NewContext();
        var service = new TestPlanService(other, NullLogger<TestPlanService>.Instance);
        await service.ReleaseAsync(plan.Id, Snapshot);
        plan.Steps[0].LimitLow = 0;
        await Assert.ThrowsAsync<PlanConflictException>(() => db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task StepEditPreventsReleaseFromStalePlanSnapshot()
    {
        await using var db = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        db.Context.Add(plan);
        await db.Context.SaveChangesAsync();
        await using var editor = db.NewContext();
        var edited = await editor.TestPlans.Include(x => x.Steps).SingleAsync();
        edited.Steps[0].LimitLow = 3.1;
        await editor.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.Plans.ReleaseAsync(plan.Id, Snapshot));
        await using var verify = db.NewContext();
        Assert.Equal(PlanStatus.Draft, (await verify.TestPlans.SingleAsync()).Status);
        Assert.Equal(3.1, (await verify.TestSteps.SingleAsync()).LimitLow);
    }

    [Fact]
    public async Task DetachedStepCannotBeMovedFromReleasedPlanToDraft()
    {
        await using var db = await TestDatabase.CreateAsync();
        var released = TestDatabase.Draft();
        var draft = TestDatabase.Draft(2);
        db.Context.AddRange(released, draft);
        await db.Context.SaveChangesAsync();
        await db.Plans.ReleaseAsync(released.Id, Snapshot);
        var step = released.Steps[0];
        db.Context.ChangeTracker.Clear();
        step.TestPlanId = draft.Id;
        step.Order = 1;
        db.Context.Update(step);
        await Assert.ThrowsAsync<PlanConflictException>(() => db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task SynchronousSaveAlsoGuardsRetiredContent()
    {
        await using var db = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        db.Context.Add(plan);
        db.Context.SaveChanges();
        await db.Plans.ReleaseAsync(plan.Id, Snapshot);
        await db.Plans.RetireAsync(plan.Id);
        plan.Steps[0].Name = "Changed";
        Assert.Throws<PlanConflictException>(() => db.Context.SaveChanges());
    }

    [Fact]
    public async Task NewStepsCanBeAddedToExistingDraftNavigation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        db.Context.Add(plan);
        await db.Context.SaveChangesAsync();
        plan.Steps.Add(new TestStep { Order = 1, Name = "Wait", StepType = StepType.Wait,
            ParametersJson = StepParameters.Serialize(new WaitParameters(1)) });
        await db.Context.SaveChangesAsync();
        Assert.Equal(2, await db.Context.TestSteps.CountAsync());
    }
}
