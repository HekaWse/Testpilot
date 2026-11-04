using Microsoft.EntityFrameworkCore;
using Testpilot.Core;

namespace Testpilot.Data;

/// <summary>
/// The test system types used in the company (names and descriptions as specified by the test department).
/// Seeding only adds missing types (name compared case-insensitively); existing types and their devices are never changed.
/// </summary>
public static class TestSystemTypeSeed
{
    public static IReadOnlyList<(string Name, string Description)> Types { get; } =
    [
        ("PS01", "Prüfplatz für manuelle Funktionstest oder Programmierungen"),
        ("PS02", "Funktionstester, Eigenbau"),
        ("PS04", "Funktionstester, Eigenbau"),
        ("PS05", "Funktionstest für Funkmodule, Eigenbau"),
        ("PS06", "Boundary Scan Tester und Programmierplatz, Eigenbau"),
        ("PS07", "ICT und Funktionstester Fa. Dr. Eschke"),
        ("PS10", "Funktionstester für Trumpf Baugruppen, Testbox von Fa. Trumpf -> Kunde ist Eigentümer!"),
        ("PS11", "Hochspg. Tester, Fa. Deutronic"),
        ("PS12", "Programmierplatz AMB35, Eigenbau"),
        ("PS13", "Programmierplatz TPM, Eigenbau"),
        ("PS14", "Funktionstest für MCP535, Eigenbau"),
        ("PS15", "Prüfplatz zur freien Verwendung, Eigenbau"),
        ("PS17", "Programmierplatz MCP545X, Eigenbau"),
        ("PS18", "Programmierplatz PRIME8, Eigenbau"),
        ("PS20", "Funktionstest für neue AMB35, Eigenbau"),
        ("PS21", "Programmierplatz für neue AMB35, Eigenbau"),
        ("PS23", "Funktionstester für LAWO Baugruppen, Eigenbau"),
        ("PS24", "Funktionstester für FEO Baugruppen, beigestellt von Fa. FEO -> Kunde ist Eigentümer!"),
        ("PS25", "Funktionstester für MMX-Baugruppen, beigestellt von Fa. HGS -> Kunde ist Eigentümer!"),
        ("PS26", "Funktionstester für MMX-Endgeräte in OFE, bereitgestellt von Fa. HGS -> Kunde ist Eigentümer."),
        ("PS28", "Funktionstester für VCI-Baugruppen in OFE, bereitgestellt von Fa. HGS -> Kunde ist Eigentümer."),
        ("PS29", "Funktionstester für VCI-Baugruppen in OFE, bereitgestellt von Fa. HGS -> Kunde ist Eigentümer."),
        ("PX30", "Funktionstester für Truma CP-Baugruppen in OFE, Eigenbau"),
        ("PS31", "Funktionstester für Nutzentests in OFH, Eigenbau"),
    ];

    /// <summary>Adds the missing types and returns their names (empty when everything already exists).</summary>
    public static async Task<IReadOnlyList<string>> SeedAsync(TestpilotDbContext db, CancellationToken cancellationToken = default)
    {
        var existing = await db.TestSystemTypes.Select(type => type.Name).ToListAsync(cancellationToken);
        var existingNames = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var (name, description) in Types)
        {
            if (!existingNames.Add(name)) continue;
            db.TestSystemTypes.Add(new TestSystemType { Name = name, Description = description });
            added.Add(name);
        }
        if (added.Count != 0)
            await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
