using Microsoft.Extensions.Configuration;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;
using Testpilot.Editor.Services;
using Testpilot.Sequencer;

namespace Testpilot.Tests;

public sealed class DevicesFileTests
{
    private const string UserExample = """
        [
          {
            "Alias": "ag34980a",
            "Resource": "TCPIP0::192.168.178.100::inst0::INSTR"
          },
          {
            "Alias": "PSU1",
            "Resource": "TCPIP0::192.168.178.101::5025::SOCKET"
          },
          {
            "Alias": "PSU2",
            "Resource": "TCPIP0::192.168.178.102::5025::SOCKET"
          }
        ]
        """;

    [Fact]
    public void ExampleFileIsReadWithCaseInsensitiveAliases()
    {
        var addresses = DeviceAddressFile.Parse(UserExample);

        Assert.Equal(3, addresses.Count);
        Assert.Equal("TCPIP0::192.168.178.100::inst0::INSTR", addresses["Ag34980A"]);
        Assert.Equal("TCPIP0::192.168.178.102::5025::SOCKET", addresses["psu2"]);
    }

    [Theory]
    [InlineData("{}", "muss eine Liste")]
    [InlineData("[{\"Alias\": \"PSU1\"}]", "Eintrag 1 braucht")]
    [InlineData("[{\"Alias\": \"PSU1\", \"Resource\": \"A\"}, {\"Alias\": \"psu1\", \"Resource\": \"B\"}]", "Alias 'psu1' ist mehrfach")]
    [InlineData("[{\"Alias\": ", "kein gültiges JSON")]
    public void InvalidFileIsRejectedWithGermanMessage(string json, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() => DeviceAddressFile.Parse(json));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyReplacesOnlyTheVisaResourceOfListedDevices()
    {
        var addresses = DeviceAddressFile.Parse(UserExample);

        var replaced = DeviceAddressFile.Apply(addresses, "Ag34980A", "Ag34980Scpi", """{"resource":"GPIB0::9::INSTR","slots":{"1":"34921A"}}""");
        Assert.Equal("TCPIP0::192.168.178.100::inst0::INSTR", ConnectionAddress.GetResource(replaced));
        Assert.Contains("34921A", replaced, StringComparison.Ordinal);
        Assert.Equal("{\"resource\":\"X\"}", DeviceAddressFile.Apply(addresses, "Hmp2030", "HmpScpi", "{\"resource\":\"X\"}"));
        Assert.Equal("{}", DeviceAddressFile.Apply(addresses, "PSU1", "SimulatedPowerSupply", "{}"));
        var exception = Assert.Throws<InvalidOperationException>(
            () => DeviceAddressFile.Apply(addresses, "PSU1", "SensopartTcp", "{\"host\":\"192.0.2.10\"}"));
        Assert.Contains("keine VISA-Adresse", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunPreparationAppliesAddressesAndReportsUnknownAliases()
    {
        var station = new Station { Name = "PS04-1" };
        station.Devices.Add(new StationDevice { DeviceKey = "Ag34980A", Driver = "Ag34980Scpi", ConnectionParametersJson = """{"resource":"GPIB0::9::INSTR"}""" });
        station.Devices.Add(new StationDevice { DeviceKey = "Hmp4040", Driver = "HmpScpi", ConnectionParametersJson = """{"resource":"GPIB0::5::INSTR"}""" });

        var unknown = RunPreparation.ApplyDeviceAddresses(station, DeviceAddressFile.Parse(UserExample));

        Assert.Equal(["PSU1", "PSU2"], unknown);
        Assert.Equal("TCPIP0::192.168.178.100::inst0::INSTR", ConnectionAddress.GetResource(station.Devices[0].ConnectionParametersJson));
        Assert.Equal("GPIB0::5::INSTR", ConnectionAddress.GetResource(station.Devices[1].ConnectionParametersJson));
        Assert.Empty(RunPreparation.ApplyDeviceAddresses(station, null));
    }

    [Fact]
    public void ConfigurationUsesTheFileNextToTheExecutableOrTheConfiguredFile()
    {
        var empty = new ConfigurationBuilder().Build();
        var (defaultPath, defaultAddresses) = DeviceAddressConfiguration.Load(empty);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "devices.json"), defaultPath);
        Assert.Null(defaultAddresses);

        var missing = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DevicesFile"] = "fehlt.json" }).Build();
        Assert.Contains("fehlt.json", Assert.Throws<InvalidOperationException>(() => DeviceAddressConfiguration.Load(missing)).Message, StringComparison.Ordinal);

        var file = Path.Combine(Path.GetTempPath(), $"devices-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, UserExample);
        try
        {
            var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DevicesFile"] = file }).Build();
            var (path, addresses) = DeviceAddressConfiguration.Load(configured);
            Assert.Equal(file, path);
            Assert.Equal(3, addresses!.Count);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void EditorExportsAndTakesOverStationAddresses()
    {
        var station = new Station { Name = "PS04-1" };
        station.Devices.Add(new StationDevice { DeviceKey = "Hmp4040", Driver = "HmpScpi", ConnectionParametersJson = """{"resource":"GPIB0::5::INSTR"}""" });
        station.Devices.Add(new StationDevice { DeviceKey = "Ag34980A", Driver = "Ag34980Scpi", ConnectionParametersJson = """{"resource":"GPIB0::9::INSTR"}""" });
        station.Devices.Add(new StationDevice { DeviceKey = "Reader", Driver = "SensopartTcp", ConnectionParametersJson = """{"host":"192.0.2.10"}""" });

        var exported = StationDevicesFile.Export(station);
        var roundTrip = DeviceAddressFile.Parse(exported);
        Assert.Equal(["Ag34980A", "Hmp4040"], roundTrip.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("\"Alias\": \"Ag34980A\"", exported, StringComparison.Ordinal);

        var (updated, unknown) = StationDevicesFile.Apply(station, UserExample);
        Assert.Equal(["Ag34980A"], updated);
        Assert.Equal(["PSU1", "PSU2"], unknown);
        Assert.Equal("TCPIP0::192.168.178.100::inst0::INSTR", ConnectionAddress.GetResource(station.Devices[1].ConnectionParametersJson));
        Assert.Equal("GPIB0::5::INSTR", ConnectionAddress.GetResource(station.Devices[0].ConnectionParametersJson));
    }
}
