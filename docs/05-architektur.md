# 5. Softwarearchitektur

[Dokumentationsübersicht](README.md) · Weiter: [Datenmodell und Formate](06-datenmodell-und-formate.md)

## Inhalt

- [Leitentscheidungen](#leitentscheidungen)
- [Projekte und Abhängigkeiten](#projekte-und-abhängigkeiten)
- [Datenfluss vom Entwurf zum Ergebnis](#datenfluss-vom-entwurf-zum-ergebnis)
- [Wichtige Klassen und Lebensdauern](#wichtige-klassen-und-lebensdauern)
- [Grenzen und Verantwortlichkeiten](#grenzen-und-verantwortlichkeiten)

## Leitentscheidungen

- **Pläne sind Daten.** Kein erzeugter C#-Code und keine Laufzeitkompilierung.
- **Planung ist rein.** Der Optimizer benötigt Plan und Profile, aber keine Hardware und keine Zufallswerte.
- **Ausführung ist UI-frei.** `SequenceRunner` ist aus WPF, Konsole und Tests nutzbar.
- **Editor ohne Hardware.** Gerätebeschreibungen liegen in einer eigenständigen Abstraktionsbibliothek.
- **Konkrete, lesbare Implementierungen.** Kleine Executor-Klassen pro Schritttyp; registrierte Fähigkeiten statt eines universellen undurchsichtigen Befehlsinterpreters.
- **Versionen und Snapshots.** Freigegebener Ablauf wird eingefroren; Ergebnisse behalten Grenzwerte und Einheit.
- **Fehler sichtbar machen.** Ungültige Konfiguration führt zu expliziten Fehlern, nicht zu Erfolg mit Ersatzwerten.
- **Keine Laufzeit-KI.** Entscheidungen sind durch Code, Daten und Profile nachvollziehbar.

## Projekte und Abhängigkeiten

Die Projektmappen liegen im Repository-Hauptverzeichnis. Für den Einstieg ist `Testpilot.slnx` vorgesehen; `Testpilot.sln` ist ebenfalls vorhanden.

| Projekt | Verantwortung | Direkte Projektabhängigkeiten |
| --- | --- | --- |
| [Testpilot.Core](../src/Testpilot.Core) | Domänenmodell, Parameterverträge, Einheiten/Grenzen, Einstellungen, Export, Versionsinformation | keine |
| [Testpilot.Drivers.Abstractions](../src/Testpilot.Drivers.Abstractions) | Instrument-/Fähigkeitsinterfaces, Geräte-/Modulprofile, Katalog, Adressformate; ohne Hardwarezugriff | keine |
| [Testpilot.Planning](../src/Testpilot.Planning) | ExecutionPlan, Serialisierung, Einstellungsauflösung, Optimizer | Core, Drivers.Abstractions |
| [Testpilot.Data](../src/Testpilot.Data) | EF Core, Provider, Migrationen, Plan- und Debugbenutzerdienste, Seed | Core, Planning |
| [Testpilot.Drivers](../src/Testpilot.Drivers) | Registry/Host, Simulation, SCPI/VISA/Prozess/TCP/seriell, Tracing | Drivers.Abstractions |
| [Testpilot.Sap](../src/Testpilot.Sap) | ISapClient, Mock, REST und externer Adapter | Core |
| [Testpilot.Sequencer](../src/Testpilot.Sequencer) | Vorbereitung, Gerätebindung, Session, Executors, Runner und Fortschritt | Core, Data, Drivers.Abstractions, Drivers, Planning, Sap |
| [Testpilot.Editor](../src/Testpilot.Editor) | Blazor/MudBlazor, Entwürfe, Ausstattung, Vorschau, Freigabe | Core, Data, Planning, Drivers.Abstractions |
| [Testpilot.Operator](../src/Testpilot.Operator) | WPF-Bedienung, Auftrag/Seriennummer, Adapterautomat, Debug-Login | Sequencer, Drivers, Sap, Data |
| [Testpilot.RunPlan](../tools/Testpilot.RunPlan) | Konsolenhost für Ausführung/Einzelschritt | Sequencer, Drivers, Sap |
| [Testpilot.Tests](../tests/Testpilot.Tests) | xUnit-Tests aller Bereiche einschließlich WPF-naher Logik | alle Anwendungs-/Bibliotheksprojekte |

Die Testassembly zielt wegen der WPF-Referenz auf Windows. Ein hardwarefreier Testlauf benötigt trotzdem Windows, nicht nur ein beliebiges .NET-fähiges Betriebssystem.

### Zentrale Abhängigkeitsgrenze

```text
Browser -> Editor -> Data / Planning -> Core
			 |              |
			 +------> Drivers.Abstractions

WPF / Konsole -> Sequencer -> DriverHost -> Registry -> Instrument
					|                         |
					+-> Data / SAP            +-> echte oder simulierte Treiber
```

Der Editor darf **nie** `Testpilot.Drivers` oder den Sequencer zur Ausführung referenzieren. Ein neues Geräteprofil muss deshalb ohne Konstruktion eines echten Treibers verfügbar bleiben. Änderungen an dieser Grenze erfordern eine bewusste Architekturentscheidung, keine beiläufige Projektreferenz.

## Datenfluss vom Entwurf zum Ergebnis

### 1. Stammdaten

`TestSystemType` definiert Modelle und Module. `Station` verweist auf einen Typ und enthält konkrete `StationDevice`-Einträge. Plan und Station treffen sich über Typ und `DeviceKey`, nicht über einen Fremdschlüssel vom einzelnen Prüfschritt zum physischen Gerät.

### 2. Planung und Freigabe

1. Editor speichert `TestPlan` und geordnete `TestStep`-Daten.
2. Profile bestimmen zulässige Kanäle/Funktionen und Konflikte.
3. `ExecutionPlanOptimizer` erzeugt die Vorschau.
4. `TestPlanService` kontrolliert Entwurf, Identität, Schritte, Einstellungen, Typbindung und bestehende Freigaben.
5. Serialisierter Ausführungsplan und Freigabezeit werden unveränderlich gespeichert.

Das Einfrieren sichert den geplanten Ablauf und die Planwerte. Aktuelle Treibersoftware, Dateien hinter Pfaden und spätere Stationskonfiguration sind nicht automatisch derselbe unveränderliche Snapshot.

### 3. Vorbereitung am Messplatz

1. SAP liefert Artikel/FZ für die FAUF.
2. Freigegebenen Plan exakt über diesen Schlüssel laden.
3. Station/Typ und Einbau prüfen; gegebenenfalls lokale VISA-Adressen übernehmen.
4. Modelle auf konkrete Treiber/Verbindungen abbilden.
5. Plan- und Stationsreferenzen auflösen.
6. Bei Simulation Treiber ersetzen.
7. Verbindungen/Profile prüfen, bevor Geräte geöffnet werden.

Für moderne typbezogene Pläne wird die Bindung an die konkrete Station zur Laufzeit vorgenommen. Ältere Pläne ohne Modellbindung können bereits feste Treiber/Verbindungen im Snapshot enthalten; diese werden nicht einfach durch Stationswerte ersetzt. Bei Änderungen deshalb wissen, welche Planart vorliegt.

### 4. Ausführung und Nachweis

`SequenceRunner` erstellt den Lauf und eine `DeviceSession`, führt Init und Schritte aus, fordert immer den Abschluss an und gibt Ressourcen frei. Anschließend werden Datenbankspeicherung und Export getrennt versucht. Logs liefern technische Details neben den Ergebnisdaten.

## Wichtige Klassen und Lebensdauern

| Baustein | Aufgabe / Lebensdauer |
| --- | --- |
| `TestpilotDbContext` | Eine kurze Datenbankaktion beziehungsweise ein Scope. Nicht threadübergreifend verwenden. |
| `IDbContextFactory<TestpilotDbContext>` im Editor | Neuer Context pro Benutzeraktion, nicht ein Context für den gesamten langlebigen Blazor-Circuit. |
| `TestPlanService` | Konsistente Freigabe, Rückzug, neue Version und freigegebener Lookup. |
| `NamedSettingsResolver` | JSON-bewusste Auflösung; vor Geräteoperationen, nicht während eines laufenden Befehls. |
| `ExecutionPlanOptimizer` | Zustandslose fachliche Transformation; keine Gerätesession. |
| `RunPreparation` | Liefert `PreparedRun` für Auftrag, Station und ausführbaren Plan. |
| `DriverRegistry` | Name → Factory; validiert Profil und tatsächliche Fähigkeitsinterfaces. |
| `DriverHost` | Beschafft Lease mit Sperre, verbindet und dekoriert Instrument. |
| `DeviceSession` | Ein Handle/Lease pro verwendetem Gerät innerhalb eines Laufs. |
| `StepExecutorRegistry` | Schritttyp → kleiner Executor. |
| `SequenceRunner` | Ablauf, Timeout-/Abbruchbehandlung, Fortschritt, Abschluss und Speichern. |
| `ISequenceInteraction` | Hostabhängige Einzelschritt-/Ja-Nein-Interaktion ohne UI im Runner. |
| `MainViewModel` | WPF-Zustand und Befehle; übernimmt Fortschritt in den UI-Kontext. |
| `OperatorRunService` | Datenbankscope und Hintergrundarbeit pro Vorbereitung/Lauf; separater Lauf-Logger. |

Fortschrittsereignisse laufen auf dem ausführenden Thread. Ein neuer Host muss selbst auf seinen UI-Thread marshallen und darf im Event keine schwergewichtige/blockierende Arbeit ausführen.

Der Adapter kann im Bedienprogramm über mehrere Läufe gehalten werden. Der feste Code-Leser wird dagegen kurzzeitig benutzt, damit spätere Scan-Schritte nicht dauerhaft ausgesperrt werden.

## Grenzen und Verantwortlichkeiten

### Nebenläufigkeit

- Gerätezugriff ist pro exakter Gerätekennung **innerhalb eines Prozesses** gesperrt.
- Keine verteilte Sperre zwischen zwei Operator-Prozessen, PCs oder verschiedenen Kennungen derselben Hardware.
- Gleiche physische Verbindung unter verschiedenen Kennungen ist nicht automatisch geschützt. Bei PS05 wird die gemeinsame 34970-Verbindung von Adapter- und Messlogik gezielt sequenziert; daraus keinen allgemeinen Parallelzugriff ableiten.
- Datenbank-Unique-Indizes und Concurrency-Tokens schützen fachliche Planänderungen auch gegen konkurrierende Editoraktionen.

### Integration und Vertrauen

- `ISapClient` ist lesend, keine Buchungsschnittstelle.
- `ICustomStep` ist registrierter C#-Code im Prozess, kein frei eingegebenes Skript.
- `External` startet ein vertrauenswürdiges Programm direkt. Die erlaubten Windows-Rechte sind die des Testpilot-Prozesses.
- `IDriverHost` ist eine Auslagerungsgrenze für später; es gibt derzeit keinen Treiberservice und kein IPC-Protokoll.
- `Externes` enthält Altcode und native Referenzartefakte. Deren Vorhandensein bedeutet nicht, dass sie bereits als aktive Implementierung eingebunden sind.

### Reproduzierbarkeit

Für spätere Nachvollziehbarkeit zusammen aufbewahren: freigegebener Plan, Softwareversion, passende Profile/Treiber, Station und lokale Overrides, Firmware-/Projektdateien und Ergebnisdaten. Der Laufexport allein enthält **keinen vollständigen Aufbau- und Einstellungs-Snapshot**.

**Nächste Quellen:** [Models](../src/Testpilot.Core/Models.cs), [ExecutionPlan](../src/Testpilot.Planning/ExecutionPlan.cs), [RunPreparation](../src/Testpilot.Sequencer/RunPreparation.cs), [SequenceRunner](../src/Testpilot.Sequencer/SequenceRunner.cs).
