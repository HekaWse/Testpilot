namespace Testpilot.Operator;

/// <summary>One installed device of the test system as read from the database at startup.</summary>
public sealed record SystemDeviceInfo(string DeviceKey, string? Model, string Driver, string Address)
{
    public override string ToString() =>
        $"{DeviceKey}{(Model is null ? "" : $" ({Model})")}: {Driver}{(Address.Length == 0 ? "" : $" @ {Address}")}";
}

/// <summary>
/// The test system configuration this PC runs with: the name comes from Operator:Station,
/// type, devices, drivers and addresses come from the database (editor page "Prüfsysteme").
/// </summary>
public sealed record SystemConfiguration(string Station, string? SystemType, IReadOnlyList<SystemDeviceInfo> Devices)
{
    public override string ToString()
    {
        var header = SystemType is null ? $"Prüfsystem {Station} (ohne Prüfsystemtyp)" : $"Prüfsystem {Station} (Typ {SystemType})";
        return Devices.Count == 0
            ? $"{header}, keine Geräte eingetragen"
            : header + Environment.NewLine + string.Join(Environment.NewLine, Devices.Select(device => "  " + device));
    }
}
