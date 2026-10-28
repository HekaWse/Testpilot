using Microsoft.EntityFrameworkCore;
using Testpilot.Data;

namespace Testpilot.Editor.Services;

/// <summary>
/// Creates a short-lived database context per editor action. Blazor Server keeps one DI scope per browser
/// connection, so a scoped context would be shared by all clicks of a user, fail on overlapping clicks and
/// show stale data that other users have changed in the meantime.
/// </summary>
public sealed class EditorDbContextFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
    : IDbContextFactory<TestpilotDbContext>
{
    private readonly Func<TestpilotDbContext> create = DatabaseConfiguration.CreateContextFactory(configuration, loggerFactory);

    public TestpilotDbContext CreateDbContext() => create();
}
