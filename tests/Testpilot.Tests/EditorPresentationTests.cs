using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using MudBlazor.Utilities;
using Testpilot.Editor.Components.Layout;
using Testpilot.Editor.Components.Pages;

namespace Testpilot.Tests;

public sealed class EditorPresentationTests
{
    [Theory]
    [InlineData(typeof(MainLayout))]
    [InlineData(typeof(Home))]
    public async Task NavigationAndOverviewFollowTypeStationPlanOrder(Type componentType)
    {
        var html = await RenderAsync(componentType);

        var types = html.IndexOf("href=\"/system-types\"", StringComparison.Ordinal);
        var stations = html.IndexOf("href=\"/stations\"", StringComparison.Ordinal);
        var plans = html.IndexOf("href=\"/plans\"", StringComparison.Ordinal);
        Assert.True(types >= 0 && types < stations && stations < plans,
            "Navigation must list test system types, then stations, then plans.");
    }

    [Fact]
    public async Task EditorUsesWhiteSurfacesAndAccessibleBlueText()
    {
        var html = await RenderAsync(typeof(MainLayout));
        var white = new MudColor("#FFFFFF");
        foreach (var name in new[] { "background", "surface", "appbar-background" })
        {
            var color = PaletteColor(html, name);
            Assert.Equal(white.R, color.R);
            Assert.Equal(white.G, color.G);
            Assert.Equal(white.B, color.B);
        }
        foreach (var name in new[] { "primary", "secondary", "text-primary", "text-secondary", "appbar-text", "info", "success", "warning", "error" })
        {
            var color = PaletteColor(html, name);
            var contrast = 1.05 / (Luminance(color) + 0.05);
            Assert.True(contrast >= 4.5, $"{name} must have at least 4.5:1 contrast on white; actual: {contrast:F2}:1.");
        }
        var primary = PaletteColor(html, "primary");
        Assert.True(primary.B > primary.R, "The primary accent should remain blue.");
    }

    private static MudColor PaletteColor(string html, string name)
    {
        var match = Regex.Match(html, $@"--mud-palette-{Regex.Escape(name)}:\s*(?<color>[^;]+);");
        Assert.True(match.Success, $"Palette color '{name}' was not rendered.");
        return new MudColor(match.Groups["color"].Value.Trim());
    }

    private static double Luminance(MudColor color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static async Task<string> RenderAsync(Type componentType)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddMudServices();
        collection.AddSingleton<NavigationManager, TestNavigationManager>();
        collection.AddSingleton<IJSRuntime, PrerenderJsRuntime>();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync(componentType);
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class PrerenderJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSDisconnectedException("No browser is connected during static rendering.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new JSDisconnectedException("No browser is connected during static rendering.");
    }
}
