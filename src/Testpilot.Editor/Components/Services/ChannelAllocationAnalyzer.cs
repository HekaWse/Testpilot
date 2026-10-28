using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Editor.Services;

public static class ChannelAllocationAnalyzer
{
    /// <summary>
    /// Lists every channel used by a measurement or a closed relay. Reports channels measured with several functions
    /// and channel pairs that the device profile forbids to use together (for example 4-wire sense channels).
    /// Devices without profile and channels missing in the profile are listed without the pair check.
    /// </summary>
    public static IReadOnlyList<ChannelAllocation> Analyze(TestPlan plan,
        IReadOnlyDictionary<string, DeviceProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(profiles);
        var usages = new List<ChannelUsage>();
        foreach (var step in plan.Steps.Where(step => step.DeviceKey is not null))
        {
            if (step.StepType == StepType.Measure)
            {
                StepParameters.Validate(step);
                var parameters = StepParameters.Deserialize<MeasureParameters>(step.ParametersJson);
                usages.Add(new ChannelUsage(step, parameters.Channel, parameters.Function, IsMeasurement: true));
            }
            else if (step.StepType == StepType.Relay)
            {
                StepParameters.Validate(step);
                var parameters = StepParameters.Deserialize<RelayParameters>(step.ParametersJson);
                if (parameters.Closed)
                    usages.Add(new ChannelUsage(step, parameters.Channel, "Relais", IsMeasurement: false));
            }
        }

        var channels = usages
            .GroupBy(usage => (DeviceKey: usage.Step.DeviceKey!, usage.Channel))
            .OrderBy(group => group.Key.DeviceKey, StringComparer.Ordinal).ThenBy(group => group.Key.Channel)
            .Select(group => new ChannelAllocation(group.Key.DeviceKey, group.Key.Channel,
                string.Join(", ", group.Select(usage => usage.Usage).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                string.Join(", ", group.OrderBy(usage => usage.Step.Order).Select(usage => $"{usage.Step.Order + 1}: {usage.Step.Name}")),
                group.Where(usage => usage.IsMeasurement).Select(usage => usage.Usage).Distinct(StringComparer.Ordinal).Count() > 1,
                Array.Empty<int>()))
            .ToArray();

        return channels.Select(channel =>
        {
            if (!profiles.TryGetValue(channel.DeviceKey, out var profile)) return channel;
            var conflictingChannels = new List<int>();
            var channelProfile = FindChannel(profile, channel.Channel);
            foreach (var other in channels.Where(candidate => candidate.DeviceKey == channel.DeviceKey
                && candidate.Channel != channel.Channel))
            {
                var otherProfile = FindChannel(profile, other.Channel);
                if (channelProfile?.ConflictsWithChannels?.Contains(other.Channel) == true
                    || otherProfile?.ConflictsWithChannels?.Contains(channel.Channel) == true)
                    conflictingChannels.Add(other.Channel);
            }
            return channel with { ConflictingChannels = conflictingChannels };
        }).ToArray();
    }

    private static ChannelProfile? FindChannel(DeviceProfile profile, int number) =>
        profile.Channels.FirstOrDefault(channel => channel.Number == number);

    private sealed record ChannelUsage(TestStep Step, int Channel, string Usage, bool IsMeasurement);

    /// <param name="Usage">Measurement functions and "Relais" for closed relay steps, comma separated.</param>
    public sealed record ChannelAllocation(string DeviceKey, int Channel, string Usage, string Steps,
        bool FunctionConflict, IReadOnlyList<int> ConflictingChannels)
    {
        public bool HasConflict => FunctionConflict || ConflictingChannels.Count != 0;
    }
}
