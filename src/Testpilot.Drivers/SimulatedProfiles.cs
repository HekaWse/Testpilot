using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

internal static class SimulatedProfiles
{
    internal static void ValidateNoOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ConnectionParameters.EnumerateObject().Any())
            throw new System.Text.Json.JsonException($"Driver '{configuration.Driver}' does not accept connection options.");
    }

    internal static DeviceProfile Create(string id, string model, DeviceCapability capability,
        IEnumerable<int>? channelNumbers = null) =>
        SimulatedDeviceProfiles.Create(id, model, capability, channelNumbers);
}
