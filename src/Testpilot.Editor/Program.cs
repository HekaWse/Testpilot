using Microsoft.EntityFrameworkCore;
using Serilog;
using Testpilot.Data;
using Testpilot.Editor.Components;
using Testpilot.Editor.Services;
using Testpilot.Planning;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSerilog(configuration => configuration.ReadFrom.Configuration(builder.Configuration));
builder.Services.AddMudServices();
builder.Services.AddTestpilotData(builder.Configuration);
// The pages create one short-lived context per user action instead of sharing the scoped context of the circuit.
builder.Services.AddSingleton<IDbContextFactory<TestpilotDbContext>, EditorDbContextFactory>();
builder.Services.AddSingleton<ExecutionPlanOptimizer>();
builder.Services.AddScoped<StationYamlService>();
builder.Services.AddScoped<TestSystemTypeYamlService>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseSerilogRequestLogging();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<TestpilotDbContext>();
    await database.Database.MigrateAsync();
    app.Logger.LogInformation("Applied database migrations for {Provider}", database.Database.ProviderName);
    return;
}

if (args.Contains("--seed-system-types", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<TestpilotDbContext>();
    var added = await TestSystemTypeSeed.SeedAsync(database);
    app.Logger.LogInformation("Added {Count} test system types: {Names}", added.Count, string.Join(", ", added));
    var equipment = await StationSeed.SeedAsync(database);
    app.Logger.LogInformation("Added known devices and test systems: {Items}", equipment.Count == 0 ? "none" : string.Join(", ", equipment));
    return;
}

await app.RunAsync();
