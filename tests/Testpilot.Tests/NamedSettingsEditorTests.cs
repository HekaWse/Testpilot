using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Testpilot.Core;
using Testpilot.Editor.Components;

namespace Testpilot.Tests;

public sealed class NamedSettingsEditorTests
{
    [Theory]
    [InlineData("selectedStation.SettingsJson")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"alias\":\"A\"}]")]
    [InlineData("[{\"directory\":\"\",\"name\":\"Port\",\"alias\":\"bad-alias\",\"dataType\":\"string\",\"value\":\"COM9\"}]")]
    [InlineData("[{\"directory\":\"\",\"name\":\"Enabled\",\"alias\":\"Enabled\",\"dataType\":\"boolean\",\"value\":\"No\"}]")]
    public async Task InvalidStoredSettingsShowErrorWithoutOfferingToOverwriteData(string json)
    {
        await using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var saves = 0;
        foreach (var scope in new[] { "station", "plan" })
        foreach (var readOnly in new[] { false, true })
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(NamedSettingsEditor.SettingsJson)] = json,
                [nameof(NamedSettingsEditor.Scope)] = scope,
                [nameof(NamedSettingsEditor.ReadOnly)] = readOnly,
                [nameof(NamedSettingsEditor.OnSave)] = EventCallback.Factory.Create<string>(this, _ => saves++)
            });

            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<NamedSettingsEditor>(parameters);
                return WebUtility.HtmlDecode(root.ToHtmlString());
            });

            Assert.Contains("Die benannten Einstellungen konnten nicht geladen werden", html, StringComparison.Ordinal);
            Assert.Contains("Die gespeicherten Daten bleiben unverändert", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Einstellung hinzufügen", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Eingabe leeren", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Einstellung bearbeiten", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Einstellung löschen", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Keine Einstellungen angelegt", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
        }
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task LoadFailureSurvivesRerenderAndRecoversWhenCorrectedSettingsArrive()
    {
        await using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        SettingsHost host = null!;
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(SettingsHost.Capture)] = (Action<SettingsHost>)(component => host = component)
        });
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SettingsHost>(parameters));
        var json = NamedSettings.Serialize([new NamedSetting("", "Debug port", "Port", SettingDataType.String, "COM9")]);
        await host.UpdateAsync(json);
        var html = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.Contains("COM9", html, StringComparison.Ordinal);
        Assert.Contains("Einstellung hinzufügen", html, StringComparison.Ordinal);

        await host.UpdateAsync("invalid json");
        html = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.Contains("Das gespeicherte JSON ist ungültig", html, StringComparison.Ordinal);
        Assert.DoesNotContain("COM9", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);

        await host.UpdateAsync("invalid json", busy: true);
        html = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.Contains("Das gespeicherte JSON ist ungültig", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Einstellung hinzufügen", html, StringComparison.Ordinal);

        await host.UpdateAsync(json);
        html = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.DoesNotContain("konnten nicht geladen werden", html, StringComparison.Ordinal);
        Assert.Contains("COM9", html, StringComparison.Ordinal);
        Assert.Contains("Einstellung hinzufügen", html, StringComparison.Ordinal);

        await host.UpdateAsync("[]");
        html = await renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));
        Assert.DoesNotContain("konnten nicht geladen werden", html, StringComparison.Ordinal);
        Assert.DoesNotContain("COM9", html, StringComparison.Ordinal);
        Assert.Contains("Keine Einstellungen angelegt", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("station", false)]
    [InlineData("station", true)]
    [InlineData("plan", false)]
    [InlineData("plan", true)]
    public async Task ExistingSettingsAndNewEntryFormAreClearlySeparated(string scope, bool readOnly)
    {
        await using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(NamedSettingsEditor.SettingsJson)] = NamedSettings.Serialize(
                [new NamedSetting("", "Debug port", "Port", SettingDataType.String, "COM9")]),
            [nameof(NamedSettingsEditor.Scope)] = scope,
            [nameof(NamedSettingsEditor.ReadOnly)] = readOnly
        });
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<NamedSettingsEditor>(parameters);
            return WebUtility.HtmlDecode(root.ToHtmlString());
        });

        Assert.Contains("aria-label=\"Benannte Einstellungen\"", html, StringComparison.Ordinal);
        Assert.Contains("Vorhandene Einstellungen", html, StringComparison.Ordinal);
        Assert.Contains("COM9", html, StringComparison.Ordinal);
        const string form = "<section class=\"editor-form\"";
        if (readOnly)
        {
            Assert.DoesNotContain(form, html, StringComparison.Ordinal);
            Assert.DoesNotContain("Neue Einstellung hinzufügen", html, StringComparison.Ordinal);
        }
        else
        {
            var formStart = html.IndexOf(form, StringComparison.Ordinal);
            Assert.True(formStart > html.IndexOf("</table>", StringComparison.Ordinal));
            Assert.Contains("Neue Einstellung hinzufügen", html[formStart..], StringComparison.Ordinal);
            Assert.Contains("Eingabe leeren", html[formStart..], StringComparison.Ordinal);
        }
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, PrerenderJsRuntime>();
        return services.BuildServiceProvider();
    }

    public sealed class SettingsHost : ComponentBase
    {
        [Parameter] public Action<SettingsHost> Capture { get; set; } = null!;
        private string json = "[]";
        private bool busy;

        protected override void OnInitialized() => Capture(this);

        public Task UpdateAsync(string settingsJson, bool busy = false) => InvokeAsync(() =>
        {
            json = settingsJson;
            this.busy = busy;
            StateHasChanged();
        });

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<NamedSettingsEditor>(0);
            builder.AddAttribute(1, nameof(NamedSettingsEditor.SettingsJson), json);
            builder.AddAttribute(2, nameof(NamedSettingsEditor.Scope), "station");
            builder.AddAttribute(3, nameof(NamedSettingsEditor.Busy), busy);
            builder.CloseComponent();
        }
    }

    private sealed class PrerenderJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("JavaScript is not available during static rendering.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("JavaScript is not available during static rendering.");
    }
}
