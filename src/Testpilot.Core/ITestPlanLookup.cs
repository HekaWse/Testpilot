namespace Testpilot.Core;

public sealed class PlanNotFoundException(string message) : InvalidOperationException(message);
public sealed class PlanConflictException(string message) : InvalidOperationException(message);

/// <summary>Finds the single released plan for an article and manufacturing state without a draft fallback.</summary>
public interface ITestPlanLookup
{
    Task<TestPlan> GetReleasedAsync(string artNr, string fz, CancellationToken cancellationToken = default);
}
