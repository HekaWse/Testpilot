using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

/// <summary>
/// FSA device under test via the production DLL FSACommunication.dll (.NET Framework, class
/// FSACommunication.Communication). The DLL is loaded at runtime from <c>dllPath</c>; each operation of
/// <see cref="FsaOperations"/> is called by name. A method that returns false is repeated once, like the
/// legacy LabWindows test (_RETRY); after that the step ends with an error.
/// Connection options: <c>port</c> (e.g. "COM9", required) and <c>dllPath</c> (default D:\sys\FSACommunication.dll).
/// TODO: an echter Hardware prüfen – Laden der .NET-Framework-DLL unter .NET 10 und Rückgabe der ref-Arrays
/// (ReadInfoConfigBuffer, SendGenericTestCommand, ReceivingData).
/// </summary>
internal sealed class FsaCommunication : IDutCommunication
{
    private const string TypeName = "FSACommunication.Communication";
    private static readonly ConcurrentDictionary<string, Assembly> LoadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    private readonly FsaOptions options;
    private object? communication;

    public FsaCommunication(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options = JsonSerializer.Deserialize<FsaOptions>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("FSA options must be a JSON object.");
        if (string.IsNullOrWhiteSpace(options.Port))
            throw new JsonException("The FSA driver needs a 'port', e.g. \"COM9\".");
        if (string.IsNullOrWhiteSpace(options.DllPath))
            throw new JsonException("The FSA driver needs a 'dllPath'.");
    }

    public DeviceProfile Profile { get; } = SimulatedProfiles.Create(
        "fsa-communication", "FSA (FSACommunication.dll)", DeviceCapability.DutCommunication);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (communication is not null) throw new InvalidOperationException("The FSA device is already connected.");
        var fullPath = Path.GetFullPath(options.DllPath!);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("FSACommunication.dll was not found.", fullPath);
        var assembly = LoadedAssemblies.GetOrAdd(fullPath, Assembly.LoadFrom);
        var type = assembly.GetType(TypeName, throwOnError: true)!;
        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"{TypeName} could not be created.");
        if (!await CallWithRetryAsync(instance, "Open", [options.Port!], cancellationToken))
            throw new IOException($"The FSA device on {options.Port} could not be opened.");
        communication = instance;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var instance = communication;
        communication = null;
        if (instance is not null)
            await Task.Run(() => instance.GetType().GetMethod("Close", Type.EmptyTypes)?.Invoke(instance, null), CancellationToken.None);
    }

    // The sequencer identifies all devices before the first step, when the device under test is not yet programmed.
    // So no command is sent; the firmware version is a step of the test plan.
    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        var instance = EnsureConnected(cancellationToken);
        return Task.FromResult($"FSA,{options.Port},FSACommunication {instance.GetType().Assembly.GetName().Version}");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(new SelfCheckResult(true, $"FSA device on {options.Port} is open."));
    }

    // The device under test has no outputs that must be switched off; power and relays are handled by the station.
    public Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<DutResponse> ExecuteAsync(string operation, IReadOnlyList<double> arguments,
        CancellationToken cancellationToken = default)
    {
        var instance = EnsureConnected(cancellationToken);
        ArgumentNullException.ThrowIfNull(arguments);
        var definition = FsaOperations.GetRequired(operation, arguments.Count);
        var method = instance.GetType().GetMethods()
            .SingleOrDefault(candidate => candidate.Name == operation && candidate.GetParameters().Length
                == definition.Inputs.Count + definition.Outputs.Count)
            ?? throw new MissingMethodException(TypeName, operation);

        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        var inputIndex = 0;
        foreach (var parameter in parameters)
        {
            var type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            if (parameter.ParameterType.IsByRef)
                values[parameter.Position] = type == typeof(int[]) ? Array.Empty<int>() : Activator.CreateInstance(type);
            else
            {
                var number = FsaOperations.ToInteger(arguments[inputIndex++], operation, parameter.Name ?? "?");
                values[parameter.Position] = type == typeof(uint) ? checked((uint)number) : (object)checked((int)number);
            }
        }

        if (!await CallWithRetryAsync(instance, method, values, cancellationToken))
            return DutResponse.Failed;

        var outputs = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var parameter in parameters.Where(parameter => parameter.ParameterType.IsByRef))
        {
            outputs[parameter.Name!] = values[parameter.Position] switch
            {
                int[] array => array.Select(value => (double)value).ToArray(),
                int value => [value],
                uint value => [value],
                null => [],
                var other => throw new NotSupportedException($"FSA output '{parameter.Name}' has the unsupported type {other.GetType().Name}.")
            };
        }
        return DutResponse.Ok(outputs);
    }

    private static Task<bool> CallWithRetryAsync(object instance, string methodName, object?[] values,
        CancellationToken cancellationToken)
    {
        var method = instance.GetType().GetMethod(methodName, values.Select(value => value!.GetType()).ToArray())
            ?? throw new MissingMethodException(TypeName, methodName);
        return CallWithRetryAsync(instance, method, values, cancellationToken);
    }

    private static async Task<bool> CallWithRetryAsync(object instance, MethodInfo method, object?[] values,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The DLL blocks on the serial port; run it off the calling thread.
            var result = await Task.Run(() =>
            {
                try
                {
                    return method.Invoke(instance, values);
                }
                catch (TargetInvocationException exception) when (exception.InnerException is not null)
                {
                    throw exception.InnerException;
                }
            }, CancellationToken.None);
            if (result is true)
                return true;
        }
        return false;
    }

    private object EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return communication ?? throw new InvalidOperationException("Connect the FSA device first.");
    }

    private sealed class FsaOptions
    {
        public string? Port { get; init; }
        public string? DllPath { get; init; } = @"D:\sys\FSACommunication.dll";
    }
}

public static class FsaCommunicationRegistration
{
    public static DriverRegistry AddFsaCommunication(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("FsaCommunication", configuration => new FsaCommunication(configuration));
        return registry;
    }
}
