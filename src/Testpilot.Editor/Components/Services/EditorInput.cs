using System.Globalization;
using Testpilot.Core;

namespace Testpilot.Editor.Services;

/// <summary>
/// Number input of the editor. Both ',' and '.' are accepted as decimal separator; thousands separators are
/// rejected because "1.000" would be ambiguous. Numbers are displayed with the German culture.
/// </summary>
public static class EditorInput
{
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Parses a plain number such as "0,2" or "0.2"; empty input means "not set".</summary>
    public static double? ParseOptionalNumber(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = NormalizeDecimalSeparator(text, name);
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new FormatException($"{name}: '{text.Trim()}' ist keine gültige Zahl.");
        return value;
    }

    /// <summary>Parses a value with optional SI prefix and unit, e.g. "3,3 mV" or "3.3 m" for volts; returns SI base units.</summary>
    public static double? ParseOptionalSi(string? text, MeasurementUnit unit, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = NormalizeDecimalSeparator(text, name);
        try
        {
            return Units.Parse(normalized, unit, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            throw new FormatException(
                $"{name}: '{text.Trim()}' ist kein gültiger Wert in {Units.Symbol(unit)} (Beispiel: 3,3 m{Units.Symbol(unit)}).");
        }
    }

    public static string FormatNumber(double value) => value.ToString("G", German);

    private static string NormalizeDecimalSeparator(string text, string name)
    {
        var trimmed = text.Trim();
        if (trimmed.Count(character => character is ',' or '.') > 1)
            throw new FormatException(
                $"{name}: '{trimmed}' enthält mehrere Trennzeichen. Bitte nur ein Dezimaltrennzeichen und keine Tausendertrennzeichen verwenden.");
        return trimmed.Replace(',', '.');
    }
}
