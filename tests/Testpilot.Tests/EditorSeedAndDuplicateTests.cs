using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using Testpilot.Data;
using Testpilot.Editor.Services;

namespace Testpilot.Tests;

public sealed class EditorSeedAndDuplicateTests
{
    [Fact]
    public async Task SystemTypeSeedAddsOnlyMissingTypesAndKeepsExistingOnes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ownPs04 = new TestSystemType { Name = "ps04", Description = "Eigene Beschreibung" };
        await using (var db = database.NewContext())
        {
            db.TestSystemTypes.Add(ownPs04);
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
        {
            var added = await TestSystemTypeSeed.SeedAsync(db);
            Assert.Equal(23, added.Count);
            Assert.DoesNotContain("PS04", added);
        }
        await using (var db = database.NewContext())
            Assert.Empty(await TestSystemTypeSeed.SeedAsync(db));

        await using var verify = database.NewContext();
        var types = await verify.TestSystemTypes.AsNoTracking().ToListAsync();
        Assert.Equal(24, types.Count);
        Assert.Equal("Eigene Beschreibung", types.Single(type => type.Id == ownPs04.Id).Description);
        Assert.Equal("Funktionstester für Trumpf Baugruppen, Testbox von Fa. Trumpf -> Kunde ist Eigentümer!",
            types.Single(type => type.Name == "PS10").Description);
        Assert.Contains(types, type => type.Name == "PX30");
    }

    [Theory]
    [InlineData("PS04-1", new[] { "PS04-1" }, "PS04-2")]
    [InlineData("PS04-1", new[] { "PS04-1", "ps04-2", "PS04-3" }, "PS04-4")]
    [InlineData("PS04-9", new[] { "PS04-9" }, "PS04-10")]
    [InlineData("PS05", new[] { "PS05" }, "PS05-2")]
    [InlineData("Labor", new[] { "Labor", "Labor-2" }, "Labor-3")]
    public void DuplicateNameCountsUpTheTrailingNumber(string source, string[] existing, string expected) =>
        Assert.Equal(expected, StationCopy.NextFreeName(source, existing));

    [Fact]
    public async Task DuplicatedStationGetsNewIdsAndTheSameDevicesAndAddresses()
    {
        await using var database = await TestDatabase.CreateAsync();
        var type = new TestSystemType { Name = "PS04" };
        var source = new Station { Name = "PS04-1", Description = "Linie 1", TestSystemTypeId = type.Id };
        source.Devices.Add(new StationDevice
        {
            DeviceKey = "Ag34980A",
            Driver = "Ag34980Scpi",
            ConnectionParametersJson = """{"resource":"TCPIP0::192.168.178.100::inst0::INSTR"}"""
        });
        source.Devices.Add(new StationDevice { DeviceKey = "PSU1", Driver = "HmpScpi", ConnectionParametersJson = """{"resource":"ASRL3::INSTR"}""" });
        await using (var db = database.NewContext())
        {
            db.TestSystemTypes.Add(type);
            db.Stations.Add(source);
            await db.SaveChangesAsync();
        }

        var copy = StationCopy.Copy(source, "PS04-2", source.TestSystemTypeId);
        await using (var db = database.NewContext())
        {
            db.Stations.Add(copy);
            await db.SaveChangesAsync();
        }

        await using var verify = database.NewContext();
        var stored = await verify.Stations.AsNoTracking().Include(station => station.Devices).SingleAsync(station => station.Name == "PS04-2");
        Assert.NotEqual(source.Id, stored.Id);
        Assert.Equal(type.Id, stored.TestSystemTypeId);
        Assert.Equal("Linie 1", stored.Description);
        Assert.Equal(["Ag34980A", "PSU1"], stored.Devices.Select(device => device.DeviceKey).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(stored.Devices, device => source.Devices.Any(original => original.Id == device.Id));
        Assert.Equal(source.Devices.Single(device => device.DeviceKey == "Ag34980A").ConnectionParametersJson,
            stored.Devices.Single(device => device.DeviceKey == "Ag34980A").ConnectionParametersJson);
        Assert.Equal(2, await verify.StationDevices.CountAsync(device => device.StationId == source.Id));
    }
}
