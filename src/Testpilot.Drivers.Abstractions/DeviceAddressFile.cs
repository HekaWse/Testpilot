using System.Text.Json;

namespace Testpilot.Drivers.Abstractions;

/// <summary>
/// Optional <c>devices.json</c> on a test system PC that tells Testpilot under which VISA address each device is reachable:
/// <c>[{"Alias": "Ag34980A", "Resource": "TCPIP0::192.168.178.100::inst0::INSTR"}]</c>.
/// The alias is the device key of the station (case-insensitive). A listed address replaces the VISA resource stored
/// in the database on this PC only; the database is not changed.
/// </summary>
public static class DeviceAddressFile
{
    public const string DefaultFileName = "devices.json";

    public sealed record Entry(string Alias, string Resource);

    /// <summary>Reads the list; the returned dictionary compares aliases case-insensitively.</summary>
    /// <exception cref="InvalidDataException">No valid JSON list, an entry without alias or resource, or a duplicate alias.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"devices.json ist kein gültiges JSON: {exception.Message}", exception);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("devices.json muss eine Liste der Form [{\"Alias\": …, \"Resource\": …}] sein.");
            var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var number = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                number++;
                var alias = ReadString(element, "Alias");
                var resource = ReadString(element, "Resource");
                if (alias.Length == 0 || resource.Length == 0)
                    throw new InvalidDataException($"devices.json: Eintrag {number} braucht \"Alias\" und \"Resource\".");
                if (!addresses.TryAdd(alias, resource))
                    throw new InvalidDataException($"devices.json: Alias '{alias}' ist mehrfach eingetragen.");
            }
            return addresses;
        }
    }

    /// <summary>Reads the file, or returns null when it does not exist.</summary>
    /// <exception cref="InvalidDataException">The file content is invalid (message contains the path).</exception>
    public static IReadOnlyDictionary<string, string>? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return null;
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{path}: {exception.Message}", exception);
        }
    }

    public static string ToJson(IEnumerable<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return JsonSerializer.Serialize(entries.ToArray(), new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Returns the connection parameters with the VISA resource from the file when the device key is listed,
    /// otherwise unchanged. Simulated drivers are left unchanged.
    /// </summary>
    /// <param name="addresses">Result of <see cref="Parse"/> or <see cref="Load"/> (case-insensitive aliases).</param>
    /// <exception cref="InvalidOperationException">The device is listed but its driver is not addressed by a VISA resource.</exception>
    public static string Apply(IReadOnlyDictionary<string, string> addresses, string deviceKey, string driver,
        string connectionParametersJson)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (!addresses.TryGetValue(deviceKey, out var resource) || driver.StartsWith("Simulated", StringComparison.Ordinal))
            return connectionParametersJson;
        if (!ConnectionAddress.VisaDrivers.Contains(driver))
            throw new InvalidOperationException(
                $"devices.json: Gerät '{deviceKey}' nutzt Treiber '{driver}', der keine VISA-Adresse hat. Bitte den Eintrag entfernen.");
        return ConnectionAddress.SetResource(connectionParametersJson, resource);
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return "";
        foreach (var property in element.EnumerateObject())
            if (StringComparer.OrdinalIgnoreCase.Equals(property.Name, name) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString()!.Trim();
        return "";
    }
}
