namespace Testpilot.Drivers.Abstractions;

/// <summary>
/// Builds simple profiles from a capability and a channel list. Used by the simulated drivers,
/// by drivers without channels (for example the J-Flash programmer) and by the device catalog.
/// </summary>
public static class SimulatedDeviceProfiles
{
    public static readonly IReadOnlyList<string> MeasurementFunctions = ["VOLT:DC", "VOLT:AC", "CURR:DC", "CURR:AC", "RES", "FRES"];

    public static DeviceProfile Create(string id, string model, DeviceCapability capability,
        IEnumerable<int>? channelNumbers = null)
    {
        var numbers = (channelNumbers ?? [1]).Distinct().Order().ToArray();
        if (numbers.Length == 0 || numbers.Length > 10_000 || numbers.Any(number => number <= 0))
            throw new ArgumentOutOfRangeException(nameof(channelNumbers), "Simulated channels must be positive and limited to 10,000.");
        var settings = capability == DeviceCapability.SwitchMultimeter
            ? new HashSet<string>(["function", "range", "nplc"], StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var state = new ChannelStateProfile(settings,
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["relay"] = new HashSet<string>(["*"], StringComparer.Ordinal),
                // Like the 34980A, each channel keeps its own configuration.
                ["configure"] = new HashSet<string>(StringComparer.Ordinal),
                ["measure"] = new HashSet<string>(StringComparer.Ordinal)
            }, UnknownCommandInvalidatesAll: true);
        var supportedFunctions = capability switch
        {
            DeviceCapability.SwitchMultimeter => new HashSet<string>(MeasurementFunctions, StringComparer.Ordinal),
            DeviceCapability.PowerSupply => new HashSet<string>([PowerOutputLimits.Voltage, PowerOutputLimits.CurrentLimit], StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
        var channelType = capability == DeviceCapability.PowerSupply ? ChannelType.PowerOutput : ChannelType.GeneralPurpose;
        var channels = numbers.Select(number => new ChannelProfile(number, $"Channel {number}", channelType,
            supportedFunctions, new Dictionary<string, NumericRange>(StringComparer.Ordinal), state)).ToArray();
        return new DeviceProfile(id, model, channels.Length, "channel", new HashSet<DeviceCapability> { capability },
            state, channels, [], []);
    }
}
