## Rahmenbedingungen
- Aktuelle .NET-LTS-Version, C# mit `nullable enable`, Visual-Studio-Projektmappe `Testpilot.sln`. Zielplattform Windows, Entwicklung in Visual Studio.
- Code, Bezeichner und XML-Doku auf Englisch. Texte in der Oberfläche auf Deutsch.
- EF Core für die Datenbank: SQLite als Standard, Provider und Connection-String nur über Konfiguration austauschbar (später SQL Server oder PostgreSQL). Migrationen einchecken.
- Tests mit xUnit. Logging über `Microsoft.Extensions.Logging` mit Serilog-Dateiausgabe.
- Alles muss ohne echte Hardware lauffähig und testbar sein (simulierte Treiber, SAP-Mock).
- Das Team arbeitet sonst in C, C# und C++: einfacher, gut lesbarer Code, wenig Magie, keine unnötigen Abstraktionen.

## Konzept
Ein **Prüfplan** wird in einer Blazor-Webanwendung (ohne Gerätezugriff) zusammengeklickt und als Daten in der Datenbank gespeichert. Beim Freigeben wandelt ein **deterministischer Optimizer** den Plan in einen **Ausführungsplan** um (Init, Schritte, Abschluss). Der **Sequencer** (C#, mit Gerätezugriff) führt den Ausführungsplan aus. Es wird kein C#-Code erzeugt. Keine KI im Programm.

## Projektmappe
```
Testpilot.sln
  src/Testpilot.Core                # Domänenmodell, Einheiten, Grenzwertprüfung, Plan-Lookup, JSON-Export
  src/Testpilot.Data                # EF Core DbContext, Entitäten, Migrationen
  src/Testpilot.Drivers.Abstractions# Interfaces, DeviceProfile, Profil-Loader (KEINE Hardware-Abhängigkeit)
  src/Testpilot.Drivers             # Treiber-Implementierungen, Registry
  src/Testpilot.Planning            # Optimizer: Prüfplan -> Ausführungsplan (rein, keine Hardware)
  src/Testpilot.Sap                 # ISapClient, Mock, Adapter für das bestehende Programm
  src/Testpilot.Sequencer           # Engine, die den Ausführungsplan ausführt (Step-Executoren, DeviceSession, Ergebnisse)
  src/Testpilot.Editor              # Blazor Web App (Interactive Server), MudBlazor, OHNE Gerätezugriff
  tools/Testpilot.RunPlan           # Konsolen-Hilfsanwendung zum Ausführen eines Plans (Debuggen, Tests am Schreibtisch)
  tests/Testpilot.Tests
```
Der Editor darf nur `Testpilot.Drivers.Abstractions`, `Planning`, `Core` und `Data` referenzieren, nie `Testpilot.Drivers`. Eine passende `.gitignore` für Visual Studio anlegen.

## Treiberschicht
- `IInstrument` (ConnectAsync, DisconnectAsync, IdentifyAsync, SelfCheckAsync, SafeStateAsync, `DeviceProfile Profile`).
- Fähigkeiten als Interfaces: `ISwitchMultimeter`, `IPowerSupply`, `IProgrammer`, `IBarcodeScanner`.
- `DeviceProfile`: Fähigkeiten, Kanäle mit Typ, unterstützte Messfunktionen, Wertebereiche und **Zustandsinformation**: welche Einstellungen pro Kanal existieren (Funktion, Bereich, NPLC usw.) und welche Befehle welchen Zustand ungültig machen (`invalidates`). Unbekanntes wird konservativ behandelt, also erneut konfiguriert.
- Treiber-Registry: Konfiguration enthält z.B. `Driver: "Ag34980Scpi"`. Pro Gerät sind mehrere Treiber möglich (DLL- und SCPI-Variante austauschbar).
- Geplante Treiber: `Simulated*` (zuerst), `VisaGeneric` (Ivi.Visa hinter eigener Abstraktion), `Ag34980Scpi`, `Ag34980Dll` und `HmpDll` (P/Invoke-Wrapper für vorhandene C-DLLs, **nur Gerüst** mit TODOs für DLL-Pfad, Aufrufkonvention, Signaturen), `HmpScpi`, `SeggerJFlashCli` (Prozessaufruf), `SensopartTcp` (TCP, Protokoll als TODO).
- Modulprofile des 34980A (z.B. 34921A, 34937A) als YAML-Datendateien: Anzahl Kanäle, Kanaltyp, erlaubte Funktionen, Sonderfälle (Stromkanäle, 4-Draht-Kanalpaare), Zustandsinfos. Lege das Format an und befülle zwei Beispielmodule. Alle Werte, die du nicht sicher kennst, mit `TODO: aus Datenblatt prüfen` markieren. Erfinde keine Daten.
- Kanalnummerierung beim 34980A: Slot·100 + Kanal (z.B. 1003).
- Treiber laufen im selben Prozess hinter `IDriverHost` (später auslagerbar, aber nicht jetzt implementieren). Pro Gerät höchstens ein Zugriff gleichzeitig (`SemaphoreSlim`).

## Einheiten
Intern alles in SI-Basiseinheiten als `double` (V, A, Ω). Präfixe (n, µ, m, k, M) nur in der Oberfläche. Parse- und Formatierfunktionen (`"3.3 m"` + Volt → 0.0033) mit Tests.

## Datenmodell (EF Core)
- `Station`, `StationDevice` (DeviceKey, Treiber, Verbindungsparameter).
- `TestPlan`: `ArtNr`, `Fz`, `Version`, `Status` (Draft / Released / Retired), `ExecutionPlanJson` (wird beim Freigeben erzeugt und eingefroren), Zeitstempel. Pro (`ArtNr`, `Fz`) höchstens EINE freigegebene Version: gefilterter Unique-Index (Filtersyntax providerabhängig, deshalb zusätzlich in der Service-Logik prüfen und testen). Freigegebene Pläne sind nicht editierbar, stattdessen „neue Version daraus erstellen".
- `TestStep`: Reihenfolge, Name, StepType, `DeviceKey` (ohne Fremdschlüssel), Parameter (JSON, je StepType validiert), `LimitLow`/`LimitHigh`, Einheit, `BreakpointFlag`.
- `Run`: FAUF, ArtNr, Fz, Seriennummer, Messplatz, Bediener, Planversion, Start/Ende, Gesamtergebnis, Softwareversion.
- `RunStepResult`: Messwert, Ergebnis (Pass / Fail / Error / Skipped), **Snapshot von Grenzwerten und Einheit**, Zeitstempel.

Schritttypen: `Measure` (Gerät, Kanal, Funktion VOLT:DC / VOLT:AC / CURR:DC / CURR:AC / RES / FRES, Bereich, NPLC, Grenzwerte), `Relay`, `SetPower`, `Program`, `Scan`, `Wait`, `OperatorPrompt`, `External` (bestehendes Programm aufrufen), `CustomCode` (ruft eine registrierte C#-Klasse `ICustomStep` auf, für Sonderfälle).

## Optimizer (Testpilot.Planning)
Eingabe: Prüfplan (Liste von Schritten). Ausgabe: `ExecutionPlan` mit drei Phasen.
- **Init:** alle benötigten Geräte einmal verbinden (ein Handle pro Gerät), Kanäle konfigurieren, die im gesamten Plan nur eine einzige Konfiguration haben.
- **Schritte:** Gehe die Schritte der Reihe nach durch und verfolge den Konfigurationszustand jedes Kanals. Konfiguriere direkt vor einer Messung nur dann, wenn sich der Zustand gegenüber dem bekannten Zustand ändert (auch wenn Befehle laut Geräteprofil Zustand ungültig machen).
- **Abschluss:** `SafeStateAsync` aller Geräte (Relais öffnen, Ausgänge aus), danach alle Handles schließen. Läuft immer, auch bei Fehler oder Abbruch.
- Rein deterministisch, ohne Zufall und ohne KI, vollständig mit xUnit-Tests abgedeckt (Beispiele: eine Konfiguration pro Kanal → Init, zwei Konfigurationen → vor der Messung, Zustand ungültig durch Befehl → neu konfigurieren).
- Der Editor zeigt eine Vorschau des Ausführungsplans (Init / Schritte / Abschluss), bevor man freigibt.

## Sequencer (Testpilot.Sequencer)
- `SequenceRunner.RunAsync(ExecutionPlan, RunContext, CancellationToken)`: UI-frei, aufrufbar aus Tests, Konsolenanwendung und später einer Bediener-Oberfläche. Fortschrittsereignisse für die Oberfläche.
- Pro Schritttyp ein kleiner, gut lesbarer Executor (`MeasureStepExecutor` usw.). Pro Lauf eine `DeviceSession` mit einem Handle pro Gerät.
- SAP: `ISapClient.GetOrderAsync(fauf)` → `OrderInfo(ArtNr, Fz)`, `MockSapClient`, `ExternalProgramSapClient` (konfigurierbares Kommandozeilenprogramm, JSON von stdout, Platzhalter). Nur lesend. Klare Exception-Typen.
- Nach dem Lauf: Run und Ergebnisse in die Datenbank schreiben und JSON in einen konfigurierbaren Ordner exportieren. Softwareversion (Assembly-Version, wenn möglich mit Git-Commit-Hash) in `Run` und JSON.

### JSON-Export
```json
{
  "run": {"fauf": "...", "artnr": "...", "fz": "...", "serial": "...",
          "station": "...", "planVersion": 3, "softwareVersion": "...",
          "started": "ISO-8601", "finished": "ISO-8601", "result": "pass"},
  "devices": [{"id": "dmm1", "idn": "..."}],
  "characteristics": [
    {"name": "VCC_3V3", "device": "dmm1", "channel": 1003, "function": "VOLT:DC",
     "value": 3.298, "unit": "V", "low": 3.2, "high": 3.4, "result": "pass"}
  ]
}
```

## Debugbarkeit (wichtige Anforderung)
Ich muss Tests mit Breakpoints debuggen und Variablen sowie Rückgabewerte ansehen können, auch auf einem Testrechner ohne Visual Studio (Remote-Debugger von Visual Studio, kein Sondercode nötig).
- `BreakpointFlag` pro Schritt: Der Sequencer ruft vor diesem Schritt `Debugger.Break()` auf, wenn `Debugger.IsAttached` ist, sonst nichts.
- Einzelschrittmodus: pausiert vor jedem Schritt, bis „Weiter" gesendet wird (über die Konsole in `Testpilot.RunPlan`, später in der Oberfläche).
- Konfigurationsschalter `Simulation:Enabled` ersetzt alle Treiber durch simulierte, `Debug:DisableTimeouts` schaltet Timeouts ab.
- Jeder Gerätebefehl wird mit Befehl, Antwort und Dauer geloggt (Trace-Datei pro Lauf, z.B. über einen Decorator um den Treiber).
- Ergebnisobjekte als `record` mit lesbarer `ToString()`.
- `tools/Testpilot.RunPlan`: Plan-ID und Station als Argumente, simulierte Treiber als Standard.
- README mit Anleitung: Veröffentlichen (`win-x64`, optional `win-x86` für 32-Bit-DLLs) und Remote-Debugger einrichten.

## Editor (Testpilot.Editor)
- Messplätze und Geräte anlegen und bearbeiten, Treiber wählen. Import und Export als YAML.
- Prüfpläne anlegen: Artikelnummer + Fertigungszustand, Schritte hinzufügen und sortieren. Bei Messschritten: Gerät → Kanal (aus dem Geräteprofil) → Messgröße, Einheit, Bereich → Grenzwerte mit Einheitenpräfix. Beim Anlegen einer Messung muss der Kanal konfiguriert werden (Messgröße, Einheit usw.).
- Kanalbelegung anzeigen, Konflikte warnen (inklusive 4-Draht-Kanalpaare). Breakpoint-Flag pro Schritt.
- Status-Workflow Entwurf → freigegeben → zurückgezogen, mit Vorschau des Ausführungsplans vor der Freigabe.

## Phasen
1. **Gerüst und Kern:** Projektmappe, Projekte, Konfiguration, EF-Core-Modelle + erste Migration, Einheitenmodul, Grenzwertprüfung, Plan-Lookup (inkl. Regel „eine freigegebene Version pro ArtNr/Fz"), JSON-Export, xUnit-Tests, README mit Startanleitung.
2. **Treiberschicht:** Abstractions, Registry, `DeviceProfile` mit Zustandsinfos, simulierte Treiber, Modulprofil-Format + zwei Beispielmodule, `IDriverHost`, Gerätesperre, Trace-Logging, Tests.
3. **Optimizer und Sequencer-Engine:** Ausführungsplan, Optimizer mit Tests, Step-Executoren, `DeviceSession`, SAP-Mock, Debug-Funktionen, `Testpilot.RunPlan`.
4. **Editor-Webanwendung.**
5. **Echte Treiber:** `VisaGeneric`, `Ag34980Scpi`, `HmpScpi`, `SeggerJFlashCli`, `SensopartTcp` und die DLL-Gerüste.
6. **Bediener-Oberfläche des Sequencers** (Technologie wird noch entschieden, nicht jetzt umsetzen).

Beginne jetzt mit Phase 1. Zeige am Ende eine kurze Zusammenfassung, was fertig ist und was ich prüfen soll.