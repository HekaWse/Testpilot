# 7. Optimizer und Sequencer

[Dokumentationsübersicht](README.md) · Zurück: [Datenmodell](06-datenmodell-und-formate.md) · Weiter: [Treiber und Profile](08-treiber-und-profile.md)

## Inhalt

- [Freigabe und Laufvorbereitung sind verschieden](#freigabe-und-laufvorbereitung-sind-verschieden)
- [Der deterministische Optimizer](#der-deterministische-optimizer)
- [Ablauf eines Laufs](#ablauf-eines-laufs)
- [Ergebnis-, Fehler- und Abbruchregeln](#ergebnis--fehler--und-abbruchregeln)
- [Timeouts und Debugpausen](#timeouts-und-debugpausen)
- [Simulation](#simulation)

## Freigabe und Laufvorbereitung sind verschieden

**Freigabe:** Ein noch veränderlicher Prüfplan wird gegen typbezogene Profile optimiert. Der Service friert die überprüfte Vorschau als `ExecutionPlanJson` ein. Die spätere Station muss denselben Typ und passende Geräte besitzen.

**Vorbereitung:** Der freigegebene Snapshot wird mit der aktuellen Station verbunden: konkrete Treiber/Adressen, lokale Overrides und Stationswerte. Erst dann entsteht ein ausführbarer, gebundener Plan. Es wird nicht einfach jeder inzwischen bearbeitete Entwurf ausgeführt.

`RunPreparation` prüft unter anderem:

1. Station vorhanden und Konfiguration lesbar.
2. SAP-Auftrag liefert Artikel/FZ; genau dazu ist ein freigegebener Plan vorhanden.
3. Bei gezielter Plan-ID: Plan ist freigegeben und passt zum Auftrag.
4. Snapshotidentität passt zum Datenbankplan.
5. Typ stimmt überein; alle benötigten Geräte sind eingebaut.
6. Modell, Treiber und Ausstattung sind kompatibel.
7. Plan-/Stationsreferenzen können aufgelöst werden.
8. Profile und Verbindungsparameter lassen sich erstellen und validieren.

Diese Prüfung findet vor Gerätekontakt statt, kann aber keine physische Erreichbarkeit, Firmwaredatei-Verfügbarkeit oder falsche Verdrahtung ausschließen.

Für reale Läufe dient eine erneute Optimierung gegen die gebundenen Profile der Validierung; ausgeführt wird der freigegebene Ablauf. Für Simulation wird der Ablauf gegen die ersetzten Simulationsprofile neu aufgebaut. Deshalb sind Simulations- und reale Init-/Konfigurationsaktionen nicht zwingend identisch.

## Der deterministische Optimizer

**API:** `ExecutionPlanOptimizer.Optimize(TestPlan, IReadOnlyDictionary<string, DeviceDefinition>)`.

Gleicher Plan, gleiche Profile und gleiche Konfiguration ergeben denselben fachlichen Ausführungsplan. Kein Zeitstempel/Zufall/KI beeinflusst die Auswahl der Konfigurationsaktionen. Die Optimierung tauscht **niemals die Reihenfolge der Prüfschritte**.

### Vorbedingungen

- Gültige Planidentität, Artikel/FZ und Version.
- Mindestens ein Schritt; `Order` lückenlos ab 0.
- Gültige Parameter, Einheiten/Grenzen, passende Gerätefähigkeiten.
- Profil erlaubt Kanal, Funktion und benötigten Wertebereich.
- Planreferenzen sind mit eingefrorenen Werten validierbar; Stationsbezug wird gezielt später aufgelöst.

### Algorithmus

1. Benötigte Geräte aus den Schritten sammeln und nach Gerätekennung ordinal sortieren.
2. Pro `(Gerät, Kanal)` die verschiedenen Messkonfigurationen sammeln: Funktion, Bereich und NPLC.
3. Hat der Kanal im gesamten Plan genau **eine** Konfiguration, sie in **Init** vorziehen.
4. Schritte der Reihe nach durchlaufen und den bekannten Kanalzustand verfolgen.
5. Vor einer Messung nur dann konfigurieren, wenn die benötigten Einstellungen nicht mehr sicher bekannt sind oder sich unterscheiden.
6. Nach Befehlen die im Profil beschriebenen Ungültigmachungen anwenden.
7. Abschlussliste für Safe State und Disconnect erzeugen.

„Keine Angabe“ bei Bereich/NPLC ist ein eigener Gerätestandard, nicht automatisch gleich irgendeinem zuletzt gesetzten Zahlenwert. Unbekannte Zustände werden konservativ erneut konfiguriert.

### Beispiel A: Einmalige Konfiguration

```text
Plan:
  0: Kanal 1003, VOLT:DC, Bereich 10, NPLC 1
  1: Kanal 1003, VOLT:DC, Bereich 10, NPLC 1

Init: Kanal 1003 einmal konfigurieren
Schritt 0: messen
Schritt 1: messen
```

Das gilt nur, soweit die dazwischenliegenden Befehle den Zustand nicht ungültig machen.

### Beispiel B: Wechselnde Konfiguration

```text
Plan:
  0: Kanal 1003, VOLT:DC, Bereich 10
  1: Kanal 1003, RES, Bereich 1000
  2: Kanal 1003, VOLT:DC, Bereich 10

Init: keine feste Konfiguration dieses Kanals
Schritt 0: VOLT:DC konfigurieren, messen
Schritt 1: RES konfigurieren, messen
Schritt 2: VOLT:DC konfigurieren, messen
```

### Beispiel C: Ein Befehl invalidiert Zustand

```text
Init: Kanal 1003 auf VOLT:DC konfigurieren
Schritt 0: messen
Schritt 1: Relaisbefehl -> laut Profil Messzustand unbekannt
Schritt 2: VOLT:DC erneut konfigurieren, messen
```

Auch eine in Init gesetzte Konfiguration ist nicht automatisch bis zum Laufende gültig. Profile sind damit nicht nur für Dropdownlisten relevant, sondern Teil der Ausführungslogik.

### Zustandsregeln und Sonderfälle

- `configure`, `measure`, `relay`, `setPower`, `program` und `scan` werden entsprechend den Profilregeln berücksichtigt.
- Eine Konfiguration/Messung kann auch bekannte Zustände anderer Kanäle desselben Geräts invalidieren.
- Fehlende/unbekannte Regeln führen konservativ zu erneuter Konfiguration.
- `External` und `CustomCode` können beliebige Geräte beeinflussen: Danach werden bekannte Messzustände global verworfen.
- Kanalpaare/Konflikte kommen aus den Profilen und dem Belegungsanalysator; keine vermuteten Hardwaredetails ergänzen.

## Ablauf eines Laufs

**API:**

```csharp
Task<SequenceRunResult> RunAsync(
	ExecutionPlan plan,
	RunContext context,
	CancellationToken cancellationToken = default);
```

Der konkrete Runner erhält seine Abhängigkeiten über den Host. Er ist UI-frei, aber nicht automatisch datenbankfrei: Ergebnisse werden durch den eingebundenen Daten-/Exportpfad gespeichert.

```text
Kontext / Einstellungen / Custom-Handler prüfen
  -> Run anlegen
  -> DeviceSession
	  -> Geräte in stabiler Reihenfolge erwerben und verbinden
	  -> Identifikation speichern
	  -> Init-Konfigurationen ausführen
	  -> pro Prüfschritt:
		   Debugbreak / optionale Einzelschrittpause
		   gegebenenfalls Messkonfiguration
		   passenden Executor ausführen
		   Ergebnis und Fortschritt erzeugen
  -> finally:
	  Safe State für alle erworbenen Geräte versuchen
	  Sessions/Handles schließen
  -> Gesamtergebnis bestimmen
  -> Datenbankspeicherung versuchen
  -> JSON-Export versuchen
  -> SequenceRunResult zurückgeben
```

### DeviceSession

- Genau eine Lease pro DeviceKey und Lauf.
- Erwerb in sortierter Reihenfolge verhindert widersprüchliche Sperrreihenfolgen.
- Teilweise erfolgreiche Initialisierung wird bei Fehlern aufgeräumt.
- Executor erhält benötigte Fähigkeit über `GetCapability<T>`.
- Disconnect in umgekehrter Erwerbsreihenfolge.
- Profilvalidierung ist kein Self-Test: Der normale Runner identifiziert Geräte, ruft aber nicht pauschal bei jedem Lauf `SelfCheckAsync` auf.

### Abschluss

Safe State wird für erworbene Geräte versucht, auch bei Fehler/Abbruch. Die geplante Priorität ist Versorgung → Programmer → Switch/DMM → übrige Geräte, jeweils stabil nach Gerätekennung. Danach Disconnect.

Cleanup ignoriert den bereits abgebrochenen Lauf-Token und verwendet eigene Operationstimeouts. Scheitert ein Safe State, wird der Fehler protokolliert und mit den anderen Geräten fortgefahren. Disconnect wird trotzdem versucht. Das ist **best effort**, keine Garantie, dass tatsächlich jeder Ausgang aus ist.

Bei Prozessabsturz, Stromausfall oder dauerhaft blockierendem nativen Code gibt es keinen ausführbaren `finally`-Nachweis. Dafür sind Hardwareabsicherung und Betriebsanweisungen nötig.

## Ergebnis-, Fehler- und Abbruchregeln

| Ereignis | Verhalten |
| --- | --- |
| Messung innerhalb Grenzen | Schritt Pass. |
| Grenzwert verletzt / Bediener Nein / Fremdprogramm Exitcode ≠0 | Schritt Fail, folgende Schritte laufen weiter. |
| Exception, ungültiger Messwert oder Timeout | Schritt Error, restliche Schritte Skipped. |
| Cancellation | Lauf Aborted, aktueller/übrige nicht abgeschlossene Schritte werden entsprechend dokumentiert. |
| Safe-State-/Cleanupfehler | Lauf Error, auch wenn vorher Pass/Fail/Aborted. |
| Datenbank-/Exportfehler | Unabhängig protokollieren und als `SaveError` zurückgeben; physischer Prüfentscheid wird dadurch nicht zwingend geändert. |

Gesamtpriorität: **Error > Aborted > Fail > Pass**.

`SequenceRunResult` enthält `Run`, optional `ExportPath` und optional `SaveError`. Ein aufrufendes Programm darf nicht nur `Run.Result == Pass` prüfen und einen Speicherfehler unterschlagen. Die Konsole liefert dafür Exitcode 1.

Fehler vor dem eigentlichen Lauf, beispielsweise kein freigegebener Plan oder fehlender Custom-Handler, sind Vorbereitungsfehler; es ist nicht für jeden solchen Fehler bereits ein vollständiger gespeicherter Run vorhanden.

## Timeouts und Debugpausen

- Standardoperation: 30 s; lange Operation: 600 s; konfigurierbar.
- Program, External und CustomCode verwenden den langen Timeout.
- Wait und OperatorPrompt warten auf ihre Dauer/Antwort oder Abbruch, nicht auf den normalen Geräteoperationstimeout.
- Timeouts sind grundsätzlich **kooperative Cancellation**, kein zuverlässiges Abbrechen beliebigen blockierenden Codes.
- Prozesse wie J-Flash/External/SAP-Adapter besitzen eigene Abbruch-/Prozessbeendigungslogik.
- `Debug:DisableTimeouts` entfernt Sequencer-Timeouts, nicht alle eigenständigen Treiber-/SAP-Timeouts.
- Breakpoint/Einzelschritt liegen vor dem eigentlichen Schritt. Init und Abschluss sind keine gewöhnlichen Einzelschritteinträge.

### Fortschritt und Interaktion

`ProgressChanged` liefert Reihenfolge, Gesamtanzahl, Name, Zustand und gegebenenfalls Ergebnis. Die WPF-Oberfläche marshalt zum UI-Thread. `ISequenceInteraction` trennt Weiter-/Ja-Nein-Anfragen vom Runner; Konsole und WPF implementieren dieselbe fachliche Interaktion verschieden.

## Simulation

Zuordnung anhand der tatsächlich benötigten Schritt-Fähigkeit:

| Schritte | Ersatztreiber |
| --- | --- |
| Measure/Relay | SimulatedSwitchMultimeter |
| SetPower | SimulatedPowerSupply |
| Program | SimulatedProgrammer |
| Scan | SimulatedBarcodeScanner |

Verwendete Kanäle werden in das Simulationsprofil aufgenommen. Verbindungsparameter wie vorgegebene Messwerte bleiben nur erhalten, wenn bereits genau derselbe simulierte Treiber gebunden war; reale Verbindungsparameter werden nicht blind übernommen.

Mehrdeutige Kombinationen verschiedener Fähigkeiten unter einer Gerätekennung oder Geräte, deren Simulation nur aus einem Custom-Schritt abgeleitet werden müsste, werden ausdrücklich abgewiesen. Nicht jeder beliebige reale Plan ist ohne Anpassung simuliert ausführbar.

**Simulation ist keine Sandbox:** `External`, `CustomCode`, Dateizugriffe und die gewählte SAP-Anbindung bleiben real. Eigene Handler müssen Simulation bewusst respektieren, wenn sie Nebenwirkungen vermeiden sollen.

**Quellen:** [Optimizer](../src/Testpilot.Planning/ExecutionPlanOptimizer.cs), [Settings-Auflösung](../src/Testpilot.Planning/ExecutionPlanSettings.cs), [Vorbereitung](../src/Testpilot.Sequencer/RunPreparation.cs), [Runner](../src/Testpilot.Sequencer/SequenceRunner.cs), [Executors](../src/Testpilot.Sequencer/StepExecutors.cs), [Session](../src/Testpilot.Sequencer/DeviceSession.cs).
