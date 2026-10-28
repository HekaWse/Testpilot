using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using Testpilot.Drivers.Abstractions;
using Testpilot.Editor.Services;

namespace Testpilot.Tests;

public sealed class EditorReviewTests
{
    [Theory]
    [InlineData("0,2", 0.2)]
    [InlineData("0.2", 0.2)]
    [InlineData(" 10 ", 10.0)]
    [InlineData("1e-3", 0.001)]
    public void EditorInputParsesBothDecimalSeparators(string text, double expected)
    {
        Assert.Equal(expected, EditorInput.ParseOptionalNumber(text, "NPLC")!.Value, 12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EditorInputTreatsEmptyAsNotSet(string? text)
    {
        Assert.Null(EditorInput.ParseOptionalNumber(text, "NPLC"));
        Assert.Null(EditorInput.ParseOptionalSi(text, MeasurementUnit.Volt, "Bereich"));
    }

    [Theory]
    [InlineData("1.000,5")]
    [InlineData("1,000.5")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public void EditorInputRejectsAmbiguousOrInvalidNumbers(string text)
    {
        var exception = Assert.Throws<FormatException>(() => EditorInput.ParseOptionalNumber(text, "NPLC"));
        Assert.StartsWith("NPLC:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorInputRoundTripsGermanDisplay()
    {
        var text = EditorInput.FormatNumber(0.2);
        Assert.Equal("0,2", text);
        Assert.Equal(0.2, EditorInput.ParseOptionalNumber(text, "NPLC"));
    }

    [Theory]
    [InlineData("3,3 m", 0.0033)]
    [InlineData("3.3 mV", 0.0033)]
    [InlineData("3,3", 3.3)]
    public void EditorInputParsesSiValuesWithBothSeparators(string text, double expected)
    {
        Assert.Equal(expected, EditorInput.ParseOptionalSi(text, MeasurementUnit.Volt, "Grenzwert")!.Value, 12);
    }

    [Fact]
    public void EditorInputReportsInvalidSiValueInGerman()
    {
        var exception = Assert.Throws<FormatException>(() => EditorInput.ParseOptionalSi("3,3 kA", MeasurementUnit.Volt, "Grenzwert"));
        Assert.Contains("kein gültiger Wert in V", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceEditRulesBlockKeyChangeWhenDeviceIsUsed()
    {
        var device = Device();
        var usage = new DeviceEditRules.DeviceUsage([], ["A1/B V1"], []);

        var check = DeviceEditRules.Check(device, device.OptionsJson, "dmm2", device.Model, device.OptionsJson, usage);

        Assert.NotNull(check.Error);
        Assert.Contains("A1/B V1", check.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceEditRulesBlockKeyChangeWhenStationUsesDevice()
    {
        var device = Device();
        var usage = new DeviceEditRules.DeviceUsage([], [], ["PS04-1"]);

        Assert.NotNull(DeviceEditRules.Check(device, device.OptionsJson, "dmm2", device.Model, device.OptionsJson, usage).Error);
    }

    [Fact]
    public void DeviceEditRulesBlockHardwareChangeWhenReleasedPlanUsesDevice()
    {
        var device = Device();
        var usage = new DeviceEditRules.DeviceUsage(["A1/B V2"], [], []);

        Assert.NotNull(DeviceEditRules.Check(device, device.OptionsJson, "dmm1", "OtherModel", device.OptionsJson, usage).Error);
        Assert.NotNull(DeviceEditRules.Check(device, device.OptionsJson, "dmm1", device.Model, "{\"slots\":{\"1\":\"34921A\"}}", usage).Error);
    }

    [Fact]
    public void DeviceEditRulesWarnOnHardwareChangeUsedByDraftsOrStations()
    {
        var device = Device();
        var usage = new DeviceEditRules.DeviceUsage([], ["A1/B V3"], ["PS04-1"]);

        var check = DeviceEditRules.Check(device, device.OptionsJson, "dmm1", "OtherModel", device.OptionsJson, usage);

        Assert.Null(check.Error);
        Assert.NotNull(check.Warning);
    }

    [Fact]
    public void DeviceEditRulesAllowEverythingForUnusedDeviceAndDescriptionOnlyChanges()
    {
        var device = Device();
        var unused = DeviceEditRules.Check(device, device.OptionsJson, "dmm2", "OtherModel", "{}", DeviceEditRules.DeviceUsage.None);
        var sameHardware = DeviceEditRules.Check(device, device.OptionsJson, "dmm1", device.Model, device.OptionsJson,
            new DeviceEditRules.DeviceUsage(["A1/B V2"], [], ["PS04-1"]));

        Assert.Equal(new DeviceEditRules.DeviceEditCheck(null, null), unused);
        Assert.Equal(new DeviceEditRules.DeviceEditCheck(null, null), sameHardware);
    }

    [Fact]
    public void EditorErrorsMapKnownExceptionsToGerman()
    {
        Assert.Equal(EditorErrors.SaveFailed, EditorErrors.ToGerman(new DbUpdateException("UNIQUE constraint failed")));
        Assert.Equal("Nur Entwürfe können freigegeben werden.", EditorErrors.ToGerman(new PlanConflictException("Nur Entwürfe können freigegeben werden.")));
        Assert.StartsWith("Ungültiges JSON", EditorErrors.ToGerman(new JsonException("bad")), StringComparison.Ordinal);
        Assert.StartsWith("Ungültige Eingabe", EditorErrors.ToGerman(new ArgumentException("Value cannot be empty.")), StringComparison.Ordinal);
        Assert.True(EditorErrors.IsExpected(new PlanConflictException("x")));
        Assert.True(EditorErrors.IsExpected(new FormatException("x")));
        Assert.False(EditorErrors.IsExpected(new NullReferenceException()));
    }

    [Fact]
    public void ChannelAllocationAnalyzerIncludesClosedRelaysAndIgnoresOpenOnes()
    {
        var plan = new TestPlan
        {
            ArtNr = "A1",
            Fz = "A",
            Steps =
            [
                new TestStep { Order = 0, Name = "Measure", StepType = StepType.Measure, DeviceKey = "dmm1",
                    ParametersJson = StepParameters.Serialize(new MeasureParameters(1, "VOLT:DC")), Unit = MeasurementUnit.Volt },
                new TestStep { Order = 1, Name = "Close", StepType = StepType.Relay, DeviceKey = "dmm1",
                    ParametersJson = StepParameters.Serialize(new RelayParameters(2, true)) },
                new TestStep { Order = 2, Name = "Open", StepType = StepType.Relay, DeviceKey = "dmm1",
                    ParametersJson = StepParameters.Serialize(new RelayParameters(3, false)) }
            ]
        };

        var allocations = ChannelAllocationAnalyzer.Analyze(plan,
            new Dictionary<string, DeviceProfile>(StringComparer.Ordinal) { ["dmm1"] = PairProfile() });

        Assert.Equal([1, 2], allocations.Select(item => item.Channel));
        Assert.Equal("Relais", allocations[1].Usage);
        Assert.Equal([2], allocations[0].ConflictingChannels);
    }

    [Fact]
    public void ChannelAllocationAnalyzerWorksWithoutProfilesAndForUnknownChannels()
    {
        var plan = new TestPlan
        {
            ArtNr = "A1",
            Fz = "A",
            Steps =
            [
                new TestStep { Order = 0, Name = "First", StepType = StepType.Measure, DeviceKey = "dmm1",
                    ParametersJson = StepParameters.Serialize(new MeasureParameters(99, "VOLT:DC")), Unit = MeasurementUnit.Volt },
                new TestStep { Order = 1, Name = "Second", StepType = StepType.Measure, DeviceKey = "dmm1",
                    ParametersJson = StepParameters.Serialize(new MeasureParameters(99, "RES")), Unit = MeasurementUnit.Ohm }
            ]
        };

        var withoutProfile = Assert.Single(ChannelAllocationAnalyzer.Analyze(plan, new Dictionary<string, DeviceProfile>()));
        var unknownChannel = Assert.Single(ChannelAllocationAnalyzer.Analyze(plan,
            new Dictionary<string, DeviceProfile>(StringComparer.Ordinal) { ["dmm1"] = PairProfile() }));

        Assert.True(withoutProfile.FunctionConflict);
        Assert.Empty(unknownChannel.ConflictingChannels);
    }

    private static SystemTypeDevice Device() => new()
    {
        DeviceKey = "dmm1",
        Model = DeviceCatalog.Ag34980A,
        OptionsJson = "{\"slots\":{},\"channels\":[]}"
    };

    private static DeviceProfile PairProfile()
    {
        var state = new ChannelStateProfile(new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), true);
        return new DeviceProfile("test", "Test", 2, "channel",
            new HashSet<DeviceCapability> { DeviceCapability.SwitchMultimeter }, state,
            [
                new ChannelProfile(1, "A", ChannelType.Voltage, new HashSet<string>(["VOLT:DC"], StringComparer.Ordinal),
                    new Dictionary<string, NumericRange>(StringComparer.Ordinal), state, new HashSet<int> { 2 }),
                new ChannelProfile(2, "B", ChannelType.Relay, new HashSet<string>(StringComparer.Ordinal),
                    new Dictionary<string, NumericRange>(StringComparer.Ordinal), state)
            ], [], []);
    }
}
