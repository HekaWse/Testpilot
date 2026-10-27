namespace Testpilot.Core;

public sealed record LimitCheckResult(double Value, double? Low, double? High, StepOutcome Result);

public static class LimitChecker
{
    public static void Validate(double? low, double? high)
    {
        if (low.HasValue && !double.IsFinite(low.Value))
            throw new ArgumentOutOfRangeException(nameof(low));
        if (high.HasValue && !double.IsFinite(high.Value))
            throw new ArgumentOutOfRangeException(nameof(high));
        if (low > high)
            throw new ArgumentException("The lower limit must not exceed the upper limit.");
    }

    /// <summary>Checks inclusive SI limits. A non-finite measurement is an error, never a pass.</summary>
    public static LimitCheckResult Check(double value, double? low, double? high)
    {
        Validate(low, high);
        var result = !double.IsFinite(value) ? StepOutcome.Error
            : (low.HasValue && value < low.Value) || (high.HasValue && value > high.Value)
                ? StepOutcome.Fail : StepOutcome.Pass;
        return new(value, low, high, result);
    }
}
