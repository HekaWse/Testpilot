namespace Testpilot.Editor.Services;

public static class PlanReleasePolicy
{
    public static bool IsAllowed(IReadOnlyCollection<string> unresolvedProfileWarnings,
        IReadOnlyCollection<ChannelAllocationAnalyzer.ChannelAllocation> allocations)
    {
        ArgumentNullException.ThrowIfNull(unresolvedProfileWarnings);
        ArgumentNullException.ThrowIfNull(allocations);
        return unresolvedProfileWarnings.Count == 0
            && allocations.All(allocation => allocation.ConflictingChannels.Count == 0);
    }
}
