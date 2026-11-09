using Testpilot.Core;

namespace Testpilot.Tests;

public sealed class FormulaTests
{
    private static readonly Dictionary<string, double[]> Values = new(StringComparer.Ordinal)
    {
        ["V103"] = [3.0],
        ["V114"] = [2.9],
        ["GS empfangen 433:rssi"] = [-60, -61, -59, -60, -58],
        ["2.9.5 GS zu DUT 868 RSSI Kanal 0"] = [-54.4]
    };

    private static double Evaluate(string formula) => Formula.Evaluate(formula, Resolve);

    private static IReadOnlyList<double> Resolve(string name) =>
        Values.TryGetValue(name, out var list) ? list : throw new FormulaException($"unknown {name}");

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("-2 - -3", 1)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("1.5e3 / 1000", 1.5)]
    [InlineData("{V114} / {V103}", 2.9 / 3.0)]
    [InlineData("trunc(-3.7)", -3)]
    [InlineData("round(2.5)", 3)]
    [InlineData("abs(-4)", 4)]
    [InlineData("min(3, 1, 2)", 1)]
    [InlineData("max(0, -5)", 0)]
    [InlineData("mean({GS empfangen 433:rssi})", -59.6)]
    [InlineData("sum({GS empfangen 433:rssi})", -298)]
    [InlineData("count({GS empfangen 433:rssi})", 5)]
    [InlineData("{GS empfangen 433:rssi[2]}", -59)]
    [InlineData("3 <= 3", 1)]
    [InlineData("3 > 4", 0)]
    [InlineData("({V103} == 3) * ({V114} != 3)", 1)]
    [InlineData("-106 + max(0, trunc({2.9.5 GS zu DUT 868 RSSI Kanal 0}) + 55)", -105)]
    public void EvaluatesArithmeticFunctionsComparisonsAndReferences(string formula, double expected) =>
        Assert.Equal(expected, Evaluate(formula), 10);

    [Theory]
    [InlineData("", "empty")]
    [InlineData("1 +", "end of the formula")]
    [InlineData("foo(1)", "Unknown function")]
    [InlineData("{V103", "Missing '}'")]
    [InlineData("{GS empfangen 433:rssi}", "5 values")]
    [InlineData("{GS empfangen 433:rssi[7]}", "index 7")]
    [InlineData("trunc(1, 2)", "exactly one value")]
    [InlineData("{V999}", "unknown V999")]
    [InlineData("2 3", "Unexpected '3'")]
    public void ReportsUnderstandableErrors(string formula, string expectedMessage)
    {
        var error = Assert.Throws<FormulaException>(() => Evaluate(formula));
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReturnsReferencesWithoutIndexAndDoesNotNeedValues()
    {
        Assert.Equal(["DUT lesen:data", "serial"], Formula.Validate("{DUT lesen:data[1]} + {serial} - {serial}"));
        Assert.Throws<FormulaException>(() => Formula.Validate("1 +* 2"));
    }

    [Fact]
    public void FormatTextReplacesReferencesInvariantly()
    {
        var outputs = new Dictionary<string, double[]>
        {
            ["major"] = [1], ["minor"] = [6], ["deb"] = [20], ["data"] = [1.5, 2]
        };
        IReadOnlyList<double> Lookup(string name) => outputs[name];

        Assert.Equal("1.6.20", Formula.FormatText("{major}.{minor}.{deb}", Lookup));
        Assert.Equal("Daten 1.5,2 / 2", Formula.FormatText("Daten {data} / {data[1]}", Lookup));
        Assert.Throws<FormulaException>(() => Formula.FormatText("{major", Lookup));
    }
}
