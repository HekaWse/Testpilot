using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using YamlDotNet.Core;

namespace Testpilot.Editor.Services;

/// <summary>Maps the exceptions an editor action can expect to German messages for the user interface.</summary>
public static class EditorErrors
{
    public const string SaveFailed = "Speichern fehlgeschlagen: Eintrag existiert bereits oder wurde zwischenzeitlich geändert.";

    /// <summary>
    /// True for failures caused by input, validation, invalid files or concurrent changes. Everything else is a bug
    /// and is deliberately not caught by the pages.
    /// </summary>
    public static bool IsExpected(Exception exception) => exception is ArgumentException or InvalidOperationException
        or DbUpdateException or JsonException or FormatException or OverflowException or InvalidDataException
        or IOException or YamlException;

    public static string ToGerman(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            // The plan service and the database context write these messages in German for the user interface.
            PlanConflictException or PlanNotFoundException => exception.Message,
            DbUpdateConcurrencyException => "Speichern fehlgeschlagen: Der Eintrag wurde zwischenzeitlich geändert oder gelöscht. Bitte neu laden.",
            DbUpdateException => SaveFailed,
            JsonException => "Ungültiges JSON: " + exception.Message,
            YamlException => "Ungültiges YAML: " + exception.Message,
            FormatException => "Ungültige Zahl: " + exception.Message,
            OverflowException => "Die Zahl liegt außerhalb des zulässigen Bereichs.",
            InvalidDataException => "Ungültige Daten: " + exception.Message,
            IOException => "Datei konnte nicht gelesen werden: " + exception.Message,
            ArgumentException => "Ungültige Eingabe: " + exception.Message,
            _ => "Aktion nicht möglich: " + exception.Message
        };
    }
}
