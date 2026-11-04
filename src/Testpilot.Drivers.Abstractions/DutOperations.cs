namespace Testpilot.Drivers.Abstractions;

/// <summary>
/// One command of a device-under-test library. <see cref="Inputs"/> are the numeric arguments in call order,
/// <see cref="Outputs"/> the returned values (names as in the library, e.g. "major", "data").
/// </summary>
public sealed record DutOperation(string Name, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs,
    string Description);

/// <summary>
/// Commands of the FSA production DLL (FSACommunication.dll, class FSACommunication.Communication).
/// The names are the method and parameter names of the DLL; the real driver calls them by reflection.
/// Open and Close are called by the driver itself when connecting and disconnecting.
/// Methods with array inputs (WriteInfoConfigBuffer, SendGenericHostCommand) are not supported yet.
/// </summary>
public static class FsaOperations
{
    private static readonly string[] RfConfig =
    [
        "roleConf", "rssi433", "rssi868", "tresh433", "tresh868", "correctRx433", "correctTx433", "correctRx868",
        "correctTx868", "txPwrHigh433", "txPwrLow433", "txPwrHigh868", "txPwrLow868"
    ];

    public static IReadOnlyList<DutOperation> All { get; } =
    [
        new("SetProdMode", [], [], "Produktionsmodus setzen"),
        new("SetTestMode", [], [], "Testmodus setzen"),
        new("ResetDevice", [], [], "Gerät zurücksetzen"),
        new("ReadType", [], ["type"], "Gerätetyp lesen"),
        new("ReadFirmwareVersion", [], ["major", "minor", "deb"], "Firmwareversion lesen"),
        new("ReadRssi", [], ["rssi"], "RSSI lesen"),
        new("ReadEeprom", ["adress", "length"], ["data"], "EEPROM lesen"),
        new("ReadInfoConfigBuffer", ["taskAdr", "buffer", "address", "length"], ["data"], "Info-Config-Puffer lesen"),
        new("SendGenericTestCommand", ["taskAddress", "cmd"], ["data"], "Generisches Testkommando"),
        new("SetChannelParameter", ["band", "channel"], [], "Funkband und -kanal setzen"),
        new("SetRfTestmode", ["mode", "packageCount", "treshUp", "treshDown"], [], "RF-Testmodus setzen"),
        new("ReadMainConfigBuffer", [], ["deviceType", "serial"], "Main-Config lesen"),
        new("WriteMainConfigBuffer", ["deviceType", "serial"], [], "Main-Config schreiben"),
        new("ReadRfConfigBuffer", [], RfConfig, "RF-Config lesen"),
        new("WriteRfConfigBuffer", RfConfig, [], "RF-Config schreiben"),
        new("ReadSupplyInfoBuffer", [], ["temperature", "vccIdle", "vccLoad", "loadTempComp"], "Versorgungsinfo lesen"),
        new("ReadSupplyConfigBuffer", [], ["limitLoadHigh", "limitLoadLow"], "Versorgungs-Config lesen"),
        new("WriteSupplyConfigBuffer", ["limitLoadHigh", "limitLoadLow"], [], "Versorgungs-Config schreiben"),
        new("StartSending", ["band", "channel"], [], "Senden starten"),
        new("StopSending", [], [], "Senden beenden"),
        new("ReceivingData", ["band", "channel", "packageCount"], ["rssi", "pqi", "sqi", "afc"], "Funkpakete empfangen"),
        new("GetDateTime", [], ["year", "month", "day", "hours", "minutes", "seconds"], "Uhrzeit lesen"),
        new("SetDatetime", ["year", "month", "day", "hours", "minutes", "seconds"], [], "Uhrzeit setzen")
    ];

    public static DutOperation? Find(string name) =>
        All.FirstOrDefault(operation => string.Equals(operation.Name, name, StringComparison.Ordinal));

    /// <summary>Checks the operation name and the number of arguments.</summary>
    public static DutOperation GetRequired(string name, int argumentCount)
    {
        var operation = Find(name) ?? throw new NotSupportedException(
            $"Unknown FSA operation '{name}'. Known: {string.Join(", ", All.Select(item => item.Name))}.");
        if (operation.Inputs.Count != argumentCount)
            throw new ArgumentException(
                $"FSA operation '{name}' expects {operation.Inputs.Count} arguments ({string.Join(", ", operation.Inputs)}), not {argumentCount}.");
        return operation;
    }

    /// <summary>Converts an argument to a whole number; the DLL only takes integers.</summary>
    public static long ToInteger(double value, string operation, string parameter)
    {
        if (!double.IsFinite(value) || Math.Truncate(value) != value || Math.Abs(value) > uint.MaxValue)
            throw new ArgumentOutOfRangeException(parameter,
                FormattableString.Invariant($"FSA operation '{operation}': '{parameter}' must be a whole number, not {value}."));
        return (long)value;
    }
}
