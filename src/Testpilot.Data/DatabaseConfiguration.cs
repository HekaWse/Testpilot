using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testpilot.Core;

namespace Testpilot.Data;

public static class DatabaseConfiguration
{
    public static IServiceCollection AddTestpilotData(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? throw new InvalidOperationException("Database:Provider is required.");
        var connection = configuration.GetConnectionString("Testpilot");
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        switch (provider)
        {
            case "Sqlite":
                services.AddDbContext<TestpilotDbContext, SqliteTestpilotDbContext>(options => options.UseSqlite(connection));
                break;
            case "SqlServer":
                services.AddDbContext<TestpilotDbContext, SqlServerTestpilotDbContext>(options => options.UseSqlServer(connection));
                break;
            case "PostgreSql":
                services.AddDbContext<TestpilotDbContext, PostgreSqlTestpilotDbContext>(options => options.UseNpgsql(connection));
                break;
            default:
                throw new InvalidOperationException($"Unsupported database provider '{provider}'. Use Sqlite, SqlServer or PostgreSql.");
        }
        services.AddScoped<TestPlanService>();
        services.AddScoped<ITestPlanLookup>(serviceProvider => serviceProvider.GetRequiredService<TestPlanService>());
        return services;
    }

    /// <summary>
    /// Creates new contexts for the configured provider, for applications that need one short-lived context per action
    /// (the Blazor editor). Keep the provider names in sync with <see cref="AddTestpilotData"/>.
    /// </summary>
    public static Func<TestpilotDbContext> CreateContextFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var provider = configuration["Database:Provider"] ?? throw new InvalidOperationException("Database:Provider is required.");
        var connection = configuration.GetConnectionString("Testpilot");
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        switch (provider)
        {
            case "Sqlite":
                var sqlite = new DbContextOptionsBuilder<SqliteTestpilotDbContext>()
                    .UseSqlite(connection).UseLoggerFactory(loggerFactory).Options;
                return () => new SqliteTestpilotDbContext(sqlite);
            case "SqlServer":
                var sqlServer = new DbContextOptionsBuilder<SqlServerTestpilotDbContext>()
                    .UseSqlServer(connection).UseLoggerFactory(loggerFactory).Options;
                return () => new SqlServerTestpilotDbContext(sqlServer);
            case "PostgreSql":
                var postgreSql = new DbContextOptionsBuilder<PostgreSqlTestpilotDbContext>()
                    .UseNpgsql(connection).UseLoggerFactory(loggerFactory).Options;
                return () => new PostgreSqlTestpilotDbContext(postgreSql);
            default:
                throw new InvalidOperationException($"Unsupported database provider '{provider}'. Use Sqlite, SqlServer or PostgreSql.");
        }
    }
}

// Each provider has its own migration history and model snapshot.
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteTestpilotDbContext>
{
    public SqliteTestpilotDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqliteTestpilotDbContext>()
        .UseSqlite(DesignTimeConnection.Get(args, "Data Source=testpilot.db")).Options);
}

public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerTestpilotDbContext>
{
    public SqlServerTestpilotDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqlServerTestpilotDbContext>()
        .UseSqlServer(DesignTimeConnection.Get(args, "Server=(localdb)\\MSSQLLocalDB;Database=Testpilot;Trusted_Connection=True")).Options);
}

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<PostgreSqlTestpilotDbContext>
{
    public PostgreSqlTestpilotDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PostgreSqlTestpilotDbContext>()
        .UseNpgsql(DesignTimeConnection.Get(args, "Host=localhost;Database=testpilot;Username=testpilot")).Options);
}

internal static class DesignTimeConnection
{
    internal static string Get(string[] args, string fallback)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--connection"))
            throw new ArgumentException("Use --connection <connection-string> after --.");
        var value = args.Length == 2 ? args[1] : Environment.GetEnvironmentVariable("ConnectionStrings__Testpilot") ?? fallback;
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
