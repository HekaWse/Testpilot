using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Testpilot.Core;

namespace Testpilot.Data;

/// <summary>
/// Maintains the persons allowed to use the debug mode of the operator program and checks their logins.
/// Short names are 2-20 letters or digits and are compared in upper case. Messages are German for the user interface.
/// </summary>
public sealed class DebugUserService(TestpilotDbContext database)
{
    private static readonly Regex ShortNamePattern = new("^[A-Z0-9]{2,20}$", RegexOptions.CultureInvariant);

    public static string NormalizeShortName(string shortName) =>
        (shortName ?? "").Trim().ToUpperInvariant();

    /// <summary>Creates the user or sets a new password for an existing one.</summary>
    public async Task SetPasswordAsync(string shortName, string password, CancellationToken cancellationToken = default)
    {
        var name = NormalizeShortName(shortName);
        if (!ShortNamePattern.IsMatch(name))
            throw new ArgumentException("Das Kürzel muss aus 2 bis 20 Buchstaben (A-Z) oder Ziffern bestehen.");
        if (password is null || password.Length < DebugPassword.MinimumLength)
            throw new ArgumentException($"Das Passwort muss mindestens {DebugPassword.MinimumLength} Zeichen lang sein.");
        var user = await database.DebugUsers.SingleOrDefaultAsync(item => item.ShortName == name, cancellationToken);
        if (user is null)
        {
            user = new DebugUser { ShortName = name };
            database.DebugUsers.Add(user);
        }
        (user.PasswordHash, user.PasswordSalt, user.PasswordIterations) = DebugPassword.Hash(password);
        user.UpdatedAt = DatabaseClock.UtcNow();
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Returns true when the short name exists and the password matches.</summary>
    public async Task<bool> VerifyAsync(string shortName, string password, CancellationToken cancellationToken = default)
    {
        var name = NormalizeShortName(shortName);
        if (name.Length == 0 || string.IsNullOrEmpty(password)) return false;
        var user = await database.DebugUsers.AsNoTracking().SingleOrDefaultAsync(item => item.ShortName == name, cancellationToken);
        return user is not null && DebugPassword.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations);
    }

    public async Task DeleteAsync(string shortName, CancellationToken cancellationToken = default)
    {
        var name = NormalizeShortName(shortName);
        var user = await database.DebugUsers.SingleOrDefaultAsync(item => item.ShortName == name, cancellationToken);
        if (user is null) return;
        database.DebugUsers.Remove(user);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DebugUser>> ListAsync(CancellationToken cancellationToken = default) =>
        await database.DebugUsers.AsNoTracking().OrderBy(user => user.ShortName).ToListAsync(cancellationToken);
}
