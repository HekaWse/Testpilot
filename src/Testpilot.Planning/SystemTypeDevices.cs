using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Planning;

/// <summary>Optimizer input for all devices of a test system, built from the device catalog.</summary>
public static class SystemTypeDevices
{
    public static IReadOnlyDictionary<string, DeviceDefinition> CreateDefinitions(TestSystemType system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var definitions = new Dictionary<string, DeviceDefinition>(StringComparer.Ordinal);
        foreach (var device in system.Devices)
        {
            var profile = DeviceCatalog.CreateProfile(device.Model, device.OptionsJson);
            var model = DeviceCatalog.Get(device.Model).Name;
            if (!definitions.TryAdd(device.DeviceKey, DeviceDefinition.ForSystemDevice(model, profile)))
                throw new ArgumentException($"Gerätekennung '{device.DeviceKey}' ist im Prüfsystem '{system.Name}' doppelt.");
        }
        return definitions;
    }
}
