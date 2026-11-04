namespace Testpilot.Operator;

/// <summary>One installed device of the test system as read from the database at startup.</summary>
public sealed record SystemDeviceInfo(string DeviceKey, string? Model, string Driver, string Address)
{
    /// <summary>True when the address comes from devices.json instead of the database.</summary>
    public bool FromDevicesFile { get; init; }

    public override string ToString() =>
        $"{DeviceKey}{(Model is null ? "" : $" ({Model})")}: {Driver}{(Address.Length == 0 ? "" : $" @ {Address}")}{(FromDevicesFile ? " (devices.json)" : "")}";
}

/// <summary>
/// The test system configuration this PC runs with: the name comes from Operator:Station,
/// type, devices, drivers and addresses come from the database (editor page "Prüfsysteme"),
/// VISA addresses listed in the devices.json of this PC replace the stored ones.
/// </summary>
public sealed record SystemConfiguration(string Station, string? SystemType, IReadOnlyList<SystemDeviceInfo> Devices)
{
    /// <summary>Path of the devices.json that was read; null when there is none.</summary>
    public string? DevicesFile { get; init; }

    /// <summary>Aliases in devices.json that match no device of the station; they are ignored.</summary>
    public IReadOnlyList<string> UnknownAliases { get; init; } = [];

    public override string ToString()
    {
        var header = SystemType is null ? $"Prüfsystem {Station} (ohne Prüfsystemtyp)" : $"Prüfsystem {Station} (Typ {SystemType})";
        var lines = new List<string> { Devices.Count == 0 ? $"{header}, keine Geräte eingetragen" : header };
        lines.AddRange(Devices.Select(device => "  " + device));
        if (DevicesFile is not null)
            lines.Add($"Adressen aus {DevicesFile}");
        if (UnknownAliases.Count != 0)
            lines.Add($"devices.json: unbekannte Geräte ignoriert: {string.Join(", ", UnknownAliases)}");
        return string.Join(Environment.NewLine, lines);
    }
}
