namespace Testpilot.Drivers.Abstractions;

/// <summary>
/// Device and module profiles shipped as embedded YAML files (folder Profiles).
/// They live here so that the editor can read them without referencing the hardware drivers.
/// </summary>
public static class EmbeddedProfiles
{
    private const string ResourcePrefix = "Testpilot.Drivers.Abstractions.Profiles.";

    /// <summary>File names of all embedded profiles, for example "HMP4040.yaml".</summary>
    public static IReadOnlyList<string> FileNames { get; } = typeof(EmbeddedProfiles).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
            && name.EndsWith(".yaml", StringComparison.Ordinal))
        .Select(name => name[ResourcePrefix.Length..])
        .Order(StringComparer.Ordinal)
        .ToArray();

    public static DeviceProfile Load(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return DeviceProfileLoader.Load(ReadText(fileName));
    }

    public static string ReadText(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        using var stream = typeof(EmbeddedProfiles).Assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new FileNotFoundException($"Embedded device profile '{fileName}' was not found.", fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
