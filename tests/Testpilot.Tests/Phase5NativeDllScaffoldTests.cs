using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Testpilot.Drivers;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Tests;

public sealed class Phase5NativeDllScaffoldTests
{
    private const string VerifiedTestProfile = """
        id: dll-scaffold-test
        model: Synthetic test module
        channelCount: 1
        channelNumbering: channel
        capabilities: [SwitchMultimeter]
        state:
          settings: [function, range]
          invalidatedByCommand: {}
          unknownCommandInvalidatesAll: true
        channels:
          - number: 1003
            name: Test channel
            type: Voltage
            supportedFunctions: [VOLT:DC]
            valueRanges: {}
        specialCases: []
        todos: []
        """;

    [Fact]
    public async Task HmpDllIsRegisteredButNeverLoadsTheConfiguredLibrary()
    {
        var registry = new DriverRegistry().AddHmpDll();
        var configuration = DriverConfiguration.Parse("hmp-dll-test", "HmpDll", JsonSerializer.Serialize(new
        {
            libraryPath = Path.Combine(Path.GetTempPath(), "missing-hmp.dll"),
            resource = "USB::SIMULATED",
            model = "HMP4040"
        }));
        var profile = registry.GetProfile(configuration);
        Assert.Contains(DeviceCapability.PowerSupply, profile.Capabilities);
        Assert.Equal(4, profile.ChannelCount);

        var host = new DriverHost(registry, NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<NativeDriverScaffoldException>(async () =>
            await host.AcquireAsync(configuration));
    }

    [Fact]
    public async Task Ag34980DllRequiresVerifiedProfileButNeverLoadsTheConfiguredLibrary()
    {
        var profilePath = WriteProfile(VerifiedTestProfile);
        try
        {
            var registry = new DriverRegistry().AddAg34980Dll();
            var configuration = DriverConfiguration.Parse("ag-dll-test", "Ag34980Dll", JsonSerializer.Serialize(new
            {
                libraryPath = Path.Combine(Path.GetTempPath(), "missing-ag34980.dll"),
                resource = "GPIB::SIMULATED",
                profilePath
            }));
            Assert.Equal("dll-scaffold-test", registry.GetProfile(configuration).Id);

            var host = new DriverHost(registry, NullLoggerFactory.Instance);
            await Assert.ThrowsAsync<NativeDriverScaffoldException>(async () =>
                await host.AcquireAsync(configuration));
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    [Fact]
    public void DllScaffoldOptionsAreStrictlyValidated()
    {
        var hmpRegistry = new DriverRegistry().AddHmpDll();
        Assert.Throws<JsonException>(() => hmpRegistry.GetProfile(
            DriverConfiguration.Parse("hmp-invalid", "HmpDll", "{\"libraryPath\":\"hmp.dll\"}")));
        Assert.Throws<JsonException>(() => hmpRegistry.GetProfile(
            DriverConfiguration.Parse("hmp-invalid", "HmpDll", "{\"libraryPath\":\"hmp.dll\",\"resource\":\"USB::1\",\"model\":\"HMP9999\"}")));
        Assert.Throws<JsonException>(() => hmpRegistry.GetProfile(
            DriverConfiguration.Parse("hmp-invalid", "HmpDll", "{\"libraryPath\":\"hmp.dll\",\"resource\":\"USB::1\",\"model\":\"HMP4040\",\"channel\":1}")));

        var agRegistry = new DriverRegistry().AddAg34980Dll();
        Assert.Throws<JsonException>(() => agRegistry.GetProfile(
            DriverConfiguration.Parse("ag-invalid", "Ag34980Dll", "{\"libraryPath\":\"ag.dll\",\"resource\":\"GPIB::1\",\"profilePath\":\"p.yaml\",\"extra\":true}")));
    }

    private static string WriteProfile(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"testpilot-dll-profile-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
