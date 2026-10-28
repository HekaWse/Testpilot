namespace Testpilot.Core;

/// <summary>
/// Current UTC time truncated to whole microseconds. PostgreSQL stores microseconds only; without truncation
/// a reloaded timestamp differs from the tracked value, which breaks concurrency tokens and immutability checks.
/// </summary>
public static class DatabaseClock
{
    public static DateTimeOffset UtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return now.AddTicks(-(now.Ticks % 10));
    }
}
