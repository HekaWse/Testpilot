using System.Globalization;
using Testpilot.Core;

namespace Testpilot.Tests;

public sealed class UnitsAndLimitsTests
{
    [Theory]
    [InlineData("3.3 m", 0.0033)]
    [InlineData("3.3mV", 0.0033)]
    [InlineData("2 µV", 0.000002)]
    [InlineData("2 nV", 0.000000002)]
    [InlineData("4 kV", 4000)]
    [InlineData("4 MV", 4000000)]
    [InlineData("-1.2e2 V", -120)]
    [InlineData("0", 0)]
    public void ParseConvertsPrefixesToSi(string input, double expected) =>
        Assert.Equal(expected, Units.Parse(input, MeasurementUnit.Volt), 12);

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1 A")]
    [InlineData("1 K")]
    [InlineData("1 u")]
    [InlineData("1,234")]
    [InlineData("3.3 mm")]
    public void ParseRejectsInvalidOrAmbiguousValues(string input) =>
        Assert.Throws<FormatException>(() => Units.Parse(input, MeasurementUnit.Volt));

    [Theory]
    [InlineData(MeasurementUnit.Volt, "V")]
    [InlineData(MeasurementUnit.Ampere, "A")]
    [InlineData(MeasurementUnit.Ohm, "Ω")]
    public void UnitsUseCorrectSymbols(MeasurementUnit unit, string symbol)
    {
        Assert.Equal(symbol, Units.Symbol(unit));
        Assert.Equal(2, Units.Parse($"2 {symbol}", unit));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("µ")]
    [InlineData("m")]
    [InlineData("")]
    [InlineData("k")]
    [InlineData("M")]
    public void FormatRoundTripsAllPrefixes(string prefix)
    {
        var formatted = Units.Format(0.0033, MeasurementUnit.Volt, prefix);
        Assert.EndsWith($" {prefix}V", formatted);
        Assert.Equal(0.0033, Units.Parse(formatted, MeasurementUnit.Volt), 12);
    }

    [Fact]
    public void FormattingIsReadableWithoutFloatingPointArtifacts()
    {
        Assert.Equal("3.3 mV", Units.Format(0.0033, MeasurementUnit.Volt, "m"));
        Assert.Equal("3,3 mV", Units.Format(0.0033, MeasurementUnit.Volt, "m", CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void GermanInputUsesExplicitCulture()
    {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal(0.0033, Units.Parse("3,3 mV", MeasurementUnit.Volt, culture), 12);
        Assert.Equal("1,5 mV", Units.Format(0.0015, MeasurementUnit.Volt, "m", culture));
    }

    [Fact]
    public void FormatRejectsInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Units.Format(double.NaN, MeasurementUnit.Volt));
        Assert.Throws<ArgumentException>(() => Units.Format(1, MeasurementUnit.Volt, "K"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Units.Format(double.MaxValue, MeasurementUnit.Volt, "n"));
    }

    [Theory]
    [InlineData(3.2, 3.2, 3.4, StepOutcome.Pass)]
    [InlineData(3.4, 3.2, 3.4, StepOutcome.Pass)]
    [InlineData(3.3, 3.2, 3.4, StepOutcome.Pass)]
    [InlineData(3.1999999, 3.2, 3.4, StepOutcome.Fail)]
    [InlineData(3.4000001, 3.2, 3.4, StepOutcome.Fail)]
    [InlineData(3.3, null, null, StepOutcome.Pass)]
    [InlineData(3.3, null, 3.2, StepOutcome.Fail)]
    [InlineData(3.3, 3.4, null, StepOutcome.Fail)]
    [InlineData(double.NaN, null, null, StepOutcome.Error)]
    [InlineData(double.PositiveInfinity, 0d, 1d, StepOutcome.Error)]
    public void LimitCheckUsesInclusiveBounds(double value, double? low, double? high, StepOutcome expected)
    {
        var result = LimitChecker.Check(value, low, high);
        Assert.Equal(expected, result.Result);
        Assert.Equal(low, result.Low);
        Assert.Equal(high, result.High);
        Assert.Contains(expected.ToString(), result.ToString());
    }

    [Fact]
    public void InvalidLimitsAreErrorsNotFailedMeasurements()
    {
        Assert.Throws<ArgumentException>(() => LimitChecker.Check(3.3, 3.4, 3.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => LimitChecker.Check(3.3, double.NaN, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => LimitChecker.Check(3.3, null, double.PositiveInfinity));
    }
}
