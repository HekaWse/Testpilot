# 6. Datenmodell und Datenformate

[Dokumentationsübersicht](README.md) · Zurück: [Architektur](05-architektur.md) · Weiter: [Optimizer und Sequencer](07-optimizer-und-sequencer.md)

## Inhalt

- [Entitäten und Beziehungen](#entitäten-und-beziehungen)
- [Integrität und Versionierung](#integrität-und-versionierung)
- [Einheiten und Grenzwerte](#einheiten-und-grenzwerte)
- [Schrittparameter](#schrittparameter)
- [Benannte Einstellungen als JSON](#benannte-einstellungen-als-json)
- [YAML-Austauschformate](#yaml-austauschformate)
- [Ausführungsplan](#ausführungsplan)
- [Ergebnisexport](#ergebnisexport)

## Entitäten und Beziehungen

```text
TestSystemType --< SystemTypeDevice
	   |
	   +--< Station --< StationDevice
	   |
	   +--< TestPlan --< TestStep

Run --< RunDevice
  +--< RunStepResult

DebugUser                     (eigenständige Debugberechtigung)
```

`--<` bedeutet 1:n. Die Referenz zum Prüfsystemtyp ist bei älteren Stationen/Plänen optional. Einzelne Schritte referenzieren Geräte **nur über DeviceKey**. Laufresultate sind Snapshots und hängen nicht per Fremdschlüssel am veränderlichen Prüfschritt.

| Entität | Wesentliche Felder |
| --- | --- |
| `TestSystemType` | ID, eindeutiger Name, Beschreibung, Typgeräte. |
| `SystemTypeDevice` | ID, Typ-ID, DeviceKey, Model, Description, OptionsJson. |
| `Station` | ID, eindeutiger Name, Beschreibung, optionale Typ-ID, SettingsJson, Stationsgeräte. |
| `StationDevice` | ID, Stations-ID, DeviceKey, Driver, ConnectionParametersJson. |
| `TestPlan` | ID, ArtNr, Fz, Version, optionale Typ-ID, Status, SettingsJson, ExecutionPlanJson, CreatedAt/UpdatedAt/ReleasedAt, Schritte. |
| `TestStep` | ID, Plan-ID, Order, Name, StepType, DeviceKey, ParametersJson, LimitLow/High, Unit, BreakpointFlag. |
| `Run` | ID, Fauf, ArtNr/Fz, Seriennummer, Stationsname, Operator, PlanVersion, Start/Ende, Result, SoftwareVersion, Simulated. |
| `RunDevice` | Lauf-ID, DeviceKey und tatsächlich erfragte Identification. |
| `RunStepResult` | Lauf-ID, Reihenfolge/Name/Typ, Gerät/Kanal/Funktion, Messwert oder Text, Ergebnis, Grenzwert-/Einheitensnapshot, Start/Ende, Fehlermeldung. |
| `DebugUser` | Kürzel, Passwort-Hash, Salt, Iterationszahl, UpdatedAt. |

Die Station wird im Lauf als **Name** gespeichert; die komplette historische Stationskonfiguration und alle benannten Werte werden nicht zusätzlich im Run abgelegt. Das ist bei späteren Auswertungen und Sicherungskonzepten zu berücksichtigen.

Debugpasswörter werden mit PBKDF2/SHA-256, standardmäßig 210.000 Iterationen, zufälligem 16-Byte-Salt und 32-Byte-Hash verarbeitet. Hash/Salt werden Base64-kodiert gespeichert, die Prüfung vergleicht konstantzeitlich. Kürzel werden großgeschrieben. Ein Passwort-Reset oder das Löschen eines Benutzers beendet nicht automatisch eine bereits aktive Debuganmeldung; diese am Bedienprogramm ausdrücklich beenden. Implementierung: [DebugPassword](../src/Testpilot.Core/DebugPassword.cs).

## Integrität und Versionierung

### Statuswerte

- Plan: `Draft=0`, `Released=1`, `Retired=2`.
- Schrittergebnis: `Pass`, `Fail`, `Error`, `Skipped`.
- Laufergebnis: `Pass`, `Fail`, `Error`, `Aborted`.

JSON-Verträge haben eigene Enumdarstellungen; numerische EF-Statuswerte nicht mit JSON-Texten vermischen.

### Wichtige Unique-Indizes

- Namen von Typ und Station sowie Debugkürzel.
- DeviceKey innerhalb eines Typs beziehungsweise einer Station.
- `(ArtNr, Fz, Version)`.
- `(ArtNr, Fz)` **nur für Status Released**.
- `(TestPlanId, Order)`.
- `(RunId, DeviceKey)` und `(RunId, Order)`.

Der gefilterte Freigabeindex verwendet SQL Server `[Status] = 1`, SQLite/PostgreSQL `"Status" = 1`. Zusätzlich prüft der Service die Regel. Nicht nur auf eine UI-Sperre verlassen.

`Status` und `UpdatedAt` sind Concurrency-Tokens. Schrittänderungen aktualisieren den Zeitstempel des Entwurfs, damit eine gleichzeitige Freigabe nicht unbemerkt eine andere Schrittfolge einfriert. Datenbankkonflikte werden als Fachkonflikte gemeldet.

Freigegebene/zurückgezogene Pläne sind gegen Änderungen geschützt; erlaubt ist der kontrollierte Übergang Released → Retired. Neue Versionen sind neue Entwürfe mit neuen IDs, kopierten Schritten/Einstellungen und `max(Version)+1`.

Diese Regeln gelten über `TestpilotDbContext`/Services. EF-Bulkupdates, rohe SQL-Befehle oder fremde Datenbankprogramme umgehen Teile der Save-Validierung. Sie sind kein normaler Planbearbeitungsweg.

Typreferenzen werden beim Löschen geschützt; Kinddatensätze ihrer jeweiligen Hauptentität werden kaskadiert entfernt. Bestehende Runs sind kein automatisch vollständiger Ersatz für verlorene Pläne.

## Einheiten und Grenzwerte

Intern `double` in **V, A und Ω**, niemals „Millivolt als Zahl ohne Einheit“. Präfixe sind Darstellungs-/Eingabekomfort.

| Eingabe | Einheit | Gespeicherter SI-Wert |
| --- | --- | --- |
| `3.3 m` | Volt | 0.0033 |
| `250 uA` | Ampere | 0.00025 |
| `4,7 kOhm` im deutschen Editor | Ohm | 4700 |
| `1 MΩ` | Ohm | 1000000 |

Präfixe: `n`, `µ`/`μ`/`u`, `m`, `k`, `M`; Groß-/Kleinschreibung ist relevant. Ohne Präfix gilt SI. Die API arbeitet standardmäßig mit invariantem Zahlenformat; der Editor übergibt deutsche Kultur. Tausendertrennzeichen sind nicht als beliebige Zahlformatierung erlaubt.

Grenzen sind optional und einschließlich. `low > high`, nicht endliche Grenzen und nicht endliche numerische Parameter sind ungültig. Eine Messung mit NaN/Unendlich wird zu `Error` mit `Value=null`, nicht als ungültige JSON-Zahl gespeichert. Automatische Formatierung verwendet sechs signifikante Stellen; sie ist keine zusätzliche Messgenauigkeit.

## Schrittparameter

Die folgenden Ausschnitte sind jeweils **nur ParametersJson**, nicht der vollständige `TestStep`. `Name`, `DeviceKey`, Grenzen, Einheit und Breakpoint liegen daneben. JSON benutzt Dezimalpunkte, keine Kommentare, keine unbekannten/doppelten Felder und die erwarteten Typen.

### Measure – Messung

```json
{"channel":1003,"function":"VOLT:DC","range":10,"nplc":1}
```

- Gerät muss `ISwitchMultimeter` unterstützen.
- Funktionen: `VOLT:DC`, `VOLT:AC`, `CURR:DC`, `CURR:AC`, `RES`, `FRES`.
- Einheit entsprechend Volt, Ampere oder Ohm; Grenzwerte im TestStep, z. B. 3.2/3.4 V.
- `range` und `nplc` optional/null; positive Werte, profilabhängige Grenzen.
- NPLC nur bei DC und Widerstand, nicht bei AC.
- `FRES` berücksichtigt profilierte Sensekanäle; kein automatisch erfundenes Kanalpaar.

### Relay – Relais

```json
{"channel":103,"closed":true}
```

Gerät mit `ISwitchMultimeter`; schaltbarer Kanal. Digital-I/O-, Totalizer- und DAC-Kanäle sind keine Relais.

### SetPower – Versorgung

```json
{"voltage":5,"currentLimit":1,"enabled":true,"channel":1}
```

Gerät mit `IPowerSupply`; `channel` optional, Standard 1. Stromgrenze positiv; Ausgang und zulässige Spannung/Stromwerte werden gegen das Profil geprüft. Spannung allein ist noch kein zulässiger Gesamtleistungsnachweis für das Netzgerät.

### Program – Programmieren

```json
{"filePath":"{{plan.FirmwarePath}}"}
```

Gerät mit `IProgrammer`. Die Datei und das konfigurierte Programmierwerkzeug müssen am Prüf-PC verfügbar sein. Keine C#-Codegenerierung. Dateiformat und Flashparameter sind treiber-/projektspezifisch.

### Scan – Code lesen

```json
{}
```

Gerät mit `IBarcodeScanner`. Liefert Text im Schrittergebnis. Die automatische Seriennummernerfassung des Operators ist ein separater Ablauf und nicht automatisch ein Scan-Schritt des Plans.

### Wait – Warten

```json
{"durationMilliseconds":500}
```

Kein Gerät; Dauer endlich und ≥0. Abbruch bleibt möglich. Kein gewöhnlicher Geräteoperationstimeout.

### OperatorPrompt – Bedienerfrage

```json
{"message":"Leuchtet die grüne LED?"}
```

Kein Gerät; Ja → Pass, Nein → Fail. Wartet auf Bediener oder Abbruch, nicht auf den 30-s-Gerätetimeout.

### External – Fremdprogramm

```json
{"command":"C:\\TestpilotAdapters\\Check.exe","arguments":["--serial","{serial}","--order","{fauf}"]}
```

Direkter Prozessstart, keine Shell und keine `.bat`/`.cmd`. Kontextplatzhalter: `{fauf}`, `{serial}`, `{station}`, `{operator}`. Ein Argument pro Arrayelement. Exitcode 0 → Pass; anderer Exitcode → Fail mit stderr; Start-/Laufzeitexception → Error. Startet auch bei Simulation!

### CustomCode – registrierte C#-Erweiterung

```json
{"handlerKey":"Example","parameters":{"message":"Test"}}
```

`ICustomStep` muss unter exakt diesem Schlüssel im Host registriert sein. `parameters` ist das handlerspezifische JSON-Objekt. Unbekannter Handler wird vor Hardwarezugriff abgewiesen. Startet ebenfalls bei Simulation; keine automatische Assemblysuche oder Sandbox.

**Für alle Nicht-Messschritte:** keine Messgrenzen/MeasurementUnit angeben. Executor-Ausgaben werden als Text oder passender typisierter Wert gespeichert, nicht beides gleichzeitig. Ergebnistext maximal 4000 Zeichen. Die Reihenfolge ist intern lückenlos und nullbasiert: 0, 1, 2 …; UI-Anzeigen können sie lesbarer darstellen.

## Benannte Einstellungen als JSON

`SettingsJson` ist eine JSON-Liste. Auch Boolean-/Zahlwerte werden in der Liste als **String** gespeichert und beim Auflösen typisiert:

```json
[
  {"directory":"Firmware","name":"Firmwaredatei","alias":"FirmwarePath","dataType":"string","value":"C:\\Firmware\\v3\\board.bin"},
  {"directory":"Optionen","name":"Zusatzprüfung","alias":"ExtraCheck","dataType":"boolean","value":"true"}
]
```

Typen: `string`, `boolean`, `integer`, `number`. Alias `[A-Za-z_][A-Za-z0-9_]{0,99}`, ohne Beachtung der Groß-/Kleinschreibung eindeutig; Name maximal 200, Verzeichnis maximal 500 Zeichen. Werte enthalten keine verschachtelten Plan-/Stationsreferenzen.

`{{plan.Alias}}` und `{{station.Alias}}` werden in JSON-Strings rekursiv in Objekten/Arrays ersetzt, **nicht in Feldnamen**. Ein vollständiger Platzhalter erhält den deklarierten JSON-Typ; eingebettet in Text wird invariant formatiert. Fehlende oder fehlerhafte Referenzen sind explizite Fehler.

## YAML-Austauschformate

Drei verschiedene Formate nicht verwechseln:

| Format | Zweck |
| --- | --- |
| Typ-YAML | Typname, Ausstattung/Modelle/Optionen. |
| Stations-YAML | Konkreter Aufbau, Treiber/Verbindungen und SettingsJson. |
| Profil-YAML | Entwicklerseitige Gerätedaten/Kanäle/Zustandsregeln; kein Stationsimport. |

Die ersten beiden Formate verwenden camelCase, lehnen doppelte/unbekannte YAML-Felder ab und erlauben maximal 1.048.576 UTF-8-Bytes. Import legt neue Entitäten an, kein Update/Merge.

### Prüfsystemtyp

```yaml
name: DEMO
description: Hardwarefreie Beispielausstattung
devices:
  - deviceKey: dmm1
	model: SimDmm
	description: Simuliertes Multimeter
	optionsJson: '{"channels":[1003]}'
```

### Prüfsystem

```yaml
name: DEMO-1
systemType: DEMO
description: Beispielmessplatz
settingsJson: '[]'
devices:
  - deviceKey: dmm1
	driver: SimulatedSwitchMultimeter
	connectionParametersJson: '{"channels":[1003],"readings":{"1003|VOLT:DC":3.3}}'
```

`optionsJson`, `settingsJson` und `connectionParametersJson` sind **JSON-Text innerhalb von YAML**, keine frei ersetzbaren YAML-Objekte/-Listen. Bei Windows-Pfaden JSON-Escaping beachten. Stationstyp zuerst importieren. `devices.json` ist wiederum ein eigenes JSON-Adressformat; siehe [Betrieb](04-konfiguration-und-betrieb.md).

Ein allgemeiner Plan-YAML-Import/-Export ist nicht vorhanden. Die Ausführungsplanvorschau enthält nicht automatisch alle Daten für eine vollständige Rücksicherung eines Entwurfs.

## Ausführungsplan

[ExecutionPlan](../src/Testpilot.Planning/ExecutionPlan.cs) enthält:

- PlanId, PlanVersion, ArtNr, Fz.
- Devices: Gerätekennung plus Modell oder bereits gebundene Treiber-/Verbindungsdaten.
- Init: vorbereitende Messkonfigurationen.
- Steps: unveränderte Reihenfolge, Parameter, Grenzen, Einheit, Breakpoint sowie gegebenenfalls Konfiguration direkt vor dem Schritt.
- Cleanup: Safe-State- und Disconnect-Reihenfolge.
- Settings: eingefrorene Planwerte.

Über `ExecutionPlanSerializer` lesen/schreiben, nicht über beliebige Standardoptionen. Alte Datenstände und neue Eigenschaften werden dort zentral behandelt. Ein gebundener Laufplan ist nicht mit der freigegebenen, noch stationsunabhängigen Vorlage gleichzusetzen.

## Ergebnisexport

Vollständiges strukturelles Beispiel für einen simulierten Messlauf; Versionszeichenfolge/Zeiten sind Beispiele:

```json
{
  "run": {
	"fauf": "FAUF-DEMO",
	"artnr": "DEMO-001",
	"fz": "A",
	"serial": "1234567890",
	"station": "DEMO-1",
	"operator": "",
	"planVersion": 1,
	"softwareVersion": "0.1.0",
	"started": "2026-11-04T10:00:00+00:00",
	"finished": "2026-11-04T10:00:01+00:00",
	"result": "pass",
	"simulated": true
  },
  "devices": [{"id":"dmm1","idn":"Simuliertes Multimeter"}],
  "characteristics": [
	{
	  "name": "VCC_3V3",
	  "device": "dmm1",
	  "channel": 1003,
	  "function": "VOLT:DC",
	  "value": 3.3,
	  "unit": "V",
	  "low": 3.2,
	  "high": 3.4,
	  "result": "pass",
	  "started": "2026-11-04T10:00:00+00:00",
	  "finished": "2026-11-04T10:00:01+00:00",
	  "error": null
	}
  ]
}
```

- Feldname ist **`artnr`**, nicht `artNr`; übrige Namen entsprechend Beispiel.
- Resultwerte: `pass`, `fail`, `error`, bei Läufen zusätzlich `aborted`, bei Schritten `skipped`.
- Textresultate haben `textValue`; bei null wird nur dieses Feld weggelassen. Nichtmessschritte können `value`, `unit`, `channel` usw. als null enthalten.
- Alle Schritte erscheinen unter `characteristics`, nicht nur Messungen; Reihenfolge nach `Order`. Die Order selbst wird nicht exportiert.
- Geräte werden nach Gerätekennung sortiert. IDN ist die tatsächliche Identifikationsantwort beziehungsweise Simulationsidentität.
- SoftwareVersion nutzt die Assembly-Informationsversion, soweit verfügbar einschließlich Build-/Commitinformation.
- Die Lauf-ID steht im Dateinamen, nicht als zusätzliches Feld im Header.
- UTF-8, lesbare Sonderzeichen, SI-Werte und ISO-8601-Zeitangaben. Nullwerte nicht als Nullmessung interpretieren.
- Schreiben erst in temporäre Datei desselben Ordners, dann Verschieben auf den endgültigen Namen; vorhandene Exporte werden nicht überschrieben.
- Atomare einzelne Datei ist **keine gemeinsame Transaktion mit der Datenbank**.

**Quellen:** [Modelle](../src/Testpilot.Core/Models.cs), [Schrittparameter](../src/Testpilot.Core/StepParameters.cs), [Einstellungen](../src/Testpilot.Core/NamedSettings.cs), [DbContext](../src/Testpilot.Data/TestpilotDbContext.cs), [Exporter](../src/Testpilot.Core/RunJsonExporter.cs).
