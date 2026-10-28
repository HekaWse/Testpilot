namespace Testpilot.Drivers.Abstractions;

public enum DeviceCapability
{
    SwitchMultimeter,
    PowerSupply,
    Programmer,
    BarcodeScanner,
    TestFixture
}

public enum ChannelType
{
    Unknown,
    GeneralPurpose,
    Voltage,
    Current,
    Resistance,
    Relay,
    PowerOutput
}

/// <summary>Inclusive SI bounds for one supported measurement function.</summary>
public sealed record NumericRange(double Minimum, double Maximum)
{
    public void Validate()
    {
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum) || Minimum > Maximum)
            throw new ArgumentException("A device range must contain finite values with minimum <= maximum.");
    }
}

public sealed record ChannelStateProfile(
    IReadOnlySet<string> Settings,
    IReadOnlyDictionary<string, IReadOnlySet<string>> InvalidatedByCommand,
    bool UnknownCommandInvalidatesAll)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Settings);
        ArgumentNullException.ThrowIfNull(InvalidatedByCommand);
        foreach (var setting in Settings)
            ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        foreach (var (command, invalidatedSettings) in InvalidatedByCommand)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ArgumentNullException.ThrowIfNull(invalidatedSettings);
            foreach (var setting in invalidatedSettings)
                ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        }
    }

    public bool IsInvalidatedBy(string command, string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        if (!Settings.Contains(setting))
            return true;
        if (!InvalidatedByCommand.TryGetValue(command, out var invalidated)
            && !InvalidatedByCommand.TryGetValue("*", out invalidated))
            return UnknownCommandInvalidatesAll;
        return invalidated.Contains("*") || invalidated.Contains(setting);
    }
}

public sealed record ChannelProfile(
    int Number,
    string Name,
    ChannelType Type,
    IReadOnlySet<string> SupportedFunctions,
    IReadOnlyDictionary<string, NumericRange> ValueRanges,
    ChannelStateProfile State,
    IReadOnlySet<int>? ConflictsWithChannels = null)
{
    public void Validate()
    {
        if (Number <= 0)
            throw new ArgumentOutOfRangeException(nameof(Number), "A channel number must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (!Enum.IsDefined(Type))
            throw new ArgumentException("A channel has an unknown channel type.");
        ArgumentNullException.ThrowIfNull(SupportedFunctions);
        ArgumentNullException.ThrowIfNull(ValueRanges);
        ArgumentNullException.ThrowIfNull(State);
        State.Validate();
        foreach (var function in SupportedFunctions)
            ArgumentException.ThrowIfNullOrWhiteSpace(function);
        foreach (var (function, range) in ValueRanges)
        {
            if (!SupportedFunctions.Contains(function))
                throw new ArgumentException($"A value range is defined for unsupported function '{function}'.");
            ArgumentNullException.ThrowIfNull(range);
            range.Validate();
        }
        if (ConflictsWithChannels?.Any(channel => channel <= 0 || channel == Number) == true)
            throw new ArgumentException("Conflicting channel numbers must be positive and cannot reference the channel itself.");
    }
}

/// <summary>Device configuration and conservative per-channel state metadata.</summary>
public sealed record DeviceProfile(
    string Id,
    string Model,
    int? ChannelCount,
    string ChannelNumbering,
    IReadOnlySet<DeviceCapability> Capabilities,
    ChannelStateProfile DefaultChannelState,
    IReadOnlyList<ChannelProfile> Channels,
    IReadOnlyList<string> SpecialCases,
    IReadOnlyList<string> Todos)
{
    public const string DataSheetTodo = "TODO: aus Datenblatt prüfen";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(ChannelNumbering);
        ArgumentNullException.ThrowIfNull(Capabilities);
        ArgumentNullException.ThrowIfNull(DefaultChannelState);
        ArgumentNullException.ThrowIfNull(Channels);
        ArgumentNullException.ThrowIfNull(SpecialCases);
        ArgumentNullException.ThrowIfNull(Todos);
        foreach (var specialCase in SpecialCases)
            ArgumentException.ThrowIfNullOrWhiteSpace(specialCase);
        foreach (var todo in Todos)
            ArgumentException.ThrowIfNullOrWhiteSpace(todo);
        DefaultChannelState.Validate();
        if (ChannelCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(ChannelCount), "A known channel count must be positive.");
        if (ChannelCount.HasValue && Channels.Count != ChannelCount.Value)
            throw new ArgumentException("A fully enumerated profile must declare every channel.");
        if (!ChannelCount.HasValue && !Todos.Any(todo => todo.Contains(DataSheetTodo, StringComparison.Ordinal)))
            throw new ArgumentException("An unknown channel count must be explicitly marked for data-sheet verification.");
        if (Capabilities.Any(capability => !Enum.IsDefined(capability)))
            throw new ArgumentException("A device profile contains an unknown capability.");
        if (Channels.Select(channel => channel.Number).Distinct().Count() != Channels.Count)
            throw new ArgumentException("Channel numbers must be unique.");
        foreach (var channel in Channels)
        {
            channel.Validate();
            foreach (var conflictingChannel in channel.ConflictsWithChannels ?? new HashSet<int>())
                if (!Channels.Any(candidate => candidate.Number == conflictingChannel))
                    throw new ArgumentException($"Channel {channel.Number} conflicts with undefined channel {conflictingChannel}.");
        }
    }

    public ChannelProfile GetChannel(int number) => Channels.SingleOrDefault(channel => channel.Number == number)
        ?? throw new ArgumentOutOfRangeException(nameof(number), $"Channel {number} is not defined by profile '{Id}'.");
}
