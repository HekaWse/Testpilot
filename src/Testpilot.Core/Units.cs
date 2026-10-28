using System.Globalization;

namespace Testpilot.Core;

public static class Units
{
    private static readonly IReadOnlyDictionary<string, double> Factors = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [""] = 1, ["n"] = 1e-9, ["µ"] = 1e-6, ["m"] = 1e-3, ["k"] = 1e3, ["M"] = 1e6
    };

    public static string Symbol(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Volt => "V",
        MeasurementUnit.Ampere => "A",
        MeasurementUnit.Ohm => "Ω",
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    /// <summary>Parses a number with an optional SI prefix and unit symbol into SI units.</summary>
    public static double Parse(string text, MeasurementUnit unit, IFormatProvider? culture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var remaining = text.Trim();
        foreach (var symbol in AcceptedSymbols(unit))
        {
            if (remaining.EndsWith(symbol, StringComparison.Ordinal))
            {
                remaining = remaining[..^symbol.Length].TrimEnd();
                break;
            }
        }

        var factor = 1d;
        if (remaining.Length > 0 && Factors.TryGetValue(NormalizePrefix(remaining[^1..]), out var prefixFactor))
        {
            factor = prefixFactor;
            remaining = remaining[..^1].TrimEnd();
        }

        // Thousands separators are deliberately rejected to avoid ambiguous measurement input.
        var value = double.Parse(remaining, NumberStyles.Float, culture ?? CultureInfo.InvariantCulture) * factor;
        if (!double.IsFinite(value))
            throw new FormatException("The value must be finite and representable in SI units.");
        return value;
    }

    // Keyboards and other programs produce different characters for micro and ohm; "u" and "Ohm" are typable on any keyboard.
    private static string NormalizePrefix(string prefix) => prefix switch
    {
        "u" or "\u03BC" => "\u00B5",
        _ => prefix
    };

    private static string[] AcceptedSymbols(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Ohm => [Symbol(unit), "\u2126", "Ohm", "ohm"],
        _ => [Symbol(unit)]
    };

    /// <summary>
    /// Formats a value for display with the SI prefix that fits its magnitude (n, µ, m, none, k, M),
    /// rounded to six significant digits, e.g. 0.0033 V -> "3,3 mV" with a German culture.
    /// </summary>
    public static string FormatAuto(double value, MeasurementUnit unit, IFormatProvider? culture = null)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "The value must be finite.");
        // Round first, so 999.9999 V is shown as "1 kV" and not as "1000 V".
        var rounded = double.Parse(value.ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var magnitude = Math.Abs(rounded);
        var prefix = magnitude == 0 ? ""
            : magnitude >= 1e6 ? "M"
            : magnitude >= 1e3 ? "k"
            : magnitude >= 1 ? ""
            : magnitude >= 1e-3 ? "m"
            : magnitude >= 1e-6 ? "µ"
            : "n";
        var scaled = rounded / Factors[prefix];
        return $"{scaled.ToString("G6", culture ?? CultureInfo.InvariantCulture)} {prefix}{Symbol(unit)}";
    }

    public static string Format(double value, MeasurementUnit unit, string prefix = "", IFormatProvider? culture = null)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "The value must be finite.");
        if (!Factors.TryGetValue(prefix, out var factor))
            throw new ArgumentException("Unsupported SI prefix.", nameof(prefix));
        var scaled = value / factor;
        if (!double.IsFinite(scaled))
            throw new ArgumentOutOfRangeException(nameof(value), "The scaled value must be finite.");
        return $"{scaled.ToString("G", culture ?? CultureInfo.InvariantCulture)} {prefix}{Symbol(unit)}";
    }
}
