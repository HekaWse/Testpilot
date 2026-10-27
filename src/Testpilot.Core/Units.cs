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
        var symbol = Symbol(unit);
        var remaining = text.Trim();
        if (remaining.EndsWith(symbol, StringComparison.Ordinal))
            remaining = remaining[..^symbol.Length].TrimEnd();

        var factor = 1d;
        if (remaining.Length > 0 && Factors.TryGetValue(remaining[^1..], out var prefixFactor))
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
