namespace Testpilot.Core;

public sealed class PlanNotFoundException(string message) : InvalidOperationException(message);
public sealed class PlanConflictException : InvalidOperationException
{
    public PlanConflictException(string message) : base(message) { }
    public PlanConflictException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Finds the single released plan for an article and manufacturing state without a draft fallback.</summary>
public interface ITestPlanLookup
{
    Task<TestPlan> GetReleasedAsync(string artNr, string fz, CancellationToken cancellationToken = default);
}
