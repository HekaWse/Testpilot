using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Testpilot.Sap;

/// <summary>
/// Calls the existing SAP program (read-only). <c>{fauf}</c> in the arguments is replaced by the FAUF; the program
/// writes <c>{"artNr":"...","fz":"..."}</c> to stdout. Batch files are rejected because cmd.exe would interpret the FAUF.
/// </summary>
public sealed class ExternalProgramSapClient(string command, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    : ISapClient
{
    private static readonly Regex FaufPattern = new("^[0-9A-Za-z-]{1,40}$", RegexOptions.CultureInvariant);
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public async Task<OrderInfo> GetOrderAsync(string fauf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(fauf);
        cancellationToken.ThrowIfCancellationRequested();
        if (!FaufPattern.IsMatch(fauf))
            throw new SapException($"FAUF '{fauf}' has an invalid format (only letters, digits and '-', at most 40 characters).");
        var extension = Path.GetExtension(command.Trim().Trim('"'));
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            throw new SapException("Sap:Command must not be a batch file (.bat/.cmd); call the program directly.");

        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument.Replace("{fauf}", fauf, StringComparison.Ordinal));

        using var process = new Process { StartInfo = startInfo };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;
        try
        {
            if (!process.Start())
                throw new SapException("The configured SAP adapter process did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new SapException($"SAP adapter exited with code {process.ExitCode}: {stderr.Trim()}");
            var order = JsonSerializer.Deserialize<OrderInfo>(stdout, JsonOptions)
                ?? throw new JsonException("SAP adapter returned an empty order response.");
            ArgumentException.ThrowIfNullOrWhiteSpace(order.ArtNr);
            ArgumentException.ThrowIfNullOrWhiteSpace(order.Fz);
            return order;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillIfRunning(process);
            throw;
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
        {
            KillIfRunning(process);
            throw new SapException($"The SAP adapter did not answer within {timeout.TotalSeconds:0} s.", exception);
        }
        catch (SapException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SapException("The SAP adapter failed to return a valid order.", exception);
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process was never started or has just exited.
        }
    }
}
