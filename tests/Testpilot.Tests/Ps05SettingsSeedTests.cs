using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using Testpilot.Data;

namespace Testpilot.Tests;

public sealed class Ps05SettingsSeedTests
{
    [Fact]
    public async Task NewStationGetsAllSuppliedValuesAndSeedIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await TestSystemTypeSeed.SeedAsync(database.Context);
        await StationSeed.SeedAsync(database.Context);
        await using var verify = database.NewContext();
        var station = await verify.Stations.SingleAsync(item => item.Name == "PS05-1");
        var settings = NamedSettings.Deserialize(station.SettingsJson).ToDictionary(setting => setting.Alias);
        Assert.Equal(10, settings.Count);
        Assert.All(settings.Values, setting => Assert.Equal(SettingDataType.String, setting.DataType));
        Assert.Equal("COM9", settings["COMPort_Debug"].Value);
        Assert.Equal("COM3", settings["COMPort_Debug_Ref"].Value);
        Assert.Equal("COM4", settings["COMPort_Datalogic"].Value);
        Assert.Equal("COM-Port DUT", settings["COMPort_Debug"].Name);
        Assert.Equal("Kommunikation » Debug » Debug Interface", settings["COMPort_Debug"].Directory);
        Assert.Equal(@"C:\Program Files (x86)\SEGGER\JLink_V612f\JFlash.exe", settings["JFlashExecutablePath"].Value);
        Assert.Equal("850102034", settings["SERIAL_NUMBER_1"].Value);
        Assert.Equal("No", settings["HIDE_EXE"].Value);
        Assert.Equal("0x8000000", settings["START_ADDRESS_1"].Value);
        const string firmwareDirectory = @"\\heka.de\prod\pruefmittel\ATE\PS05\cvi_prog\11005\11005_327041\Firmware\";
        Assert.Equal(firmwareDirectory + "segger1_error.log", settings["ERRORFILE_PATH_1"].Value);
        Assert.Equal(firmwareDirectory + "FW_FSA_155_F_V01.05.18.bin", settings["PROGFILE_PATH_1"].Value);
        Assert.Equal(firmwareDirectory + "FB_FSA.jflash", settings["PROJFILE_PATH_1"].Value);
        Assert.Empty(await StationSeed.SeedAsync(verify));
        Assert.Empty(await verify.TestPlans.ToListAsync());
    }

    [Fact]
    public async Task ExistingStationGetsMissingSettingsWithoutOverwritingCustomValues()
    {
        await using var database = await TestDatabase.CreateAsync();
        var station = new Station
        {
            Name = "PS05-1",
            SettingsJson = NamedSettings.Serialize([
                new("Custom", "Scanner", "comport_datalogic", SettingDataType.String, "COM12"),
                new("Custom", "Other", "Other", SettingDataType.Boolean, "true")])
        };
        var unrelated = new Station { Name = "PS05-2" };
        database.Context.Stations.AddRange(station, unrelated);
        await database.Context.SaveChangesAsync();

        Assert.Equal(["PS05-1: 9 Einstellungen"], await StationSeed.SeedAsync(database.Context));

        await using var verify = database.NewContext();
        var stored = await verify.Stations.SingleAsync(item => item.Id == station.Id);
        var settings = NamedSettings.Deserialize(stored.SettingsJson);
        Assert.Equal(11, settings.Count);
        Assert.Equal("COM12", settings.Single(setting => setting.Alias == "comport_datalogic").Value);
        Assert.Equal("true", settings.Single(setting => setting.Alias == "Other").Value);
        Assert.Equal("[]", (await verify.Stations.SingleAsync(item => item.Id == unrelated.Id)).SettingsJson);
        Assert.Empty(await StationSeed.SeedAsync(verify));
    }
}
