using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// Scaffold for the existing HMP C DLL (hmp.dll). Every call throws until the TODOs below are resolved.
/// </summary>
/// <remarks>
/// Signatures taken from the legacy wrapper Externes\Geräte\HmpWrapper.cs (not yet verified against the DLL header):
/// <code>
/// [DllImport("hmp.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
/// int hmp_init(string psu, out uint taskHandle, out IntPtr retmsg);
/// int hmp_close(uint taskHandle, out IntPtr retmsg);
/// int hmp_Config(uint taskHandle, uint channel, double voltage, double current, out IntPtr retmsg);
/// int hmp_enableOutput(uint taskHandle, uint channel, out IntPtr retmsg);
/// int hmp_disableOutput(uint taskHandle, uint channel, out IntPtr retmsg);
/// int hmp_measureCurrent(uint taskHandle, uint channel, out double measuredval, out IntPtr retmsg);
/// int hmp_measureVoltage(uint taskHandle, uint channel, out double measuredval, out IntPtr retmsg);
/// </code>
/// TODO: DLL path – the legacy code loads UnmanagedAssemblies\HMP\hmp.dll relative to the process; use libraryPath
///       with NativeLibrary.Load / NativeLibrary.SetDllImportResolver instead of a constant.
/// TODO: Architecture – check whether hmp.dll is 32-bit; then the host must be published as win-x86 (see README).
/// TODO: Calling convention – StdCall/Ansi from the legacy wrapper; confirm with the C header (hmp.h).
/// TODO: retmsg – who owns and frees the returned string (static buffer or allocated)? Marshal.PtrToStringAnsi only.
/// TODO: Return codes – map non-zero results to exceptions with the retmsg text.
/// TODO: Safe state – no "all outputs off" function known; call hmp_disableOutput for every channel.
/// </remarks>
internal sealed class HmpDll(DriverConfiguration configuration) : IPowerSupply
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly HmpDllOptions options = ParseOptions(configuration);

    private DeviceProfile? profile;

    public DeviceProfile Profile => profile ??= EmbeddedProfiles.Load(options.Model + ".yaml");

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NativeDriverScaffoldException("HmpDll", options.LibraryPath);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task SafeStateAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task SetOutputAsync(int channel, double voltage, double currentLimit, bool enabled,
        CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<(double Voltage, double CurrentLimit, bool Enabled)> GetOutputAsync(int channel,
        CancellationToken cancellationToken = default) => throw Unsupported();

    private NotSupportedException Unsupported() => new NativeDriverScaffoldException("HmpDll", options.LibraryPath);

    private static HmpDllOptions ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var input = JsonSerializer.Deserialize<HmpDllOptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("HmpDll connection parameters must be a JSON object.");
        if (string.IsNullOrWhiteSpace(input.LibraryPath) || string.IsNullOrWhiteSpace(input.Resource))
            throw new JsonException("HmpDll libraryPath and resource are required.");
        return new HmpDllOptions(Path.GetFullPath(input.LibraryPath), input.Resource, HmpModels.Parse(input.Model));
    }

    private sealed class HmpDllOptionsInput
    {
        public string LibraryPath { get; init; } = "";
        public string Resource { get; init; } = "";
        public string Model { get; init; } = "";
    }

    private sealed record HmpDllOptions(string LibraryPath, string Resource, string Model);
}

public static class HmpDllRegistration
{
    public static DriverRegistry AddHmpDll(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("HmpDll", configuration => new HmpDll(configuration));
        return registry;
    }
}
