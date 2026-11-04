using System.Globalization;
using System.Text.RegularExpressions;
using Testpilot.Core;

namespace Testpilot.Editor.Services;

/// <summary>Copies a test system (type, description, installed devices with driver and address) under a new name.</summary>
public static partial class StationCopy
{
    /// <summary>
    /// Proposes a free name: a number after "-", "_" or a blank at the end is counted up ("PS04-1" -> "PS04-2",
    /// skipping used names), otherwise "-2", "-3", ... is appended ("PS05" -> "PS05-2"). Names are compared case-insensitively.
    /// </summary>
    public static string NextFreeName(string sourceName, IEnumerable<string> existingNames)
    {
        var used = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var match = TrailingNumber().Match(sourceName);
        var prefix = match.Success ? match.Groups[1].Value : sourceName + "-";
        var number = match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) + 1 : 2;
        while (used.Contains(prefix + number.ToString(CultureInfo.InvariantCulture)))
            number++;
        return prefix + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Creates a new station with new ids; <paramref name="source"/> must include its devices.</summary>
    public static Station Copy(Station source, string name, Guid? testSystemTypeId)
    {
        var copy = new Station { Name = name, Description = source.Description, TestSystemTypeId = testSystemTypeId, SettingsJson = source.SettingsJson };
        foreach (var device in source.Devices.OrderBy(device => device.DeviceKey, StringComparer.Ordinal))
            copy.Devices.Add(new StationDevice
            {
                DeviceKey = device.DeviceKey,
                Driver = device.Driver,
                ConnectionParametersJson = device.ConnectionParametersJson
            });
        return copy;
    }

    [GeneratedRegex(@"^(.*[-_ ])(\d+)$")]
    private static partial Regex TrailingNumber();
}
