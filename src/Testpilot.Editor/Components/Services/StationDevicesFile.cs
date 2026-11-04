using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Editor.Services;

/// <summary>devices.json of one test system: export of its VISA addresses and takeover of addresses from such a file.</summary>
public static class StationDevicesFile
{
    /// <summary>All devices with a VISA driver and resource as <c>[{"Alias": device key, "Resource": …}]</c>.</summary>
    public static string Export(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return DeviceAddressFile.ToJson(station.Devices
            .Where(device => ConnectionAddress.VisaDrivers.Contains(device.Driver))
            .Select(device => new DeviceAddressFile.Entry(device.DeviceKey, ConnectionAddress.GetResource(device.ConnectionParametersJson)))
            .Where(entry => entry.Resource.Length != 0)
            .OrderBy(entry => entry.Alias, StringComparer.Ordinal));
    }

    /// <summary>
    /// Sets the VISA resources of the devices listed in <paramref name="json"/> (alias = device key, case-insensitive).
    /// <paramref name="station"/> must include its devices; the caller saves. Returns the changed device keys and the
    /// aliases that match no device of the station.
    /// </summary>
    public static (IReadOnlyList<string> Updated, IReadOnlyList<string> Unknown) Apply(Station station, string json)
    {
        ArgumentNullException.ThrowIfNull(station);
        var addresses = DeviceAddressFile.Parse(json ?? "");
        var updated = new List<string>();
        foreach (var device in station.Devices.OrderBy(device => device.DeviceKey, StringComparer.Ordinal))
        {
            var parameters = DeviceAddressFile.Apply(addresses, device.DeviceKey, device.Driver, device.ConnectionParametersJson);
            if (ConnectionAddress.GetResource(parameters) == ConnectionAddress.GetResource(device.ConnectionParametersJson))
                continue;
            device.ConnectionParametersJson = parameters;
            updated.Add(device.DeviceKey);
        }
        var unknown = addresses.Keys
            .Where(alias => !station.Devices.Any(device => StringComparer.OrdinalIgnoreCase.Equals(device.DeviceKey, alias)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return (updated, unknown);
    }
}
