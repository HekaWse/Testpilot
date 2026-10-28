using Testpilot.Core;

namespace Testpilot.Editor.Services;

/// <summary>Decides whether a device of a test system type may be changed, depending on where it is used.</summary>
public static class DeviceEditRules
{
    /// <summary>Where a device key of a test system type is used: plans of the type and stations of the type.</summary>
    public sealed record DeviceUsage(IReadOnlyList<string> ReleasedPlans, IReadOnlyList<string> OtherPlans,
        IReadOnlyList<string> Stations)
    {
        public static readonly DeviceUsage None = new([], [], []);
        public bool IsUsed => ReleasedPlans.Count + OtherPlans.Count + Stations.Count > 0;

        public override string ToString()
        {
            var parts = new List<string>();
            if (ReleasedPlans.Count + OtherPlans.Count > 0)
                parts.Add("Prüfpläne " + string.Join(", ", ReleasedPlans.Concat(OtherPlans)));
            if (Stations.Count > 0)
                parts.Add("Prüfsysteme " + string.Join(", ", Stations));
            return string.Join("; ", parts);
        }
    }

    /// <summary>Error blocks the change; warning allows it but should be shown to the user.</summary>
    public sealed record DeviceEditCheck(string? Error, string? Warning);

    /// <param name="current">Stored device; <paramref name="currentOptionsJson"/> are its options in normalized form.</param>
    public static DeviceEditCheck Check(SystemTypeDevice current, string currentOptionsJson, string newDeviceKey,
        string newModel, string newOptionsJson, DeviceUsage usage)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(usage);
        var keyChanged = !StringComparer.Ordinal.Equals(current.DeviceKey, newDeviceKey);
        var hardwareChanged = !StringComparer.Ordinal.Equals(current.Model, newModel)
            || !StringComparer.Ordinal.Equals(currentOptionsJson, newOptionsJson);

        if (keyChanged && usage.IsUsed)
            return new DeviceEditCheck(
                $"Die Gerätekennung '{current.DeviceKey}' kann nicht geändert werden, weil Prüfpläne oder Prüfsysteme sie verwenden ({usage}).",
                null);
        if (hardwareChanged && usage.ReleasedPlans.Count > 0)
            return new DeviceEditCheck(
                $"Modell und Ausstattung von '{current.DeviceKey}' können nicht geändert werden, weil freigegebene Prüfpläne das Gerät verwenden: "
                + $"{string.Join(", ", usage.ReleasedPlans)}. Ziehe diese Pläne zuerst zurück.",
                null);
        if (hardwareChanged && usage.IsUsed)
            return new DeviceEditCheck(null,
                $"Gerät '{current.DeviceKey}' wurde geändert und wird verwendet ({usage}). Bitte Kanäle der Prüfschritte sowie Treiber und Adressen der Prüfsysteme prüfen.");
        return new DeviceEditCheck(null, null);
    }
}
