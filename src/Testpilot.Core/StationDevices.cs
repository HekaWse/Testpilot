namespace Testpilot.Core;

/// <summary>Checks which devices a plan needs that a concrete test system (station) does not have installed.</summary>
public static class StationDevices
{
    /// <summary>Required device keys (sorted, without duplicates) that are not installed at <paramref name="station"/>.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string?> requiredDeviceKeys, Station station)
    {
        ArgumentNullException.ThrowIfNull(requiredDeviceKeys);
        ArgumentNullException.ThrowIfNull(station);
        var installed = station.Devices.Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
        return requiredDeviceKeys.OfType<string>().Where(key => !installed.Contains(key))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }
}
