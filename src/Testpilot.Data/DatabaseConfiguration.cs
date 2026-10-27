using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
