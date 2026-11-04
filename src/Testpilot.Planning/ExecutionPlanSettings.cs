using Testpilot.Core;

namespace Testpilot.Planning;

/// <summary>Resolves settings before execution, never while a device operation is in progress.</summary>
public static class ExecutionPlanSettings
{
    public static ExecutionPlan Resolve(ExecutionPlan plan, IReadOnlyList<NamedSetting> stationSettings)
    {
        var resolver = new NamedSettingsResolver(plan.Settings, stationSettings);
        var resolved = plan with
        {
            Devices = plan.Devices.Select(device => device.ConnectionParametersJson is null ? device : device with
            {
                ConnectionParametersJson = resolver.ResolveJson(device.ConnectionParametersJson)
            }).ToArray(),
            Steps = plan.Steps.Select(step => step with { ParametersJson = resolver.ResolveJson(step.ParametersJson) }).ToArray()
        };
        _ = ExecutionPlanSerializer.Serialize(resolved);
        return resolved;
    }
}
