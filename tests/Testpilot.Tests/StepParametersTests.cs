using System.Text.Json;
using Testpilot.Core;

namespace Testpilot.Tests;

public sealed class StepParametersTests
{
    [Theory]
    [InlineData(StepType.Measure, "{\"channel\":1003,\"function\":\"VOLT:DC\",\"range\":10,\"nplc\":1}")]
    [InlineData(StepType.Relay, "{\"channel\":1003,\"closed\":true}")]
    [InlineData(StepType.SetPower, "{\"voltage\":3.3,\"currentLimit\":0.5,\"enabled\":true}")]
    [InlineData(StepType.Program, "{\"filePath\":\"firmware.hex\"}")]
    [InlineData(StepType.Scan, "{}")]
    [InlineData(StepType.Wait, "{\"durationMilliseconds\":10}")]
    [InlineData(StepType.OperatorPrompt, "{\"message\":\"Confirm\"}")]
    [InlineData(StepType.External, "{\"command\":\"adapter.exe\",\"arguments\":[]}")]
    [InlineData(StepType.CustomCode, "{\"handlerKey\":\"special\",\"parameters\":{}}")]
    public void EveryStepTypeHasValidatedParameters(StepType type, string json)
    {
        var step = new TestStep { Name = "Step", StepType = type, DeviceKey = "device", ParametersJson = json };
        if (type == StepType.Measure) step.Unit = MeasurementUnit.Volt;
        StepParameters.Validate(step);
    }

    [Theory]
    [InlineData("VOLT:DC", MeasurementUnit.Volt)]
    [InlineData("VOLT:AC", MeasurementUnit.Volt)]
    [InlineData("CURR:DC", MeasurementUnit.Ampere)]
    [InlineData("CURR:AC", MeasurementUnit.Ampere)]
    [InlineData("RES", MeasurementUnit.Ohm)]
    [InlineData("FRES", MeasurementUnit.Ohm)]
    public void MeasurementFunctionsUseMatchingSiUnits(string function, MeasurementUnit unit)
    {
        var step = new TestStep { Name = "Measure", DeviceKey = "dmm1", Unit = unit,
            ParametersJson = StepParameters.Serialize(new MeasureParameters(1003, function)) };
        StepParameters.Validate(step);
        step.Unit = null;
        Assert.Throws<ArgumentException>(() => StepParameters.Validate(step));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"channel\":1003,\"function\":\"VOLT:DC\",\"typo\":1}")]
    [InlineData("{\"channel\":\"1003\",\"function\":\"VOLT:DC\"}")]
    public void MissingMalformedOrUnknownFieldsAreRejected(string json)
    {
        var step = new TestStep { Name = "Measure", DeviceKey = "dmm1", Unit = MeasurementUnit.Volt, ParametersJson = json };
        Assert.Throws<JsonException>(() => StepParameters.Validate(step));
    }

    [Theory]
    [InlineData("{\"channel\":0,\"function\":\"VOLT:DC\"}")]
    [InlineData("{\"channel\":1003,\"function\":\"UNKNOWN\"}")]
    [InlineData("{\"channel\":1003,\"function\":\"VOLT:DC\",\"range\":-1}")]
    [InlineData("{\"channel\":1003,\"function\":\"VOLT:DC\",\"nplc\":0}")]
    public void InvalidMeasurementConfigurationIsRejected(string json)
    {
        var step = new TestStep { Name = "Measure", DeviceKey = "dmm1", Unit = MeasurementUnit.Volt, ParametersJson = json };
        Assert.ThrowsAny<ArgumentException>(() => StepParameters.Validate(step));
    }
}
