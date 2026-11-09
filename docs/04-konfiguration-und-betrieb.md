# 4. Konfiguration und Betrieb

[Dokumentationsübersicht](README.md) · Zurück: [Prüfbetrieb](03-pruefbetrieb.md) · Weiter: [Architektur](05-architektur.md)

## Inhalt

- [Welche Konfiguration wird gelesen?](#welche-konfiguration-wird-gelesen)
- [Einstellungsreferenz](#einstellungsreferenz)
- [SAP konfigurieren](#sap-konfigurieren)
- [Geräteadressen und Pfade](#geräteadressen-und-pfade)
- [Datenbank und Migrationen](#datenbank-und-migrationen)
- [Veröffentlichen und installieren](#veröffentlichen-und-installieren)
- [Backup, Update und Wiederherstellung](#backup-update-und-wiederherstellung)
- [Produktionscheckliste](#produktionscheckliste)

## Welche Konfiguration wird gelesen?

**Später geladene Werte überschreiben frühere.** Nicht jeder Host verwendet dieselben Quellen.

| Prozess | Quellen und Besonderheiten |
| --- | --- |
| Editor | Standardkonfiguration von ASP.NET Core: `appsettings.json`, optional `appsettings.<Environment>.json`, in Development gegebenenfalls User Secrets, Umgebungsvariablen und Befehlszeilenkonfiguration. |
| Operator | Erforderliche `appsettings.json` **neben der ausführbaren Anwendung**, danach Umgebungsvariablen. Keine automatische umgebungsspezifische JSON-Datei und keine allgemeine Befehlszeilenkonfiguration. |
| RunPlan | `appsettings.json` neben der Anwendung; fehlt sie, Suche in übergeordneten Verzeichnissen nach `src\Testpilot.Editor\appsettings.json`; danach Umgebungsvariablen. Eigene, begrenzte CLI-Argumente. |
| EF-Werkzeug | Providerbezogene Design-Time-Factory. Verbindungsstring aus `-- --connection ...`, sonst `ConnectionStrings__Testpilot`, sonst lokalem Factory-Standard. Liest **nicht** automatisch die JSON-Konfiguration des Editors. |

Umgebungsvariablen schreiben `__` statt `:`:

```powershell
$env:Operator__Station = 'DEMO-1'
$env:Database__Provider = 'Sqlite'
$env:ConnectionStrings__Testpilot = 'Data Source=C:\TestpilotDemo\testpilot.db'
$env:Simulation__Enabled = 'true'
```

Das gilt nur im aktuellen Terminal und gestarteten Kindprozessen. Visual Studio übernimmt später gesetzte Terminalwerte nicht rückwirkend. Das Operator-Launchprofil setzt Simulation auf `true`; ein veröffentlichter Start ohne dieses Profil tut das nicht. Die tatsächlich wirksame Konfiguration beim Start kontrollieren.

## Einstellungsreferenz

„Standard“ meint das Verhalten ohne expliziten Wert; ausgelieferte JSON-Dateien können davon abweichen.

| Schlüssel | Bedeutung / Standard |
| --- | --- |
| `Database:Provider` | Erforderlich: exakt `Sqlite`, `SqlServer` oder `PostgreSql`. |
| `ConnectionStrings:Testpilot` | Erforderlicher Provider-Verbindungsstring. SQLite ausgeliefert: `Data Source=testpilot.db`. |
| `Operator:Station` | Erforderlicher Name des Prüfsystems, z. B. DEMO-1. Ausgelieferter Wert kann ein anderer Platz sein. |
| `Operator:SerialNumberPattern` | Standard `^[0-9]{10}$`. Vollständige Übereinstimmung durch passende Regex-Anker sicherstellen. Prüfung hat 1 s Regex-Timeout. |
| `Operator:FixtureDevice` | Gerätekennung des Adapters; leer = manueller Start. |
| `Operator:SerialReaderDevice` | Fester Leser; benötigt FixtureDevice. Leer = Handscanner. |
| `Operator:FixtureSettleMilliseconds` | 0–10000, Standard 500 ms nach Verriegelung. |
| `Simulation:Enabled` | **Operator: fehlend = false**, ungültiger boolescher Text wird abgelehnt. RunPlan: standardmäßig Simulation; nur gültiges `false` schaltet sie aus. Immer ausdrücklich setzen. |
| `Simulation:SerialNumber` | Seriennummer des simulierten automatischen Lesers, Standard `1234567890`. |
| `Debug:DisableTimeouts` | Deaktiviert Sequencer-Timeouts, nicht automatisch SAP-, Treiber- oder Prozesstimeouts. Operator-Standard false. Nur kontrolliert beim Debuggen verwenden. |
| `Sequencer:OperationTimeoutSeconds` | 1–3600, Standard 30 s. |
| `Sequencer:LongOperationTimeoutSeconds` | 1–3600, Standard 600 s für Program/External/CustomCode. |
| `Export:Directory` | Operator/RunPlan: JSON-Ergebnisse; Standard `exports`. |
| `Logging:Directory` | Operator/RunPlan: Logordner; Standard `logs`. |
| `Serilog:…` | Editor-Logkonfiguration; ausgeliefert Konsole und tägliche Datei `logs\testpilot-.log`, 14 Dateien. Nicht identisch mit `Logging:Directory`. |
| `DevicesFile` | Leer/nicht gesetzt: optionale `devices.json` neben der Anwendung. Expliziter Pfad muss existieren. |
| `Sap:BaseUrl` | Nicht leer: REST-Client, hat Vorrang vor Sap:Command und Mock. |
| `Sap:Command` | Externer lesender SAP-Adapter, wenn BaseUrl leer ist. |
| `Sap:ArgumentsJson` | JSON-Array von Argumenten, Standard `["{fauf}"]`. |
| `Sap:TimeoutSeconds` | 1–600, Standard 30 s, unabhängig von Debug:DisableTimeouts. |
| `Sap:MockOrders:<FAUF>:ArtNr/Fz/Description` | Aufträge für SAP-Mock; Description optional. Nur ohne BaseUrl und Command aktiv. |

Treiberbezogene Parameter stehen bei `StationDevice.ConnectionParametersJson`, nicht als beliebige globale Konfigurationsschlüssel. [Treiberreferenz](08-treiber-und-profile.md).

## SAP konfigurieren

Die Anbindung ist **ausschließlich lesend**. Ein Testergebnis wird nicht automatisch an SAP zurückgeschrieben.

### Mock ohne Netzwerk

```json
{
  "Sap": {
	"BaseUrl": "",
	"Command": "",
	"MockOrders": {
	  "FAUF-DEMO": { "ArtNr": "DEMO-001", "Fz": "A", "Description": "Demoteil" }
	}
  }
}
```

Beide Auswahlwerte leeren; nur MockOrders ergänzen reicht nicht, wenn bereits ein REST-Endpunkt konfiguriert ist. In einer .NET-10-Umgebung kann eine leere Prozess-Umgebungsvariable einen JSON-Wert überschreiben. Bei Unsicherheit für eine separate Installation die JSON-Datei selbst eindeutig konfigurieren, statt von der Umgebung des aufrufenden Prozesses auszugehen.

### REST

- Request: `GET <BaseUrl>/getOrderDetails?orderNo=<FAUF>`.
- FAUF: 1–40 Zeichen, Buchstaben/Ziffern/Bindestrich.
- Erwartete Antwort enthält `Output.Material`, `Output.FZ`, `Output.MaterialText`; `State: "-1"` bedeutet Auftrag nicht gefunden.
- Material/FZ können als Text oder Zahl geliefert werden und werden als Text übernommen.
- TLS-Zertifikate werden normal geprüft; keine Abschaltung der Zertifikatsprüfung.
- Erreichbarkeit, zulässige URL, Zugangs-/Netzwerkregelung mit der zuständigen Stelle klären. Keine produktiven Abfragen für die Demo verwenden.

### Externes Programm

```json
{
  "Sap": {
	"BaseUrl": "",
	"Command": "C:\\TestpilotAdapters\\SapRead.exe",
	"ArgumentsJson": "[\"{fauf}\"]",
	"TimeoutSeconds": 30
  }
}
```

Das Programm muss bei Erfolg ausschließlich einen JSON-Datensatz nach stdout schreiben:

```json
{"artNr":"DEMO-001","fz":"A","description":"Demoteil"}
```

Diagnosen gehören nach stderr. Kein Shellstart, keine `.bat`/`.cmd`, Argumente als einzelne Elemente statt selbst zusammengesetzter Shellzeile. Nicht-null Exitcode, ungültiges JSON oder Timeout führt zu einer SAP-Exception; bei Abbruch wird die Prozessstruktur beendet.

## Geräteadressen und Pfade

### `devices.json`

```json
[
  {"Alias":"Ag34970A","Resource":"GPIB0::9::INSTR"},
  {"Alias":"Hmp2030","Resource":"TCPIP0::192.0.2.20::5025::SOCKET"}
]
```

Alias = Gerätekennung der Station, ohne Beachtung der Groß-/Kleinschreibung. Doppelte/leere oder unbekannte Aliase werden gemeldet. Die Datei überschreibt nur VISA-Ressourcen dieses PCs, nicht Typ, Modell oder Datenbankeinträge. Nicht-VISA-Geräte dürfen nicht damit adressiert werden; simulierte Treiber bleiben unverändert. Ein vorhandener `host` wird beim Setzen einer VISA-Ressource entsprechend ersetzt.

Die optionale Standarddatei darf fehlen. Eine ausdrücklich durch `DevicesFile` angegebene Datei darf **nicht** fehlen. Kommentare/nachgestellte Kommas werden vom Parser dieser Datei toleriert, nicht allgemein von allen JSON-Verträgen.

### Relative Pfade unterscheiden

| Wert | Bezug |
| --- | --- |
| SQLite `Data Source=testpilot.db` | Aktuelles Arbeitsverzeichnis des Prozesses. |
| Relative Export-/Logpfade | Aktuelles Arbeitsverzeichnis. |
| Operator `appsettings.json` | Verzeichnis der Anwendung (`AppContext.BaseDirectory`). |
| Standard-`devices.json` | Verzeichnis der Anwendung. |
| Relatives explizites `DevicesFile` | Verzeichnis der Anwendung. |
| Relative Firmware-/Programmpfade | Abhängig vom aufrufenden Prozess/Treiber; für Betrieb absolute, versionierte Pfade verwenden. |

Operator verwendet bei Entwicklung über `dotnet run`/Visual Studio das Editor-Verzeichnis als Arbeitsverzeichnis, um die Entwicklungsdatenbank zu teilen. Darauf bei veröffentlichten Anwendungen **nicht** vertrauen. Absolute Pfade für Daten, Exporte und Logs vermeiden versehentlich verschiedene Datenbanken.

## Datenbank und Migrationen

### Provider wählen

```text
Sqlite:     Data Source=C:\TestpilotData\testpilot.db
SqlServer:  Server=SERVER\INSTANCE;Database=Testpilot;Integrated Security=True;Encrypt=True
PostgreSql: Host=db.example.internal;Database=testpilot;Username=testpilot
```

Servernamen und Authentifizierung sind Beispiele. Für Serverprovider Zertifikate, Berechtigungen und Authentifizierung passend einrichten; Kennwörter nicht ins Repository schreiben. Produktive Verbindungsstrings außerhalb versionierter Dateien verwalten.

Ein Providerwechsel wählt eine andere Datenbank. Er **konvertiert und kopiert keine Daten**. Dafür braucht es einen gesondert geplanten und geprüften Datentransfer. Auch SQL-Server-/PostgreSQL-Migrationen sind vorhanden; generierte Migrations-SQL-Tests ersetzen keinen Integrationstest gegen den konkreten Server.

### Anwendungsmigration

Aus dem Repository mit expliziter Verbindung:

```powershell
$env:Database__Provider = 'Sqlite'
$env:ConnectionStrings__Testpilot = 'Data Source=C:\TestpilotData\testpilot.db'
dotnet run --project src\Testpilot.Editor -- --migrate
```

Migration und Seed sind getrennte Starts: Werden beide Flags zugleich angegeben, endet der Prozess nach der Migration. Danach bei Bedarf separat:

```powershell
dotnet run --project src\Testpilot.Editor -- --seed-system-types
```

Seed fügt bekannte Typen, die PS05-Ausstattung (Multiplexer, Netzteil, Adapter, Prüfling und Golden Sample über `FSACommunication.dll`, J-Link, Scanner), die Prüfsysteme **PS05-1** (echte Treiber) und **PS05-Sim** (nur simulierte Treiber) sowie den **Entwurf des FB-HAT-Prüfplans 4001430-01 / 332351** hinzu. Vorhandene Daten werden nicht überschrieben: fehlende Geräte werden ergänzt, ein vorhandener Plan für ArtNr/FZ bleibt unverändert. Firmwaredateien werden nicht kopiert; der Plan verweist auf `\\heka.de\prod\pruefmittel\ATE\PS05\…`. Altdaten/Adressen nicht ungeprüft produktiv verwenden; den Entwurf vor der Freigabe prüfen (siehe [Migration FB-HAT](migration/ps05-fb-hat.md)).

### EF-Werkzeug

Das lokale Toolmanifest liegt im Repository direkt unter `dotnet-tools.json`:

```powershell
dotnet tool restore --tool-manifest .\dotnet-tools.json
dotnet tool run dotnet-ef -- database update --project src\Testpilot.Data --context SqliteTestpilotDbContext -- --connection 'Data Source=C:\TestpilotData\testpilot.db'
```

Manche .NET-10-Aufrufe entdecken das Manifest im Repository-Hauptverzeichnis automatisch. Beim Restore den Pfad ausdrücklich angeben; Werkzeugbefehle aus dem Repository starten. Für Server die entsprechende Contextklasse wählen: `SqlServerTestpilotDbContext` oder `PostgreSqlTestpilotDbContext`.

## Veröffentlichen und installieren

### Beispiel: getrennte Veröffentlichungsordner

```powershell
dotnet publish src\Testpilot.Editor -c Release -r win-x64 --self-contained true -o artifacts\publish\Editor
dotnet publish src\Testpilot.Operator -c Release -r win-x64 --self-contained true -o artifacts\publish\Operator
dotnet publish tools\Testpilot.RunPlan -c Release -r win-x64 --self-contained true -o artifacts\publish\RunPlan
```

Self-contained bringt die .NET-Laufzeit mit; Windows, Treiberruntimes und Betriebssystemvoraussetzungen bleiben erforderlich. Für abhängige Veröffentlichung `--self-contained false` verwenden und passende .NET-/ASP.NET-/Windows-Desktop-Runtimes bereitstellen.

Für bestätigte native 32-Bit-Abhängigkeiten separat mit `-r win-x86` veröffentlichen. Das macht die vorhandenen DLL-Gerüste noch nicht funktionsfähig. Prozessarchitektur, DLLs und VISA.NET-Anbindung müssen zusammenpassen. Trimming/AOT/Single-File nicht ohne eigene Prüfung hinzufügen; Standard-Publish verwenden.

### Zielrechner

1. Veröffentlichung vollständig kopieren, einschließlich `.pdb` für geplantes Debugging.
2. Laufzeitdaten außerhalb des Programmordners ablegen, z. B. `C:\TestpilotData`.
3. Hostkonfiguration je Programm anpassen; gemeinsame absolute Datenbank und getrennte/geeignete Log-/Exportordner setzen.
4. Zunächst Simulation aktivieren und Mockauftrag verwenden.
5. Datenbank mit dem zur Version passenden Editor migrieren, bevor andere Hosts zugreifen:

```powershell
Set-Location C:\TestpilotApps\Editor
.\Testpilot.Editor.exe --migrate
```

6. Editor per eingerichtetem Hosting starten, Bedienprogramm als Arbeitsplatzanwendung starten.
7. Bei echten VISA-Geräten Vendor-Runtime und Ressourcen einrichten; native Hardware separat abnehmen.

Blazor Interactive Server benötigt eine beständige Serververbindung. Bei Reverse Proxy/IIS HTTPS, WebSockets beziehungsweise SignalR, Zertifikate und Zugriffsregelung einrichten. Die Anwendung liefert keine vollständige Editor-Anmeldung mit; nicht ungeschützt im Internet bereitstellen. `launchSettings.json` ist eine Entwicklungshilfe, keine Produktionskonfiguration.

## Backup, Update und Wiederherstellung

### Was gehört zur Sicherung?

- Datenbank einschließlich Läufen, Debugbenutzern, freigegebenen Plänen und Einstellungen.
- JSON-Exporte und benötigte Logs nach Aufbewahrungsregel.
- Je Host `appsettings.json`, lokale Adressdatei, relevante Umgebungs-/Startkonfiguration.
- Firmware/Projektdateien und vertrauenswürdige externe Programme mit Version/Prüfsumme.
- Ausgelieferte Softwareversion inklusive passender Symbole.

Typ-/Stations-YAML ist ein Austauschformat, **kein vollständiges Backup**: Prüfpläne, Läufe und Benutzer fehlen dort.

### SQLite verlässlich sichern

Alle schreibenden Testpilot-Prozesse stoppen und Abschluss laufender Prüfungen abwarten. Danach eine konsistente Offline-Sicherung erstellen, oder ein für SQLite geeignetes Online-Backupverfahren verwenden. Nicht allein eine geöffnete `.db` kopieren und mögliche `-wal`/`-shm`-Dateien ignorieren. Datenbanksicherung und Exporte gemeinsam zeitlich kennzeichnen. Backups geschützt ablegen, da Produktionsdaten und Passwort-Hashes enthalten sein können.

Für Serverdatenbanken die administrativen Backup-/Restore-Verfahren des Providers nutzen. Wiederherstellung regelmäßig in einer getrennten Umgebung erproben.

### Kontrolliertes Update

1. Laufende Prüfungen beenden, alle schreibenden Hosts stoppen.
2. Backup einschließlich bisheriger Version erstellen.
3. Änderungen/Migrationen und Hardwareprofile prüfen; Update zuerst auf Sicherungskopie testen.
4. Neue Version installieren, Konfiguration nicht versehentlich durch Standarddateien ersetzen.
5. Migration explizit ausführen.
6. Alle beteiligten Hosts gemeinsam aktualisieren, Mischbetrieb vermeiden.
7. Simulation, danach freigegebenen Hardware-Abnahmetest durchführen.

**Rollback:** Eine ältere EXE ist nach einer Schemaänderung nicht automatisch kompatibel. Im Fehlerfall vollständige passende Sicherung und Softwareversion kontrolliert wiederherstellen. Zwischenzeitliche Produktionsläufe müssen vor einem Rücksetzen gesichert und fachlich abgeglichen werden; nicht einfach verwerfen.

## Produktionscheckliste

- [ ] Station, Gerätekennungen, Adressen und Typ passen zum Aufbau.
- [ ] `Simulation:Enabled=false` ist ausdrücklich und nachweislich wirksam.
- [ ] SAP-Ziel und Auftragsschlüssel sind geprüft.
- [ ] Datenbankmigrationen aktuell; alle Hosts nutzen dieselbe gewünschte Datenbank.
- [ ] Schreibrechte/Platz für Datenbank, Export und Logs vorhanden.
- [ ] Safe State, Verriegelung und Grenzen an echter Hardware getestet.
- [ ] Externe Programme/Firmware versioniert und vertrauenswürdig.
- [ ] Debug-Timeoutabschaltung im Normalbetrieb deaktiviert.
- [ ] Zugriffsschutz, Datensicherung, Archivierung und Wiederanlauf geklärt.

**Quelleneinstieg:** [Editorstart](../src/Testpilot.Editor/Program.cs), [OperatorSettings](../src/Testpilot.Operator/OperatorSettings.cs), [Datenbankkonfiguration](../src/Testpilot.Data/DatabaseConfiguration.cs), [RunPreparation/SAP-Auswahl](../src/Testpilot.Sequencer/RunPreparation.cs).
