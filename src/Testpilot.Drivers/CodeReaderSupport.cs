namespace Testpilot.Drivers;

/// <summary>The reader answered, but not with a valid code (for example CANNOTREAD or a wrong length).</summary>
public sealed class BarcodeReadException(string message) : IOException(message);

/// <summary>The code readers read the board serial number: exactly 10 digits (as in the legacy CVI program).</summary>
internal static class SerialNumberCode
{
    public const int Length = 10;
    public const string Description = "10 digits";

    public static bool IsValid(string code) => code.Length == Length && code.All(char.IsAsciiDigit);
}
