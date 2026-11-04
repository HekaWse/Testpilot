using System.Security.Cryptography;

namespace Testpilot.Core;

/// <summary>Hashes and checks debug-mode passwords with PBKDF2 (SHA-256, random 16-byte salt).</summary>
public static class DebugPassword
{
    public const int MinimumLength = 6;
    public const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), DefaultIterations);
    }

    /// <summary>Compares in constant time; damaged stored values count as "wrong password".</summary>
    public static bool Verify(string password, string storedHash, string storedSalt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (iterations <= 0) return false;
        byte[] expected, salt;
        try
        {
            expected = Convert.FromBase64String(storedHash);
            salt = Convert.FromBase64String(storedSalt);
        }
        catch (FormatException)
        {
            return false;
        }
        if (expected.Length != HashBytes || salt.Length == 0) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
