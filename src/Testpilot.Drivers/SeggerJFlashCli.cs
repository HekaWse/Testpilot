using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testpilot.Drivers.Abstractions;

namespace Testpilot.Drivers;

internal sealed class SeggerJFlashCli(DriverConfiguration configuration) : IProgrammer
{
    private const string ImagePathToken = "{{imagePath}}";
    private const int MaximumOutputCharacters = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    private readonly SeggerOptions options = ParseOptions(configuration);
    private bool connected;

    public DeviceProfile Profile { get; } = SimulatedProfiles.Create(
        "segger-jflash-cli", "SEGGER J-Flash command-line programmer", DeviceCapability.Programmer);

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connected) throw new InvalidOperationException("The programmer is already connected.");
        if (!File.Exists(options.ExecutablePath))
            throw new FileNotFoundException("The configured J-Flash executable was not found.", options.ExecutablePath);
        if (!Directory.Exists(options.WorkingDirectory))
            throw new DirectoryNotFoundException($"The configured J-Flash working directory '{options.WorkingDirectory}' does not exist.");
        connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connected = false;
        return Task.CompletedTask;
    }

    public Task<string> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult($"SEGGER J-Flash CLI ({options.ExecutablePath})");
    }

    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        return Task.FromResult(new SelfCheckResult(File.Exists(options.ExecutablePath),
            File.Exists(options.ExecutablePath) ? "Configured J-Flash executable is available." : "Configured J-Flash executable is missing."));
    }

    public Task SafeStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task ProgramAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fullImagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullImagePath))
            throw new FileNotFoundException("The firmware image was not found.", fullImagePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in options.Arguments)
            startInfo.ArgumentList.Add(argument.Replace(ImagePathToken, fullImagePath, StringComparison.Ordinal));

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("The J-Flash process could not be started.");

        var standardOutputTask = ReadBoundedOutputAsync(process.StandardOutput);
        var standardErrorTask = ReadBoundedOutputAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAsync(process);
            var output = await Task.WhenAll(standardOutputTask, standardErrorTask);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"J-Flash did not finish within {options.TimeoutSeconds} seconds. {FormatOutput(output[0], output[1])}");
        }

        var results = await Task.WhenAll(standardOutputTask, standardErrorTask);
        if (process.ExitCode != 0)
            throw new SeggerJFlashProcessException(process.ExitCode, results[0], results[1]);
    }

    private static SeggerOptions ParseOptions(DriverConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = JsonSerializer.Deserialize<SeggerOptionsInput>(configuration.ConnectionParameters, JsonOptions)
            ?? throw new JsonException("J-Flash connection parameters must be a JSON object.");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        if (options.Arguments is null || options.Arguments.Length == 0
            || options.Arguments.Any(argument => argument is null)
            || !options.Arguments.Any(argument => argument.Contains(ImagePathToken, StringComparison.Ordinal)))
            throw new JsonException($"J-Flash arguments must be a non-empty array containing the {ImagePathToken} token.");
        if (options.TimeoutSeconds is < 1 or > 86_400)
            throw new JsonException("J-Flash timeoutSeconds must be between 1 and 86400.");

        var executable = Path.GetFullPath(options.ExecutablePath);
        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            : Path.GetFullPath(options.WorkingDirectory);
        return new SeggerOptions(executable, options.Arguments, workingDirectory, options.TimeoutSeconds);
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            var appendLength = Math.Min(read, MaximumOutputCharacters - output.Length);
            if (appendLength > 0) output.Append(buffer, 0, appendLength);
        }
        return output.ToString();
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        await process.WaitForExitAsync();
    }

    private static string FormatOutput(string standardOutput, string standardError) =>
        $"stdout: {standardOutput.Trim()} stderr: {standardError.Trim()}";

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected) throw new InvalidOperationException("Connect the programmer first.");
    }

    private sealed class SeggerOptionsInput
    {
        public string ExecutablePath { get; init; } = "";
        public string? WorkingDirectory { get; init; }
        public string[] Arguments { get; init; } = [];
        public int TimeoutSeconds { get; init; } = 600;
    }

    private sealed record SeggerOptions(string ExecutablePath, string[] Arguments, string WorkingDirectory, int TimeoutSeconds);
}

public sealed class SeggerJFlashProcessException(int exitCode, string standardOutput, string standardError)
    : InvalidOperationException($"J-Flash exited with code {exitCode}. stdout: {standardOutput.Trim()} stderr: {standardError.Trim()}")
{
    public int ExitCode { get; } = exitCode;
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
}

public static class SeggerJFlashRegistration
{
    public static DriverRegistry AddSeggerJFlashCli(this DriverRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register("SeggerJFlashCli", configuration => new SeggerJFlashCli(configuration));
        return registry;
    }
}
