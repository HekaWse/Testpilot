using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Testpilot.Drivers.Abstractions;

public static class DeviceProfileLoader
{
    private const int MaximumProfileBytes = 1_048_576;
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    public static async Task<DeviceProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Device profile was not found.", path);
        if (info.Length > MaximumProfileBytes)
            throw new InvalidDataException($"Device profile exceeds {MaximumProfileBytes} bytes.");
        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        return Load(yaml);
    }

    public static DeviceProfile Load(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaximumProfileBytes)
            throw new InvalidDataException($"Device profile exceeds {MaximumProfileBytes} bytes.");

        var document = Deserializer.Deserialize<DeviceProfileDocument>(yaml)
            ?? throw new InvalidDataException("Device profile YAML must contain a mapping.");
        var profile = document.ToProfile();
        profile.Validate();
        return profile;
    }

    private sealed class DeviceProfileDocument
    {
        public string Id { get; set; } = "";
        public string Model { get; set; } = "";
        public int? ChannelCount { get; set; }
        public string ChannelNumbering { get; set; } = "";
        public List<string> Capabilities { get; set; } = [];
        public ChannelStateDocument State { get; set; } = new();
        public List<ChannelDocument> Channels { get; set; } = [];
        public List<string> SpecialCases { get; set; } = [];
        public List<string> Todos { get; set; } = [];

        public DeviceProfile ToProfile()
        {
            ArgumentNullException.ThrowIfNull(Capabilities);
            ArgumentNullException.ThrowIfNull(State);
            ArgumentNullException.ThrowIfNull(Channels);
            ArgumentNullException.ThrowIfNull(SpecialCases);
            ArgumentNullException.ThrowIfNull(Todos);
            var capabilities = new HashSet<DeviceCapability>();
            foreach (var capability in Capabilities)
            {
                if (!Enum.TryParse<DeviceCapability>(capability, ignoreCase: false, out var parsed)
                    || !Enum.IsDefined(parsed))
                    throw new InvalidDataException($"Unknown device capability '{capability}'.");
                if (!capabilities.Add(parsed))
                    throw new InvalidDataException($"Duplicate device capability '{capability}'.");
            }

            var defaultState = State.ToProfile();
            return new DeviceProfile(Id, Model, ChannelCount, ChannelNumbering, capabilities,
                defaultState, Channels.Select(channel => channel is null
                    ? throw new InvalidDataException("Device profile channels must not contain null entries.")
                    : channel.ToProfile(defaultState)).ToArray(),
                SpecialCases.ToArray(), Todos.ToArray());
        }
    }

    private sealed class ChannelDocument
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public ChannelType Type { get; set; }
        public List<string> SupportedFunctions { get; set; } = [];
        public Dictionary<string, double[]> ValueRanges { get; set; } = new(StringComparer.Ordinal);
        public List<int> ConflictsWithChannels { get; set; } = [];
        public ChannelStateDocument? State { get; set; }

        public ChannelProfile ToProfile(ChannelStateProfile defaultState)
        {
            ArgumentNullException.ThrowIfNull(SupportedFunctions);
            ArgumentNullException.ThrowIfNull(ValueRanges);
            ArgumentNullException.ThrowIfNull(ConflictsWithChannels);
            var ranges = new Dictionary<string, NumericRange>(StringComparer.Ordinal);
            foreach (var (function, values) in ValueRanges)
            {
                ArgumentNullException.ThrowIfNull(values);
                if (values.Length != 2)
                    throw new InvalidDataException($"Range for '{function}' must contain exactly a minimum and maximum.");
                ranges.Add(function, new NumericRange(values[0], values[1]));
            }

            return new ChannelProfile(Number, Name, Type, new HashSet<string>(SupportedFunctions, StringComparer.Ordinal),
                ranges, State?.ToProfile() ?? defaultState, new HashSet<int>(ConflictsWithChannels));
        }
    }

    private sealed class ChannelStateDocument
    {
        public List<string> Settings { get; set; } = [];
        public Dictionary<string, List<string>> InvalidatedByCommand { get; set; } = new(StringComparer.Ordinal);
        public bool UnknownCommandInvalidatesAll { get; set; } = true;

        public ChannelStateProfile ToProfile()
        {
            ArgumentNullException.ThrowIfNull(Settings);
            ArgumentNullException.ThrowIfNull(InvalidatedByCommand);
            return new ChannelStateProfile(
                new HashSet<string>(Settings, StringComparer.Ordinal),
                InvalidatedByCommand.ToDictionary(
                    item => item.Key,
                    item => (IReadOnlySet<string>)(item.Value is null
                        ? throw new InvalidDataException($"Invalidated settings for command '{item.Key}' must be a list.")
                        : new HashSet<string>(item.Value, StringComparer.Ordinal)),
                    StringComparer.Ordinal),
                UnknownCommandInvalidatesAll);
        }
    }
}
