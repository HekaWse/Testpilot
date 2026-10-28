using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using Testpilot.Data;

namespace Testpilot.Tests;

public sealed class PlanServiceTests
{
    [Fact]
    public async Task LookupSelectsOnlyReleasedExactArticleAndFz()
    {
        await using var db = await TestDatabase.CreateAsync();
        var released = TestDatabase.Draft();
        db.Context.AddRange(released, TestDatabase.Draft(2), TestDatabase.Draft(1, "4711", "B"), TestDatabase.Draft(1, "9999"));
        await db.Context.SaveChangesAsync();
        await db.ReleaseAsync(released);
        var found = await db.Plans.GetReleasedAsync("4711", "A");
        Assert.Equal(released.Id, found.Id);
        Assert.Equal(Testpilot.Planning.ExecutionPlanSerializer.Serialize(TestDatabase.Optimize(released)), found.ExecutionPlanJson);
        Assert.NotNull(found.ReleasedAt);
        Assert.Single(found.Steps);
        Assert.True(found.Steps[0].BreakpointFlag);
        await Assert.ThrowsAsync<PlanNotFoundException>(() => db.Plans.GetReleasedAsync("4711", "B"));
        await Assert.ThrowsAsync<PlanNotFoundException>(() => db.Plans.GetReleasedAsync("4711", "missing"));
    }

    [Fact]
    public async Task ServicePreventsSecondReleasedVersion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var first = TestDatabase.Draft();
        var second = TestDatabase.Draft(2);
        db.Context.AddRange(first, second);
        await db.Context.SaveChangesAsync();
        await db.ReleaseAsync(first);
        await Assert.ThrowsAsync<PlanConflictException>(() => db.ReleaseAsync(second));
        Assert.Equal(PlanStatus.Draft, second.Status);
    }

    [Fact]
    public async Task FilteredUniqueIndexProtectsEvenWithoutServiceCheck()
    {
        await using var db = await TestDatabase.CreateAsync();
        var first = TestDatabase.Draft();
        var second = TestDatabase.Draft(2);
        db.Context.AddRange(first, second);
        await db.Context.SaveChangesAsync();
        foreach (var plan in new[] { first, second })
        {
            plan.Status = PlanStatus.Released;
            plan.ReleasedAt = DateTimeOffset.UtcNow;
            plan.ExecutionPlanJson = "{}";
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        await using var verify = db.NewContext();
        Assert.Equal(0, await verify.TestPlans.CountAsync(x => x.Status == PlanStatus.Released));
    }

    [Fact]
    public async Task RetirementAllowsNewReleaseAndCopiesIndependentDraft()
    {
        await using var db = await TestDatabase.CreateAsync();
        var source = TestDatabase.Draft();
        db.Context.Add(source);
        await db.Context.SaveChangesAsync();
        await db.ReleaseAsync(source);
        var copy = await db.Plans.CreateNewVersionAsync(source.Id);
        Assert.Equal(2, copy.Version);
        Assert.Equal(PlanStatus.Draft, copy.Status);
        Assert.Null(copy.ExecutionPlanJson);
        Assert.Null(copy.ReleasedAt);
        Assert.NotEqual(source.Steps[0].Id, copy.Steps[0].Id);
        Assert.Equal(source.Steps[0].ParametersJson, copy.Steps[0].ParametersJson);
        Assert.True(copy.Steps[0].BreakpointFlag);
        copy.Steps[0].LimitLow = 3.1;
        await db.Context.SaveChangesAsync();
        Assert.Equal(3.2, source.Steps[0].LimitLow);
        await db.Plans.RetireAsync(source.Id);
        await db.ReleaseAsync(copy);
        Assert.Equal(copy.Id, (await db.Plans.GetReleasedAsync("4711", "A")).Id);
        await Assert.ThrowsAsync<PlanConflictException>(() => db.ReleaseAsync(source));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("snapshot")]
    [InlineData("step")]
    [InlineData("add-step")]
    [InlineData("delete-step")]
    [InlineData("delete-plan")]
    [InlineData("draft")]
    public async Task ReleasedContentIsImmutable(string change)
    {
        await using var db = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        db.Context.Add(plan);
        await db.Context.SaveChangesAsync();
        await db.ReleaseAsync(plan);
        switch (change)
        {
            case "name": plan.ArtNr = "changed"; break;
            case "snapshot": plan.ExecutionPlanJson = "{}"; break;
            case "step": plan.Steps[0].LimitHigh = 100; break;
            case "add-step": plan.Steps.Add(new TestStep { Order = 1, Name = "Extra", StepType = StepType.Wait,
                ParametersJson = StepParameters.Serialize(new WaitParameters(1)) }); break;
            case "delete-step": db.Context.Remove(plan.Steps[0]); break;
            case "delete-plan": db.Context.Remove(plan); break;
            case "draft": plan.Status = PlanStatus.Draft; break;
        }
        await Assert.ThrowsAsync<PlanConflictException>(() => db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task DetachedPlanCannotBypassImmutability()
    {
        await using var db = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        db.Context.Add(plan);
        await db.Context.SaveChangesAsync();
        await db.ReleaseAsync(plan);
        db.Context.ChangeTracker.Clear();
        plan.ArtNr = "changed";
        db.Context.Update(plan);
        await Assert.ThrowsAsync<PlanConflictException>(() => db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task ReleaseRejectsEmptyAndUnknownPlans()
    {
        await using var db = await TestDatabase.CreateAsync();
        var empty = new TestPlan { ArtNr = "empty", Fz = "A" };
        db.Context.Add(empty);
        await db.Context.SaveChangesAsync();
        var emptyExecution = new Testpilot.Planning.ExecutionPlan(empty.Id, empty.Version, empty.ArtNr, empty.Fz, [],
            new([]), [], new([], []));
        await Assert.ThrowsAsync<PlanConflictException>(() => db.Plans.ReleaseAsync(empty.Id, emptyExecution));
        await Assert.ThrowsAsync<PlanNotFoundException>(() => db.Plans.ReleaseAsync(Guid.NewGuid(), emptyExecution));
    }

    [Fact]
    public async Task StepParameterValidationIsAppliedOnPersistence()
    {
        await using var db = await TestDatabase.CreateAsync();
        var plan = TestDatabase.Draft();
        plan.Steps[0].ParametersJson = "{}";
        db.Context.Add(plan);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => db.Context.SaveChangesAsync());
    }
}
