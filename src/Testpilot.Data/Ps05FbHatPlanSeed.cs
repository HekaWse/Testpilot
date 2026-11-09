using Microsoft.EntityFrameworkCore;
using Testpilot.Core;

namespace Testpilot.Data;

/// <summary>
/// Draft test plan of the FB-HAT (4001430-01 / 332351) on PS05, rebuilt from the legacy CVI program 11010_329431
/// (test.c). Step names start with the characteristic numbers of the legacy test report; limits are the ones of
/// test.c in SI units. Steps without number are the actions between the characteristics. See
/// docs\migration\ps05-fb-hat.md for the mapping and the open points.
/// </summary>
public static class Ps05FbHatPlanSeed
{
    public const string ArtNr = "4001430-01";
    public const string Fz = "332351";

    private const string Meter = "Ag34970A";
    private const string Power = "Hmp2030";
    private const string Dut = "Dut";
    private const string GoldenSample = "GoldenSample";
    private const string Programmer = "JLink";
    private const string FirmwareDirectory = @"\\heka.de\prod\pruefmittel\ATE\PS05\cvi_prog\11010\11010_329431\Firmware\";

    // Values of the variant INI 4001430-01_332351.ini (H: is \\heka.de\prod\pruefmittel).
    private static readonly NamedSetting[] Settings =
    [
        new("Firmware", "Firmwareversion DUT", "Firm_ver_dut", SettingDataType.String, "1.6.20"),
        new("Hardware » Flasher » J-Link", "PROJFILE_PATH_1", "PROJFILE_PATH_1", SettingDataType.String,
            FirmwareDirectory + "FB_FSA.jflash"),
        new("Hardware » Flasher » J-Link", "PROGFILE_PATH_1", "PROGFILE_PATH_1", SettingDataType.String,
            FirmwareDirectory + "FW_FSA_155_F_V01.06.20.bin"),
        new("Hardware » Flasher » J-Link", "ERRORFILE_PATH_1", "ERRORFILE_PATH_1", SettingDataType.String,
            FirmwareDirectory + "segger1_error.log"),
        new("Hardware » Flasher » J-Link", "START_ADDRESS_1", "START_ADDRESS_1", SettingDataType.String, "0x8000000")
    ];

    // RF configuration of 2.4: role, rssi433, rssi868, tresh433, tresh868, correctRx433, correctTx433, correctRx868,
    // correctTx868, txPwrHigh433, txPwrLow433, txPwrHigh868, txPwrLow868.
    private static string[] RfConfiguration(string rssi868) =>
        ["4", "-101", rssi868, "5", "5", "0", "0", "0", "0", "14", "-15", "14", "-30"];

