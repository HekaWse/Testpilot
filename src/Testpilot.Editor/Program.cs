using Microsoft.EntityFrameworkCore;
using Serilog;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Editor.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSerilog(configuration => configuration.ReadFrom.Configuration(builder.Configuration));
builder.Services.AddTestpilotData(builder.Configuration);
builder.Services.AddSingleton(services => new RunJsonExporter(
    builder.Configuration["Export:Directory"] ?? throw new InvalidOperationException("Export:Directory is required."),
    services.GetRequiredService<ILogger<RunJsonExporter>>()));
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

await app.RunAsync();
