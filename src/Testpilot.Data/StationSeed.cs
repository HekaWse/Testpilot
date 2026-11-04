using Microsoft.EntityFrameworkCore;
using Testpilot.Core;

namespace Testpilot.Data;

/// <summary>
/// Known equipment of the test system types and the concrete test systems built from it (as specified by the test
/// department). Like <see cref="TestSystemTypeSeed"/> it only adds what is missing: a device only when the type has no
/// device with that key, a test system only when no system with that name exists. Existing devices and systems are never
/// changed or removed. Run <see cref="TestSystemTypeSeed"/> first.
/// </summary>
public static class StationSeed
{
    // Addresses from the devices.json of PS05-1 given by the test department (Alias "ag34980a" and "PSU1").
    // The HMP2030 is one instrument: PSU1/PSU2 of the legacy program are its outputs 1 and 2 (SetPower channel 1/2).
    public const string Ps05Ag34970AResource = "TCPIP0::192.168.178.100::inst0::INSTR";
    public const string Ps05Hmp2030Resource = "TCPIP0::192.168.178.101::5025::SOCKET";

    private sealed record TypeDevice(string Key, string Model, string Description, string OptionsJson);
    private sealed record InstalledDevice(string Key, string Driver, string ConnectionParametersJson);

    private static readonly Dictionary<string, TypeDevice[]> TypeDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PS05"] =
        [
            // Slot 3 is fixed by the adapter wiring (34907A ports 301/302); slots 1 and 2 follow the order given.
            new("Ag34970A", "34970A", "Datenerfassung mit 34901A (Slot 1), 34903A (Slot 2), 34907A (Slot 3)",
                """{"slots":{"1":"34901A","2":"34903A","3":"34907A"}}"""),
            new("Hmp2030", "HMP2030", "Netzteil, Ausgang 1 = PSU1, Ausgang 2 = PSU2", "{}"),
            new("Ps05Adapter", "PS05-Adapter", "Adapterschalter (301) und Niederhalter/Lampe (302) über den 34970A", "{}")
        ]
    };

    private static readonly (string Name, string Type, string Description, InstalledDevice[] Devices)[] Stations =
    [
        ("PS05-1", "PS05", "Funktionstest für Funkmodule",
        [
            new("Ag34970A", "Ag34970Scpi", $$"""{"resource":"{{Ps05Ag34970AResource}}"}"""),
            new("Hmp2030", "HmpScpi", $$"""{"resource":"{{Ps05Hmp2030Resource}}","model":"HMP2030"}"""),
            // The adapter outputs are on the same 34970A, so the adapter control uses its address.
            new("Ps05Adapter", "Ps05Fixture", $$"""{"resource":"{{Ps05Ag34970AResource}}"}""")
        ])
    ];

    /// <summary>Adds the missing devices and test systems and returns short texts of what was added.</summary>
    public static async Task<IReadOnlyList<string>> SeedAsync(TestpilotDbContext db, CancellationToken cancellationToken = default)
    {
        var types = await db.TestSystemTypes.Include(type => type.Devices).ToListAsync(cancellationToken);
        var added = new List<string>();
        foreach (var (typeName, devices) in TypeDevices)
        {
            var type = types.SingleOrDefault(item => string.Equals(item.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (type is null) continue;
            var missing = devices.Where(device => !HasDevice(type, device.Key)).ToList();
            if (missing.Count == 0) continue;
            // Added to the set (the Ids are preset, so a device only added to the navigation would count as existing);
            // EF puts the tracked device into type.Devices as well.
            foreach (var device in missing)
                db.SystemTypeDevices.Add(new SystemTypeDevice
                {
                    TestSystemTypeId = type.Id, DeviceKey = device.Key, Model = device.Model,
                    Description = device.Description, OptionsJson = device.OptionsJson
                });
            added.Add($"{type.Name}: {missing.Count} Geräte");
        }

        var stationNames = await db.Stations.Select(station => station.Name).ToListAsync(cancellationToken);
        var existingStations = new HashSet<string>(stationNames, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, typeName, description, devices) in Stations)
        {
            var type = types.SingleOrDefault(item => string.Equals(item.Name, typeName, StringComparison.OrdinalIgnoreCase));
            // Only when the type has every device of the system; otherwise the device keys would not match.
            if (type is null || existingStations.Contains(name) || devices.Any(device => !HasDevice(type, device.Key)))
                continue;
            db.Stations.Add(new Station
            {
                Name = name, Description = description, TestSystemTypeId = type.Id,
                Devices = devices.Select(device => new StationDevice
                {
                    DeviceKey = device.Key, Driver = device.Driver, ConnectionParametersJson = device.ConnectionParametersJson
                }).ToList()
            });
            added.Add(name);
        }
        if (added.Count != 0)
            await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private static bool HasDevice(TestSystemType type, string key) =>
        type.Devices.Any(device => string.Equals(device.DeviceKey, key, StringComparison.OrdinalIgnoreCase));
}
