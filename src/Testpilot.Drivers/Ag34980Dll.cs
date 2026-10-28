using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Scaffold for the Keysight IVI-C driver DLL of the 34980A (ag34980a.dll). Every call throws until the TODOs
/// below are resolved; Ag34980Scpi is the working alternative for the same device.
/// </summary>
/// <remarks>
/// Signatures taken from the legacy wrapper Externes\Geräte\Ag34980AWrapper.cs (not yet verified against the header):
/// <code>
/// [DllImport("ag34980a.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
/// int ag34980a_init(string ResourceName, bool IdQuery, bool Reset, out IntPtr Vi);
/// int ag34980a_close(IntPtr Vi);
/// int ag34980a_SystemWrite(IntPtr Vi, string CommandString);
/// int ag34980a_SystemRead(IntPtr Vi, int bufferSize, byte[] buffer, out int actualSize);
/// int ag34980a_MeasureDCVoltage(IntPtr Vi, string ChannelList, double Range, int Resolution,
///     int retValBufferSize, double[] retVal, out int retValActualSize);   // likewise AC/DC current, (F)Resistance
/// </code>
/// TODO: DLL path – legacy: UnmanagedAssemblies\Agilent\ag34980a.dll; use libraryPath with NativeLibrary.Load.
/// TODO: Architecture – check whether the DLL is 32-bit; then publish as win-x86 (see README).
/// TODO: Calling convention – StdCall/Ansi from the legacy wrapper; confirm with ag34980a.h.
/// TODO: Signatures – bool parameters are ViBoolean (16-bit) in IVI-C; confirm marshalling ([MarshalAs(UnmanagedType.I2)]?).
/// TODO: Error handling – map negative ViStatus values with ag34980a_error_message (signature still to be checked).
/// TODO: Resource – the configured 'resource' is the VISA resource passed to ag34980a_init.
/// </remarks>
internal sealed class Ag34980Dll : ISwitchMultimeter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly Ag34980DllOptions options;

    public Ag34980Dll(DriverConfiguration configuration)
    {
        options = ParseOptions(configuration);
        if (!File.Exists(options.ProfilePath))
            throw new FileNotFoundException("The configured 34980A module profile was not found.", options.ProfilePath);
        Profile = DeviceProfileLoader.Load(File.ReadAllText(options.ProfilePath));
        if (!Profile.Capabilities.Contains(DeviceCapability.SwitchMultimeter) || Profile.Channels.Count == 0)
            throw new InvalidDataException("Ag34980Dll requires a channel-complete SwitchMultimeter profile.");
        if (Profile.SpecialCases.Concat(Profile.Todos).Any(note => note.Contains(DeviceProfile.DataSheetTodo, StringComparison.Ordinal)))
            throw new InvalidDataException("Ag34980Dll cannot use a profile with unresolved data-sheet TODOs.");
    }

    public DeviceProfile Profile { get; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw Unsupported();
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task SafeStateAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task ConfigureMeasurementAsync(MeasurementConfiguration configuration,
        CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<double> MeasureAsync(int channel, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task SetRelayAsync(int channel, bool closed, CancellationToken cancellationToken = default) => throw Unsupported();

    private NotSupportedException Unsupported() => new NativeDriverScaffoldException("Ag34980Dll", options.LibraryPath);

    private static Ag34980DllOptions ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<Ag34980DllOptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("Ag34980Dll connection parameters must be a JSON object.");
        if (string.IsNullOrWhiteSpace(input.LibraryPath) || string.IsNullOrWhiteSpace(input.Resource)
            || string.IsNullOrWhiteSpace(input.ProfilePath))
            throw new JsonException("Ag34980Dll libraryPath, resource, and profilePath are required.");
        return new Ag34980DllOptions(Path.GetFullPath(input.LibraryPath), input.Resource,
            Path.GetFullPath(input.ProfilePath));
    }

    private sealed class Ag34980DllOptionsInput
    {
        public string LibraryPath { get; init; } = "";
        public string Resource { get; init; } = "";
        public string ProfilePath { get; init; } = "";
    }

    private sealed record Ag34980DllOptions(string LibraryPath, string Resource, string ProfilePath);
}

public static class Ag34980DllRegistration
{
    public static DriverRegistry AddAg34980Dll(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("Ag34980Dll", configuration => new Ag34980Dll(configuration));
        return registry;
    }
}
