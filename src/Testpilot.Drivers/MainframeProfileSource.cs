using System.Text.Json;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Profile of a mainframe driver (34980A, 34970A). Either "slots" (for example {"1":"34921A","3":"34937A"}) composes
/// the profile from the embedded module profiles of the catalog model, or "profilePath" loads a YAML file.
/// Exactly one of both must be configured.
/// </summary>
internal static class MainframeProfileSource
{
    public static DeviceProfile Load(string driver, string catalogModel, string? profilePath,
        IReadOnlyDictionary<string, string>? slots)
    {
        var hasPath = !string.IsNullOrWhiteSpace(profilePath);
        var hasSlots = slots is { Count: > 0 };
        if (hasPath == hasSlots)
            throw new JsonException($"{driver}: configure either slots or profilePath.");
        DeviceProfile profile;
        if (hasSlots)
        {
            try
            {
                profile = DeviceCatalog.CreateProfile(catalogModel, JsonSerializer.Serialize(new { slots }));
            }
            catch (ArgumentException exception)
            {
                throw new JsonException($"{driver} slots: {exception.Message}", exception);
            }
        }
        else
        {
            var path = Path.GetFullPath(profilePath!);
            if (!File.Exists(path))
                throw new FileNotFoundException($"The configured {catalogModel} module profile was not found.", path);
            profile = DeviceProfileLoader.Load(File.ReadAllText(path));
        }
        if (!profile.Capabilities.Contains(DeviceCapability.SwitchMultimeter))
            throw new InvalidDataException($"{driver}: the {catalogModel} profile does not declare the SwitchMultimeter capability.");
        if (profile.Channels.Count == 0)
            throw new InvalidDataException($"{driver}: the {catalogModel} module profile must define verified channels.");
        if (profile.SpecialCases.Concat(profile.Todos).Any(note => note.Contains(DeviceProfile.DataSheetTodo, StringComparison.Ordinal)))
            throw new InvalidDataException($"{driver}: the {catalogModel} module profile still contains unresolved data-sheet TODOs.");
        return profile;
    }
}
