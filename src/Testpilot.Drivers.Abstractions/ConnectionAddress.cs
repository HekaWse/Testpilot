using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testpilot.Drivers.Abstractions;

/// <summary>
/// Reads and writes the address part of the connection parameters of a station device,
/// so that editor and operator UI can show "GPIB0::9::INSTR" or "COM3" instead of raw JSON.
/// </summary>
public static class ConnectionAddress
{
    /// <summary>Drivers that are addressed by a VISA resource string (<c>"resource"</c>).</summary>
    public static IReadOnlySet<string> VisaDrivers { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Ag34980Scpi", "Ag34980Dll", "HmpScpi", "HmpDll", "Ps05Fixture", "VisaGeneric"
    };

    /// <summary>Short address text: VISA resource, host[:port], serial port or program path; empty if none is set.</summary>
    public static string Describe(string? connectionParametersJson)
    {
        var json = TryParse(connectionParametersJson);
        if (json is null)
            return "";
        var resource = Read(json, "resource");
        if (resource.Length != 0)
            return resource;
        var host = Read(json, "host");
        if (host.Length != 0)
        {
            var port = Read(json, "port");
            return port.Length != 0 ? $"{host}:{port}" : host;
        }
        var portName = Read(json, "portName");
        return portName.Length != 0 ? portName : Read(json, "executablePath");
    }

    /// <summary>The VISA resource (<c>"resource"</c>), or an empty string.</summary>
    public static string GetResource(string? connectionParametersJson)
    {
        var json = TryParse(connectionParametersJson);
        return json is null ? "" : Read(json, "resource");
    }

    /// <summary>Returns the parameters with the VISA resource set; an empty resource removes the entry.</summary>
    /// <exception cref="JsonException">The parameters are not a JSON object.</exception>
    public static string SetResource(string? connectionParametersJson, string? resource)
    {
        var json = (string.IsNullOrWhiteSpace(connectionParametersJson) ? new JsonObject()
            : JsonNode.Parse(connectionParametersJson) as JsonObject)
            ?? throw new JsonException("Verbindungsparameter müssen ein JSON-Objekt sein.");
        foreach (var key in json.Select(property => property.Key)
                     .Where(key => StringComparer.OrdinalIgnoreCase.Equals(key, "resource")).ToArray())
            json.Remove(key);
        if (!string.IsNullOrWhiteSpace(resource))
            json["resource"] = resource.Trim();
        return json.ToJsonString();
    }

    private static JsonObject? TryParse(string? connectionParametersJson)
    {
        if (string.IsNullOrWhiteSpace(connectionParametersJson))
            return null;
        try
        {
            return JsonNode.Parse(connectionParametersJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Read(JsonObject json, string name)
    {
        foreach (var (key, value) in json)
            if (StringComparer.OrdinalIgnoreCase.Equals(key, name) && value is JsonValue scalar)
                return scalar.ToString().Trim();
        return "";
    }
}