    /// <summary>Adds the draft when the PS05 type with all plan devices exists and no plan for ArtNr/Fz exists yet.</summary>
    public static async Task<IReadOnlyList<string>> SeedAsync(TestpilotDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.TestPlans.AnyAsync(plan => plan.ArtNr == ArtNr && plan.Fz == Fz, cancellationToken))
            return [];
        var type = await db.TestSystemTypes.Include(item => item.Devices)
            .SingleOrDefaultAsync(item => item.Name == "PS05", cancellationToken);
        if (type is null)
            return [];
        var plan = CreatePlan(type.Id);
        var keys = type.Devices.Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
        if (plan.Steps.Any(step => step.DeviceKey is not null && !keys.Contains(step.DeviceKey)))
            return [];
        db.TestPlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        return [$"Prüfplan {ArtNr} / {Fz} (Entwurf)"];
    }

    public static TestPlan CreatePlan(Guid? testSystemTypeId)
    {
        var s = new StepList();

        // Start: check the 1 kΩ shunt for the quiescent current (2.5), then close the measuring paths.
        s.Relay("ShuntBridged", 201, false);
        s.Relay("CloserSense", 206, false);
        s.Relay("USBSense", 207, false);
        s.Wait(200);
        s.Measure("Shunt 1 kOhm", 109, "FRES", 10000, 1, 999, 1001);
        s.Relay("CloserSense", 206, true);
        s.Relay("ShuntBridged", 201, true);
        s.Relay("USBSense", 207, true);
        s.Wait(500);

        // 2.1 Messung VDD
        s.Supply(true);
        s.Wait(1000);
        s.Measure("2.1.1 Spg. an TP1", 103, "VOLT:DC", null, 1, 2.97, 3.03);

        // 2.2 Programmierung Release Firmware
        s.Program("2.2.1 Programmierung Firmware", "{{plan.PROGFILE_PATH_1}}").RetryCount = 1;
        s.Calculate("2.2.2 ROP Level 1 gesetzt", "1", 1, 1, MeasurementUnit.Number);
        s.Relay("Reset", 204, true);
        s.Wait(400);
        s.Relay("Reset", 204, false);
        s.Supply(false);
        s.Wait(1000);
        s.Supply(true);
        s.Wait(1000);
        s.Command(Dut, "SetProdMode");
        s.Wait(50);
        s.Generic(Dut, 8, 5);
        s.Wait(2000);
        s.Command(Dut, "ResetDevice");
        s.Wait(2000);
        s.Command(Dut, "SetProdMode");
        s.Wait(50);
        s.SelfTest("2.2.3 Selbsttest erfolgreich durchgeführt");
        s.Generic(Dut, 5, 1);
        s.Wait(50);
        s.Add("2.2.4 Firmwareversion korrekt zurückgelesen", StepType.DutCommand, Dut,
            StepParameters.Serialize(new DutCommandParameters("ReadFirmwareVersion", Text: "{major}.{minor}.{deb}",
                Expected: "{{plan.Firm_ver_dut}}")));
        s.Wait(50);

        // 2.3 EEPROM Spannung messen
        s.Generic(Dut, 8, 4);
        s.Wait(50);
        var eepromOff = s.Measure("2.3.1 EEPROM Spannung AUS messen Dif zu GND", 101, "VOLT:DC", null, 10, -0.1, 0.1);
        eepromOff.RetryCount = 29;
        eepromOff.RetryDelayMilliseconds = 25;
        s.Generic(Dut, 8, 3);
        s.Wait(200);
        s.Measure("U EEPROM ein 101", 101, "VOLT:DC", null, 10);
        s.Measure("VCC EEPROM ein 103", 103, "VOLT:DC", null, 1);
        s.Calculate("2.3.2 EEPROM Spannung EIN messen Dif zu VCC", "{VCC EEPROM ein 103} - {U EEPROM ein 101}",
            -0.03, 0.03, MeasurementUnit.Volt);
        s.Generic(Dut, 8, 4);

        // 2.4 Fertigungsdaten programmieren
        s.Command(Dut, "WriteMainConfigBuffer", "32", "{serial}");
        s.Wait(50);
        s.Command(Dut, "WriteRfConfigBuffer", RfConfiguration("-106"));
        s.Wait(50);
        s.Command(Dut, "ResetDevice");
        s.Wait(1500);
        s.Command(Dut, "SetProdMode");
        s.Wait(50);
        s.Generic(Dut, 5, 1);
        s.Wait(50);
        s.Named(Dut, "DUT Main-Config lesen", "ReadMainConfigBuffer");
        s.Wait(50);
        s.Named(Dut, "DUT RF-Config lesen", "ReadRfConfigBuffer");
        s.Number("2.4.1 Device Type korrekt zurückgelesen", "{DUT Main-Config lesen:deviceType}", 32);
        s.Number("2.4.2 Seriennummer korrekt zurückgelesen", "{DUT Main-Config lesen:serial} == {serial}", 1);
        s.Number("2.4.3 Role korrekt zurückgelesen", "{DUT RF-Config lesen:roleConf}", 4);
        s.Number("2.4.4 RSSI Band 433 korrekt zurückgelesen", "{DUT RF-Config lesen:rssi433}", -101);
        s.Number("2.4.5 RSSI Band 868 korrekt zurückgelesen", "{DUT RF-Config lesen:rssi868}", -106);
        s.Number("2.4.6 Tresh Band 433 korrekt zurückgelesen", "{DUT RF-Config lesen:tresh433}", 5);
        s.Number("2.4.7 Tresh Band 868 korrekt zurückgelesen", "{DUT RF-Config lesen:tresh868}", 5);
        s.Number("2.4.8 Korrekturfaktor Empfangen Band 433 korrekt zurückgelesen", "trunc({DUT RF-Config lesen:correctRx433})", 0);
        s.Number("2.4.9 Korrekturfaktor Senden Band 433 korrekt zurückgelesen", "trunc({DUT RF-Config lesen:correctTx433})", 0);
        s.Number("2.4.10 Korrekturfaktor Empfangen Band 868 korrekt zurückgelesen", "trunc({DUT RF-Config lesen:correctRx868})", 0);
        s.Number("2.4.11 Korrekturfaktor Senden Band 868 korrekt zurückgelesen", "trunc({DUT RF-Config lesen:correctTx868})", 0);
        s.Number("2.4.12 TX Power High, Band 433 korrekt zurückgelesen", "{DUT RF-Config lesen:txPwrHigh433}", 14);
        s.Number("2.4.13 TX Power Low, Band 433 korrekt zurückgelesen", "{DUT RF-Config lesen:txPwrLow433}", -15);
        s.Number("2.4.14 TX Power High, Band 868 korrekt zurückgelesen", "{DUT RF-Config lesen:txPwrHigh868}", 14);
        s.Number("2.4.15 TX Power Low, Band 868 korrekt zurückgelesen", "{DUT RF-Config lesen:txPwrLow868}", -30);
        s.Generic(Dut, 21, 3);
        s.Wait(50);
        s.Measure("VCC Idle 103", 103, "VOLT:DC", null, 1);
        s.Measure("VCC Last 114", 114, "VOLT:DC", null, 1);
        s.Generic(Dut, 21, 4);
        s.Wait(50);
        s.Calculate("2.4.16 Plausibilitaet Korrekturfaktor", "{VCC Last 114} / {VCC Idle 103}", 0.956, 0.976,
            MeasurementUnit.Number);
        s.Command(Dut, "WriteSupplyConfigBuffer", "2602", "2428");
        s.Wait(50);
        s.Named(Dut, "DUT Versorgungs-Config lesen", "ReadSupplyConfigBuffer");
        s.Wait(50);
        s.Number("2.4.17 Spannungswert Bat-Warning korrekt eingetragen", "{DUT Versorgungs-Config lesen:limitLoadHigh}", 2602);
        s.Number("2.4.18 Spannungswert Bat-Error korrekt eingetragen", "{DUT Versorgungs-Config lesen:limitLoadLow}", 2428);

        // 2.5 Ruhestromaufnahme: voltage over the 1 kΩ shunt, 5 readings, the 2 highest are discarded.
        s.Generic(Dut, 5, 1);
        s.Wait(50);
        s.Command(Dut, "SetRfTestmode", "1", "0", "0", "0");
        s.Wait(50);
        s.Relay("CloserSense", 206, false);
        s.Relay("ShuntBridged", 201, false);
        s.Wait(1000);
        s.Measure("U Shunt Ruhestrom 109", 109, "VOLT:DC", 0.1, 1, samples: 5, discardHighest: 2);
        s.Calculate("2.5.1 Ruhestromaufnahme", "{U Shunt Ruhestrom 109} / 1000", 3.0e-6, 5.1e-6, MeasurementUnit.Ampere);
        s.Relay("ShuntBridged", 201, true);
        s.Relay("CloserSense", 206, true);
        s.Wait(50);
        s.Command(Dut, "SetProdMode");
        s.Wait(50);

        // 2.6 Interne VDD Messung
        s.Generic(Dut, 5, 1);
        s.Wait(50);
        s.Measure("2.6.1 Spg. an VCC-Ferrit VddMultiIdle", 114, "VOLT:DC", null, 1, 2.975, 3.015);
        s.Generic(Dut, 21, 3);
        s.Wait(50);
        s.Measure("2.6.2 Load Current", 121, "CURR:DC", 1, 10, 0.140, 0.149);
        s.Measure("2.6.3 Spg. an VCC-Ferrit VddMultiLoad", 114, "VOLT:DC", null, 1, 2.85, 3.015);
        s.Generic(Dut, 21, 4);
        s.Wait(50);
        s.Generic(Dut, 21, 1);
        s.Wait(50);
        s.Generic(Dut, 21, 2);
        s.Wait(50);
        s.Generic(Dut, 21, 6);
        s.Measure("R PT1000 115", 115, "RES", 10000, 1);
        s.Calculate("Temperatur PT1000", "({R PT1000 115} - 1000) / 3.91", null, null, MeasurementUnit.Celsius);
        s.Wait(50);
        s.Named(Dut, "DUT Versorgungsinfo lesen", "ReadSupplyInfoBuffer");
        s.Wait(50);
        s.Calculate("2.6.4 Delta Idle", "{2.6.1 Spg. an VCC-Ferrit VddMultiIdle} - {DUT Versorgungsinfo lesen:vccIdle} / 1000",
            -0.02, 0.02, MeasurementUnit.Volt);
        s.Calculate("2.6.5 Delta Load", "{2.6.3 Spg. an VCC-Ferrit VddMultiLoad} - {DUT Versorgungsinfo lesen:vccLoad} / 1000",
            -0.02, 0.02, MeasurementUnit.Volt);
        s.Calculate("2.6.6 Diff Intern Temp zu PT1000", "{DUT Versorgungsinfo lesen:temperature} - {Temperatur PT1000}",
            -8, 8, MeasurementUnit.Celsius);

        // 2.7 Quarzfrequenz
        s.Generic(Dut, 2, 8);
        s.Wait(50);
        s.Measure("2.7.1 Quarzfrequenz an TP10", 108, "FREQ", null, null, 32764.7, 32771.3);
        s.Generic(Dut, 2, 9);
        s.Measure("Frequenz TP10 nach Testkommando 2/9", 108, "FREQ", null, null);

        // 2.8 RF Daten senden: DUT sends, golden sample receives.
        s.Command(GoldenSample, "ResetDevice");
        s.Command(Dut, "ResetDevice");
        s.Wait(1500);
        s.Command(GoldenSample, "SetProdMode");
        s.Wait(50);
        s.Generic(GoldenSample, 5, 1);
        s.Wait(50);
        s.Command(Dut, "SetProdMode");
        s.Wait(50);
        s.Generic(Dut, 5, 1);
        s.Wait(50);
        s.Named(GoldenSample, "GS RF-Config lesen", "ReadRfConfigBuffer");
        s.Wait(50);
        s.RfIdle();
        s.Wait(50);
        s.Command(Dut, "StartSending", "0", "4");
        s.Wait(50);
        s.Named(GoldenSample, "GS empfangen 433", "ReceivingData", "0", "4", "5");
        s.Calculate("2.8.1 DUT zu GS 433 RSSI Kanal 4", "trunc(mean({GS empfangen 433:rssi})) + {GS RF-Config lesen:correctTx433} / 10",
            -63, -57, MeasurementUnit.DecibelMilliwatt);
        s.Wait(50);
        s.RfIdle();
        s.Wait(50);
        s.Command(Dut, "StartSending", "1", "0");
        s.Wait(50);
        s.Named(GoldenSample, "GS empfangen 868", "ReceivingData", "1", "0", "5");
        s.Calculate("2.8.5 DUT zu GS 868 RSSI Kanal 0", "trunc(mean({GS empfangen 868:rssi})) + {GS RF-Config lesen:correctTx868} / 10",
            -64, -58, MeasurementUnit.DecibelMilliwatt);
        s.Wait(50);
        s.RfIdle();

        // 2.9 RF Daten empfangen: golden sample sends, DUT receives. A DUT that receives 868 MHz stronger than the
        // clipping value gets a higher threshold (Uesw 868).
        s.RfIdle();
        s.Wait(50);
        s.Command(GoldenSample, "StartSending", "0", "4");
        s.Wait(50);
        s.Named(Dut, "DUT empfangen 433", "ReceivingData", "0", "4", "5");
        s.Calculate("2.9.1 GS zu DUT 433 RSSI Kanal 4", "trunc(mean({DUT empfangen 433:rssi})) + {GS RF-Config lesen:correctRx433} / 10",
            -59.5, -54.5, MeasurementUnit.DecibelMilliwatt);
        s.Wait(50);
        s.RfIdle();
        s.Wait(50);
        s.Command(GoldenSample, "StartSending", "1", "0");
        s.Wait(50);
        s.Named(Dut, "DUT empfangen 868", "ReceivingData", "1", "0", "5");
        s.Calculate("2.9.5 GS zu DUT 868 RSSI Kanal 0", "trunc(mean({DUT empfangen 868:rssi})) + {GS RF-Config lesen:correctRx868} / 10",
            -59, -53, MeasurementUnit.DecibelMilliwatt);
        s.Wait(50);
        s.RfIdle();
        s.Calculate("2.9.6 Clipping Wert", "-55", -55, -55, MeasurementUnit.DecibelMilliwatt);
        s.Calculate("2.9.7 Uesw 868 Wert nach Clipping-Vergleich",
            "-106 + max(0, trunc({2.9.5 GS zu DUT 868 RSSI Kanal 0}) - {2.9.6 Clipping Wert})",
            -120, -100, MeasurementUnit.DecibelMilliwatt);
        s.Command(Dut, "WriteRfConfigBuffer", RfConfiguration("{2.9.7 Uesw 868 Wert nach Clipping-Vergleich}"));
        s.Wait(50);
        s.Named(Dut, "DUT RF-Config zurücklesen", "ReadRfConfigBuffer");
        s.Number("2.9.8 Uesw 868 korrekt eingetragen",
            "{DUT RF-Config zurücklesen:rssi868} == {2.9.7 Uesw 868 Wert nach Clipping-Vergleich}", 1);

        // 2.10 Sendestrom (RF test mode 4 = send continuously)
        s.Generic(Dut, 5, 1);
        s.Wait(50);
        s.RfCurrent("2.10.1 Sendestrom Band 433MHz Kanal 4", "Spg. an TP1 beim Senden 433", 4, "0", "4", 0.013, 0.021);
        s.RfCurrent("2.10.2 Sendestrom Band 868MHz Kanal 0", "Spg. an TP1 beim Senden 868", 4, "1", "0", 0.016, 0.025);
        s.Command(Dut, "SetRfTestmode", "0", "0", "0", "0");
        s.Wait(50);

        // 2.11 Empfangsstrom (RF test mode 2 = receive continuously)
        s.RfCurrent("2.11.1 Empfangsstrom Band 433MHz Kanal 4", null, 2, "0", "4", 0.008, 0.010);
        s.RfCurrent("2.11.2 Empfangsstrom Band 868MHz Kanal 0", null, 2, "1", "0", 0.008, 0.010);
        s.Command(Dut, "SetRfTestmode", "0", "0", "0", "0");

        // 2.12 Ermitteln der XO-Settling Time
        s.Generic(Dut, 9, 5, "2.12.1 XO-Settling Time", "{data[0]}", 3, 7);

        // 2.13 Reboot
        s.Add("2.13.1 Reboot erfolgreich durchgeführt", StepType.DutCommand, Dut,
            StepParameters.Serialize(new DutCommandParameters("ResetDevice")), 1, 1, MeasurementUnit.Number);
        s.Wait(1500);
        s.Command(Dut, "SetProdMode");
        s.Wait(50);
        s.Generic(Dut, 5, 1);
        s.Wait(50);

        // 2.15 Selbsttest
        s.SelfTest("2.15.1 Selbsttest erfolgreich durchgeführt");

        // 2.16 LED Test: LED off (0 V), then green and red on; the LED voltage is VCC minus the cathode voltage.
        s.Generic(Dut, 5, 1);
        s.Wait(200);
        var discharge = s.Measure("LED rot entladen TP12", 110, "VOLT:DC", 0.1, 1, null, 0.0001);
        discharge.RetryCount = 4;
        discharge.RetryDelayMilliseconds = 50;
        s.Measure("2.16.1 LED grün Test aus an TP11", 111, "VOLT:DC", 0.1, 1, -0.0001, 0.0001);
        s.Measure("2.16.2 LED rot Test aus an TP12", 110, "VOLT:DC", 0.1, 1, -0.0001, 0.0001);
        s.Generic(Dut, 5, 3);
        s.Wait(200);
        s.Measure("VCC LED grün 114", 114, "VOLT:DC", null, 1);
        s.Measure("2.16.4 V-UI an LED grün (TP11)", 111, "VOLT:DC", 0.1, 1, 0.07307, 0.082);
        s.Measure("U Kathode LED grün 113", 113, "VOLT:DC", 10, 1);
        s.Calculate("2.16.5 U LED grün, Diff zu VCC (TP28)", "{VCC LED grün 114} - {U Kathode LED grün 113}",
            1.75, 2.35, MeasurementUnit.Volt);
        s.Generic(Dut, 5, 1);
        s.Wait(200);
        s.Generic(Dut, 5, 2);
        s.Wait(200);
        s.Measure("VCC LED rot 114", 114, "VOLT:DC", null, 1);
        s.Measure("2.16.7 V-UI an LED rot (TP12)", 110, "VOLT:DC", 0.1, 1, 0.07307, 0.082);
        s.Measure("U Kathode LED rot 112", 112, "VOLT:DC", 10, 1);
        s.Calculate("2.16.8 U LED rot, Diff zu VCC (TP29)", "{VCC LED rot 114} - {U Kathode LED rot 112}",
            1.75, 2.35, MeasurementUnit.Volt);
        s.Generic(Dut, 5, 1);

        // 2.18 - 2.20 Taster: relay pressed -> state 0, released -> state 1.
        s.Button("KEY", 202, 5, 5, "2.18.1 Inbetriebnahme Taster gedrückt (KeyPressed)",
            "2.18.2 Inbetriebnahme Taster nicht gedrückt (KeyReleased)");
        s.Button("Demount", 203, 15, 3, "2.19.1 Demontage Taster gedrückt (MountState)",
            "2.19.2 Demontage Taster nicht gedrückt (DemountState)");
        s.Button("Alarm", 205, 15, 9, "2.20.1 Alarm Taster gedrückt (Alarm)", "2.20.2 Alarm Taster nicht gedrückt (NotAlarm)");
        s.Wait(50);
        // End: the cleanup phase switches the supply off and opens all relays (safe state of every device).

        return new TestPlan
        {
            ArtNr = ArtNr, Fz = Fz, Version = 1, Status = PlanStatus.Draft, TestSystemTypeId = testSystemTypeId,
            SettingsJson = NamedSettings.Serialize(Settings), Steps = s.Steps
        };
    }

    private sealed class StepList
    {
        public List<TestStep> Steps { get; } = [];

        public TestStep Add(string name, StepType type, string? deviceKey, string parametersJson,
            double? low = null, double? high = null, MeasurementUnit? unit = null)
        {
            var step = new TestStep
            {
                Order = Steps.Count, Name = name, StepType = type, DeviceKey = deviceKey, ParametersJson = parametersJson,
                LimitLow = low, LimitHigh = high, Unit = unit
            };
            Steps.Add(step);
            return step;
        }

        public TestStep Measure(string name, int channel, string function, double? range, double? nplc,
            double? low = null, double? high = null, int? samples = null, int? discardHighest = null) =>
            Add(name, StepType.Measure, Meter,
                StepParameters.Serialize(new MeasureParameters(channel, function, range, nplc, samples, discardHighest)),
                low, high, StepParameters.UnitOf(function));

        public void Relay(string function, int channel, bool closed) =>
            Add($"Relais {channel} {function} {(closed ? "ein" : "aus")}", StepType.Relay, Meter,
                StepParameters.Serialize(new RelayParameters(channel, closed)));

        // PSU1 (HMP2030 output 1) supplies the DUT with 3.0 V.
        public void Supply(bool enabled) =>
            Add($"Versorgung PSU1 3,0 V {(enabled ? "ein" : "aus")}", StepType.SetPower, Power,
                StepParameters.Serialize(new SetPowerParameters(3.0, 0.5, enabled)));

        public void Wait(double milliseconds) =>
            Add($"Warten {milliseconds} ms", StepType.Wait, null, StepParameters.Serialize(new WaitParameters(milliseconds)));

        public TestStep Program(string name, string filePath) =>
            Add(name, StepType.Program, Programmer, StepParameters.Serialize(new ProgramParameters(filePath)));

        public TestStep Calculate(string name, string formula, double? low, double? high, MeasurementUnit unit) =>
            Add(name, StepType.Calculate, null, StepParameters.Serialize(new CalculateParameters(formula)), low, high, unit);

        /// <summary>Calculated number that must equal <paramref name="expected"/> (comparisons give 1 or 0).</summary>
        public void Number(string name, string formula, double expected) =>
            Calculate(name, formula, expected, expected, MeasurementUnit.Number);

        public void Command(string deviceKey, string operation, params string[] arguments) =>
            Named(deviceKey, $"{Prefix(deviceKey)} {operation}", operation, arguments);

        /// <summary>Command whose outputs later steps use as <c>{name:output}</c>.</summary>
        public void Named(string deviceKey, string name, string operation, params string[] arguments) =>
            Add(name, StepType.DutCommand, deviceKey,
                StepParameters.Serialize(new DutCommandParameters(operation, arguments.Length == 0 ? null : arguments)));

        public void Generic(string deviceKey, int taskAddress, int command) =>
            Named(deviceKey, $"{Prefix(deviceKey)} Testkommando {taskAddress}/{command}", "SendGenericTestCommand",
                taskAddress.ToString(System.Globalization.CultureInfo.InvariantCulture),
                command.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public void Generic(string deviceKey, int taskAddress, int command, string name, string output, double low, double high) =>
            Add(name, StepType.DutCommand, deviceKey, StepParameters.Serialize(new DutCommandParameters(
                "SendGenericTestCommand",
                [taskAddress.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    command.ToString(System.Globalization.CultureInfo.InvariantCulture)], output)),
                low, high, MeasurementUnit.Number);

        // Self-test buffer of the DUT: error counter (byte 0) at most 16 and error flag (byte 1) at most 1.
        public void SelfTest(string name) =>
            Add(name, StepType.DutCommand, Dut, StepParameters.Serialize(new DutCommandParameters(
                "ReadInfoConfigBuffer", ["1", "0", "4", "4"], "({data[0]} <= 16) * ({data[1]} <= 1)")),
                1, 1, MeasurementUnit.Number);

        public void RfIdle()
        {
            Command(Dut, "SetRfTestmode", "0", "0", "0", "0");
            Command(GoldenSample, "SetRfTestmode", "0", "0", "0", "0");
        }

        /// <summary>DUT current in RF test mode <paramref name="mode"/> on band/channel; optionally VCC at TP1 for information.</summary>
        public void RfCurrent(string name, string? voltageName, int mode, string band, string channel, double low, double high)
        {
            Command(Dut, "SetRfTestmode", "0", "0", "0", "0");
            Wait(50);
            Command(Dut, "SetChannelParameter", band, channel);
            Wait(50);
            Command(Dut, "SetRfTestmode", mode.ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "0", "0");
            Wait(50);
            if (voltageName is not null)
                Measure(voltageName, 103, "VOLT:DC", null, 1);
            Measure(name, 121, "CURR:DC", 0.1, 10, low, high);
        }

        public void Button(string function, int relay, int taskAddress, int command, string pressedName, string releasedName)
        {
            Relay(function, relay, true);
            Wait(200);
            Generic(Dut, taskAddress, command, pressedName, "{data[0]}", 0, 0);
            Wait(50);
            Relay(function, relay, false);
            Wait(200);
            Generic(Dut, taskAddress, command, releasedName, "{data[0]}", 1, 1);
            Wait(50);
        }

        private static string Prefix(string deviceKey) => deviceKey == GoldenSample ? "GS" : "DUT";
    }
}
