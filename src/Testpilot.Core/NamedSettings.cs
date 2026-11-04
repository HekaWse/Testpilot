using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Testpilot.Core;

public enum SettingDataType { String, Boolean, Integer, Number }

/// <summary>A named plan or station setting. Directory and name are display labels; the alias is its stable key.</summary>
public sealed record NamedSetting(string Directory, string Name, string Alias, SettingDataType DataType, string Value);

public static partial class NamedSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static IReadOnlyList<NamedSetting> Deserialize(string json)
    {
        var settings = JsonSerializer.Deserialize<NamedSetting[]>(json, Options)
            ?? throw new ArgumentException("Die Einstellungsliste darf nicht null sein.");
        Validate(settings);
        return settings;
    }

    public static string Serialize(IEnumerable<NamedSetting> settings)
    {
        var items = settings.ToArray();
        Validate(items);
        return JsonSerializer.Serialize(items, Options);
    }

    public static void Validate(IEnumerable<NamedSetting> settings)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            ArgumentNullException.ThrowIfNull(setting);
            if (string.IsNullOrWhiteSpace(setting.Alias) || !AliasPattern().IsMatch(setting.Alias))
                throw new ArgumentException("Ein Alias muss mit einem Buchstaben oder Unterstrich beginnen und darf nur A-Z, a-z, 0-9 und Unterstriche enthalten (maximal 100 Zeichen).");
            if (!aliases.Add(setting.Alias))
                throw new ArgumentException($"Einstellungsalias '{setting.Alias}' ist mehrfach vorhanden.");
            if (string.IsNullOrWhiteSpace(setting.Name) || setting.Name.Length > 200
                || setting.Directory is null || setting.Directory.Length > 500 || setting.Value is null)
                throw new ArgumentException($"Einstellung '{setting.Alias}': Name (max. 200 Zeichen), Verzeichnis (max. 500 Zeichen) und Wert sind erforderlich.");
            if (setting.Value.Contains("{{plan", StringComparison.OrdinalIgnoreCase)
                || setting.Value.Contains("{{station", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Einstellung '{setting.Alias}': Werte dürfen keine weiteren Einstellungsreferenzen enthalten.");
            _ = ToNode(setting);
        }
    }

    internal static JsonNode ToNode(NamedSetting setting) => setting.DataType switch
    {
        SettingDataType.String => JsonValue.Create(setting.Value)!,
        SettingDataType.Boolean when bool.TryParse(setting.Value, out var value) => JsonValue.Create(value)!,
        SettingDataType.Integer when long.TryParse(setting.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) => JsonValue.Create(value)!,
        SettingDataType.Number when double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) => JsonValue.Create(value)!,
        _ => throw new ArgumentException($"Einstellung '{setting.Alias}': Wert '{setting.Value}' passt nicht zum Datentyp {setting.DataType} (Boolean: true/false, Zahlen mit Dezimalpunkt).")
    };

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,99}\z")]
    private static partial Regex AliasPattern();
}

/// <summary>
/// Resolves {{plan.Alias}} and {{station.Alias}} in JSON string values without changing JSON keys or interpreting values
/// recursively. An entire reference preserves its declared type; a reference embedded in text is formatted invariantly.
/// </summary>
public sealed partial class NamedSettingsResolver
{
    private readonly Dictionary<string, NamedSetting> plan;
    private readonly Dictionary<string, NamedSetting> station;

    public NamedSettingsResolver(IEnumerable<NamedSetting> plan, IEnumerable<NamedSetting> station)
    {
        NamedSettings.Validate(plan);
        NamedSettings.Validate(station);
        this.plan = plan.ToDictionary(setting => setting.Alias, StringComparer.OrdinalIgnoreCase);
        this.station = station.ToDictionary(setting => setting.Alias, StringComparer.OrdinalIgnoreCase);
    }

    public string ResolveJson(string json, bool deferStation = false, bool deferPlan = false)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("Ein JSON-Objekt ist erforderlich.");
        if (node is not JsonObject) throw new JsonException("Ein JSON-Objekt ist erforderlich.");
        Visit(node);
        return node.ToJsonString();

        void Visit(JsonNode current)
        {
            if (current is JsonObject obj)
                foreach (var key in obj.Select(property => property.Key).ToArray())
                    obj[key] = Replace(obj[key]);
            else if (current is JsonArray array)
                for (var index = 0; index < array.Count; index++)
                    array[index] = Replace(array[index]);
        }

        JsonNode? Replace(JsonNode? value)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
            {
                if (value is not null) Visit(value);
                return value?.DeepClone();
            }
            var matches = ReferencePattern().Matches(text);
            var remaining = ReferencePattern().Replace(text, "");
            if (remaining.Contains("{{plan", StringComparison.OrdinalIgnoreCase)
                || remaining.Contains("{{station", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Ungültige Einstellungsreferenz in '{text}'. Erwartet: {{{{plan.Alias}}}} oder {{{{station.Alias}}}}.");
            if (matches.Count == 1 && matches[0].Length == text.Length)
                return Lookup(matches[0]) ?? JsonValue.Create(text);
            return JsonValue.Create(ReferencePattern().Replace(text, match =>
            {
                var replacement = Lookup(match);
                return replacement is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue)
                    ? stringValue : replacement?.ToJsonString() ?? match.Value;
            }));
        }

        JsonNode? Lookup(Match match)
        {
            var isPlan = match.Groups[1].Value == "plan";
            if (isPlan ? deferPlan : deferStation) return null;
            var source = isPlan ? plan : station;
            var alias = match.Groups[2].Value;
            if (!source.TryGetValue(alias, out var setting))
                throw new ArgumentException($"Einstellung '{match.Value}' fehlt im {(isPlan ? "Prüfplan" : "Prüfsystem")}.");
            return NamedSettings.ToNode(setting);
        }
    }

    [GeneratedRegex(@"\{\{(plan|station)\.([A-Za-z_][A-Za-z0-9_]{0,99})\}\}")]
    private static partial Regex ReferencePattern();
}
