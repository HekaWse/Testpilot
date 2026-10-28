using System.Text.Json;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Planning;

public sealed class ExecutionPlanOptimizer
{
    public ExecutionPlan Optimize(TestPlan plan, IReadOnlyDictionary<string, DeviceDefinition> devices)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(devices);
        if (plan.Id == Guid.Empty || plan.Version <= 0)
            throw new ArgumentException("The plan must have a valid identity and version.", nameof(plan));
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ArtNr);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Fz);
        if (plan.Steps.Count == 0)
            throw new ArgumentException("A plan must contain at least one step.", nameof(plan));

        var orderedSteps = plan.Steps.OrderBy(step => step.Order).ToArray();
        for (var index = 0; index < orderedSteps.Length; index++)
        {
            StepParameters.Validate(orderedSteps[index]);
            if (orderedSteps[index].Order != index)
                throw new ArgumentException("Step order must be contiguous and start at zero.", nameof(plan));
        }

        var usedKeys = orderedSteps.Where(step => !string.IsNullOrWhiteSpace(step.DeviceKey))
            .Select(step => step.DeviceKey!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var executionDevices = new List<ExecutionDevice>(usedKeys.Length);
        foreach (var key in usedKeys)
        {
            if (!devices.TryGetValue(key, out var definition))
                throw new ArgumentException($"No driver configuration or profile was supplied for device '{key}'.", nameof(devices));
            if (definition.Configuration is null && string.IsNullOrWhiteSpace(definition.Model))
                throw new ArgumentException($"Device '{key}' needs a driver configuration or a test system model.", nameof(devices));
            if (definition.Configuration is not null && !StringComparer.Ordinal.Equals(key, definition.Configuration.DeviceKey))
                throw new ArgumentException($"Device definition key does not match '{key}'.", nameof(devices));
            definition.Profile.Validate();
            executionDevices.Add(new(key, definition.Configuration?.Driver,
                definition.Configuration?.ConnectionParameters.GetRawText(), definition.Model));
        }
        foreach (var step in orderedSteps.Where(step => step.DeviceKey is not null))
        {
            var profile = devices[step.DeviceKey!].Profile;
            var requiredCapability = step.StepType switch
            {
                StepType.Measure or StepType.Relay => DeviceCapability.SwitchMultimeter,
                StepType.SetPower => DeviceCapability.PowerSupply,
                StepType.Program => DeviceCapability.Programmer,
                StepType.Scan => DeviceCapability.BarcodeScanner,
                _ => (DeviceCapability?)null
            };
            if (requiredCapability.HasValue && !profile.Capabilities.Contains(requiredCapability.Value))
                throw new ArgumentException($"Device '{step.DeviceKey}' does not provide capability '{requiredCapability.Value}'.");
            if (step.StepType == StepType.Relay)
                _ = profile.GetChannel(StepParameters.Deserialize<RelayParameters>(step.ParametersJson).Channel);
            if (step.StepType == StepType.SetPower)
            {
                var power = StepParameters.Deserialize<SetPowerParameters>(step.ParametersJson);
                PowerOutputLimits.Validate(profile, power.Channel, power.Voltage, power.CurrentLimit);
            }
        }

        var requestedByChannel = new Dictionary<(string Device, int Channel), HashSet<MeasurementConfiguration>>();
        var requestedForStep = new Dictionary<Guid, MeasurementConfiguration>();
        foreach (var step in orderedSteps.Where(step => step.StepType == StepType.Measure))
        {
            var key = step.DeviceKey!;
            var definition = devices[key];
            if (!definition.Profile.Capabilities.Contains(DeviceCapability.SwitchMultimeter))
                throw new ArgumentException($"Device '{key}' does not provide switch/multimeter capability.");
            var parameters = StepParameters.Deserialize<MeasureParameters>(step.ParametersJson);
            var configuration = new MeasurementConfiguration(parameters.Channel, parameters.Function, parameters.Range, parameters.Nplc);
            configuration.Validate(definition.Profile);
            requestedForStep.Add(step.Id, configuration);
            var channelKey = (key, configuration.Channel);
            if (!requestedByChannel.TryGetValue(channelKey, out var configurations))
                requestedByChannel.Add(channelKey, configurations = []);
            configurations.Add(configuration);
        }

        var initialConfigurations = requestedByChannel
            .Where(item => item.Value.Count == 1)
            .OrderBy(item => item.Key.Device, StringComparer.Ordinal).ThenBy(item => item.Key.Channel)
            .Select(item => new MeasurementConfigurationAction(item.Key.Device, item.Value.Single()))
            .ToArray();
        var knownState = new Dictionary<(string Device, int Channel), Dictionary<string, object?>>();
        foreach (var action in initialConfigurations)
        {
            InvalidateState(devices[action.DeviceKey].Profile, action.DeviceKey, ConfigureCommand, knownState,
                exceptChannel: action.Configuration.Channel);
            knownState[(action.DeviceKey, action.Configuration.Channel)] = GetSettingValues(action.Configuration);
        }

        var executionSteps = new List<ExecutionStep>(orderedSteps.Length);
        foreach (var step in orderedSteps)
        {
            MeasurementConfiguration? configure = null;
            if (step.StepType == StepType.Measure)
            {
                var desired = requestedForStep[step.Id];
                var stateKey = (step.DeviceKey!, desired.Channel);
                var profileState = GetChannelState(devices[step.DeviceKey!].Profile, desired.Channel);
                var profile = devices[step.DeviceKey!].Profile;
                if (!IsKnownAndEqual(profileState, knownState.GetValueOrDefault(stateKey), desired))
                {
                    configure = desired;
                    InvalidateState(profile, step.DeviceKey!, ConfigureCommand, knownState, exceptChannel: desired.Channel);
                    knownState[stateKey] = GetSettingValues(desired);
                }
                // Measuring one channel may change shared settings of other channels (profile decides).
                InvalidateState(profile, step.DeviceKey!, MeasureCommand, knownState, exceptChannel: desired.Channel);
            }

            executionSteps.Add(new(step.Order, step.Name, step.StepType, step.DeviceKey, step.ParametersJson,
                step.LimitLow, step.LimitHigh, step.Unit, step.BreakpointFlag, configure));
            if (step.StepType is StepType.External or StepType.CustomCode)
            {
                // Foreign programs and custom code can change any device; no profile can describe that.
                knownState.Clear();
            }
            else if (step.StepType != StepType.Measure && step.DeviceKey is not null)
            {
                InvalidateState(devices[step.DeviceKey].Profile, step.DeviceKey, CommandFor(step.StepType), knownState);
            }
        }

        // Outputs are switched off before relays open, so relays never switch while the DUT is powered.
        var safeStateOrder = usedKeys
            .OrderBy(key => SafeStateRank(devices[key].Profile))
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return new(plan.Id, plan.Version, plan.ArtNr, plan.Fz, executionDevices,
            new(initialConfigurations), executionSteps,
            new(safeStateOrder, usedKeys.Reverse().ToArray()));
    }

    /// <summary>Profile command name for configuring a measurement channel.</summary>
    public const string ConfigureCommand = "configure";

    /// <summary>Profile command name for a measurement on a channel.</summary>
    public const string MeasureCommand = "measure";

    private static int SafeStateRank(DeviceProfile profile) =>
        profile.Capabilities.Contains(DeviceCapability.PowerSupply) ? 0
        : profile.Capabilities.Contains(DeviceCapability.Programmer) ? 1
        : profile.Capabilities.Contains(DeviceCapability.SwitchMultimeter) ? 2
        : 3;

    private static ChannelStateProfile GetChannelState(DeviceProfile profile, int channel) =>
        profile.Channels.SingleOrDefault(item => item.Number == channel)?.State ?? profile.DefaultChannelState;

    private static Dictionary<string, object?> GetSettingValues(MeasurementConfiguration configuration) => new(StringComparer.Ordinal)
    {
        ["function"] = configuration.Function,
        ["range"] = configuration.Range,
        ["nplc"] = configuration.Nplc
    };

    private static bool IsKnownAndEqual(ChannelStateProfile profile, Dictionary<string, object?>? known,
        MeasurementConfiguration desired)
    {
        if (known is null)
            return false;
        // An unset range or NPLC is a value of its own (the driver selects AUTO or the device default),
        // so it is compared like any other value. A requested setting the profile cannot track forces a
        // reconfiguration; an untracked setting that is not requested is not sent anyway.
        foreach (var (setting, desiredValue) in GetSettingValues(desired))
        {
            if (!profile.Settings.Contains(setting))
            {
                if (desiredValue is null)
                    continue;
                return false;
            }
            if (!known.TryGetValue(setting, out var knownValue) || !Equals(knownValue, desiredValue))
                return false;
        }
        return true;
    }

    private static void InvalidateState(DeviceProfile profile, string deviceKey, string command,
        Dictionary<(string Device, int Channel), Dictionary<string, object?>> knownState, int? exceptChannel = null)
    {
        foreach (var (key, settings) in knownState
                     .Where(item => item.Key.Device == deviceKey && item.Key.Channel != exceptChannel).ToArray())
        {
            var channelState = GetChannelState(profile, key.Channel);
            foreach (var setting in settings.Keys.ToArray())
                if (channelState.IsInvalidatedBy(command, setting))
                    settings.Remove(setting);
        }
    }

    private static string CommandFor(StepType type) => type switch
    {
        StepType.Relay => "relay",
        StepType.SetPower => "setPower",
        StepType.Program => "program",
        StepType.Scan => "scan",
        _ => "unknown"
    };
}
