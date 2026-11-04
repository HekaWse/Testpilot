# 10. Fehlersuche und Debugging

[Dokumentationsübersicht](README.md) · Zurück: [Entwicklung](09-entwicklung-und-erweiterung.md)

## Inhalt

- [Zuerst sicher und systematisch vorgehen](#zuerst-sicher-und-systematisch-vorgehen)
- [Symptom → Ursache → nächste Prüfung](#symptom--ursache--nächste-prüfung)
- [Protokolle und Supportdaten](#protokolle-und-supportdaten)
- [Lokal debuggen](#lokal-debuggen)
- [Remote-Debugging ohne Visual Studio am Prüf-PC](#remote-debugging-ohne-visual-studio-am-prüf-pc)
- [Häufige Verständnisfragen](#häufige-verständnisfragen)

## Zuerst sicher und systematisch vorgehen

1. Bei Hardwareproblemen Prüfling und Ausgänge gemäß Arbeitsanweisung sichern. Nicht durch wiederholtes Starten unklare Zustände verstärken.
2. Messplatz, Simulation/real, FAUF, Seriennummer, Planversion und Softwareversion notieren.
3. Unterscheiden: Fehler **vor** Laufstart, fachliches n.i.O., technischer Schrittfehler, Abschlussfehler oder Speicher-/Exportfehler.
4. Zuerst die **erste ursächliche Fehlermeldung** suchen. Spätere Folgefehler können lediglich den Verbindungsabbruch beschreiben.
5. Änderungen einzeln und nachvollziehbar testen; keine produktive Datenbank löschen oder freigegebenen Pläne per SQL „entsperren“.

## Symptom → Ursache → nächste Prüfung

| Symptom | Typische Ursache / gezielte Prüfung |
| --- | --- |
| Editor wirkt nach Auswahl eingefroren | Browser-Serververbindung/Circuit ausgefallen. Editorlog zum Zeitpunkt prüfen, nicht nur Browserkonsole. Server erreichbar? Seite neu laden; wiederholbaren Fehler mit ausgewählter Station/Plan dokumentieren. |
| Benannte Einstellungen zeigen Fehlermeldung | Gespeichertes JSON oder Typ/Alias ungültig. Form bleibt absichtlich verborgen. Verantwortlicher prüft den Originaldatensatz, korrigiert gezielt und lädt neu; nicht durch leere Liste ersetzen. |
| JsonException mit Zeichen `s` bei Einstellungen | Entwickler: Stringparameter wurde möglicherweise als Literal statt Razor-Ausdruck übergeben. Binding muss z. B. `SettingsJson="@selectedStation.SettingsJson"` sein. |
| SignalR-/RemoteJSRuntime-Exception nach Editorfehler | Kann Folge eines bereits beendeten Blazor-Circuits sein. Im Serverlog nach der früheren Exception suchen. |
| `no such table` / ausstehende Migrationen | Falsche/neue Datenbank oder Schema nicht aktualisiert. Absoluten Connection-String und `--migrate` prüfen; vorher sichern. |
| Editor zeigt Pläne, Operator findet keine | Unterschiedliche Datenbank/Arbeitsverzeichnisse oder keine freigegebene Version für exakt Artikel/FZ. Hosts auf denselben absoluten Pfad konfigurieren. |
| FAUF nicht gefunden | SAP-Mock kennt sie nicht oder REST liefert „nicht gefunden“. Wirksame Auswahl BaseUrl → Command → Mock prüfen. |
| Simulation greift trotzdem auf SAP zu | Beabsichtigt getrennte Konfiguration. Simulation ersetzt Geräte, nicht SAP. BaseUrl und Command leeren und MockOrders setzen. |
| Kein freigegebener Plan | ArtNr/FZ aus SAP mit Datenbank vergleichen. Entwurf/zurückgezogene Version gilt nicht als Ersatz. |
| Neue Version lässt sich nicht freigeben | Andere Version noch freigegeben, veraltete Vorschau, Profilwarnung oder Kanalkonflikt. Fachlich prüfen und kontrolliert umschalten. |
| Änderungskonflikt beim Speichern | Anderer Bearbeiter oder parallele Freigabe. Neu laden, eigenen Änderungswunsch mit aktuellem Stand abgleichen. |
| Gerät fehlt / falscher Typ | Plan- und Stationstyp, DeviceKey-Schreibweise und Einbaustatus prüfen. Keine Hardware-Fremdschlüssel im Schritt erwarten. |
| Model passt nicht | Beispielsweise HMP2030 im Typ, aber model HMP4040 in Verbindungsparametern. Daten und reales IDN-Modell vergleichen. |
| Kanal 1003/103 ungültig | 34980A und 34970A verwechselt oder Slot/Modul falsch. Nummerierungsfaktor 1000 versus 100 beachten. |
| Messung 0 in Simulation | Kein expliziter `readings`-Wert oder beim Austausch eines realen Treibers nicht übernommen. Simulation erzeugt keine automatischen Gutwerte. |
| Einstellungsalias nicht gefunden | Richtiger Scope plan/station? Alias dort gespeichert? Schreibweise, geschweifte Klammern und Datentyp prüfen. Operator nach lokalen Einstellungsänderungen neu starten. |
| JSON-/YAML-Import schlägt fehl | Dezimalpunkt/Anführungszeichen, Pflichtfelder, doppelte/unbekannte Felder und Größenlimit prüfen. Typ zuerst importieren; Namen müssen neu sein. |
| COM-Port lässt sich nicht öffnen | Portname, Rechte, Gerätestatus und anderer Prozess/Reader-Tool prüfen. Handscanner im Tastaturmodus ist kein serieller Treiber. |
| VISA-Provider fehlt | NuGet-Paket ist keine VISA-Runtime. Vendor-VISA mit VISA.NET und passende Architektur installieren; Ressource in Connection Expert prüfen. |
| `NativeDriverScaffoldException` | DLL-Treiber ist noch Gerüst. SCPI-/Simulationstreiber verwenden oder DLL-Implementierung fachgerecht fertigstellen. |
| Code-Leser meldet ungültige Seriennummer | Leser liefert derzeit erwartete zehn Ziffern nicht, Trigger/Port/Readerjob falsch. Operatorregex ändert nicht automatisch das Readerprotokoll. |
| Adapter startet nicht | Kein Auftrag/Plan, Adapter noch nicht offen gesehen, fehlende Seriennummer oder falsche Fixture-Konfiguration. Statushinweis lesen. |
| Debug-Login scheitert | Kürzel/Passwort prüfen; Berechtigung unter Debug-Berechtigungen anlegen/resetten. Das ist keine Windows-Anmeldung. |
| Breakpoint-Flag hält nicht | Kein Debugger angehängt. Für Bedienpause Einzelschritt verwenden. |
| Prüfung hängt trotz Timeout | Fremdcode/nativer Aufruf respektiert Cancellation nicht, Bedienerfrage wartet oder Debugger hält. Timeouts sind kein erzwungenes Threadende. |
| Gutprüfung, aber Speicherwarnung | Ergebnis und Persistenz getrennt. Pfade, Rechte, freien Platz, DB-Verbindung/Locks prüfen. Kein automatischer Nachversand. |
| JSON-Datei schon vorhanden | Exporte werden absichtlich nicht überschrieben; Wiederexport derselben Run-ID ist kein normaler neuer Lauf. Ursache klären, Datei nicht unbesehen löschen. |

## Protokolle und Supportdaten

### Wo sind die Dateien?

| Prozess | Standard |
| --- | --- |
| Editor | `logs\testpilot-<Datum>.log`, aus Serilog-Konfiguration. |
| Operator Anwendung | `Logging:Directory`, tägliche `operator-…`-Dateien. |
| Operator Lauf | Im Logordner `run-<Zeit>-<bereinigte Seriennummer>.log`, inklusive Geräteaktionen. |
| RunPlan | Eigene Datei pro Prozess mit Zeit/PID im Logordner. |
| Ergebnisexport | `Export:Directory`, Dateiname `<RunId:N>.json`. |

Relative Ordner hängen vom Arbeitsverzeichnis ab. Ein leerer Ordner neben der EXE heißt nicht automatisch, dass keine Logs geschrieben wurden. Editor-Logpfad und Operator-Logging-Schlüssel sind verschieden.

### Bei einer Fehlermeldung weitergeben

- Softwareversion/Build und Windows-/Prozessarchitektur.
- Messplatz, FAUF, Artikel/FZ, Seriennummer und Planversion.
- Simulation/real und verwendete Treibernamen.
- Genaue Uhrzeit, reproduzierbare Bedienfolge, vollständige **erste** Fehlermeldung.
- Betroffenes Laufprotokoll, Anwendungslog und gegebenenfalls JSON-Export.
- Relevante Konfiguration/Stationsdaten, aber Zugangsdaten und personenbezogene Inhalte vorher entfernen.
- Bei Hardware: Modell/IDN, Firmware, Module, Ressource und letzter erfolgreicher Befehl.

Logs können Produktionsdaten, Pfade, Seriennummern und Geräteantworten enthalten. Nur über freigegebene interne Wege austauschen. Datenbankkopien enthalten zusätzlich Benutzer-Hashes und gesamte Historie.

## Lokal debuggen

### Anwendung

1. Passendes Startprojekt wählen: Editor, Operator oder RunPlan.
2. Zuerst gegen getrennte Demo-Datenbank und Simulation starten.
3. Breakpoints in `RunPreparation`, `SequenceRunner` oder dem konkreten Executor/Treiber setzen.
4. Einen freigegebenen, kurzen Plan ausführen.
5. Parameter, gebundenen Plan, `RunContext`, Gerätekonfiguration und gespeichertes Executor-Ergebnis untersuchen.

Für Messprobleme erst Rückgabe des Treibers, dann Grenzprüfung, dann `RunStepResult` vergleichen. Einen Gerätebefehl nicht im Watchfenster wiederholt aufrufen: Das wäre eine neue Hardwareaktion, keine Beobachtung des alten Rückgabewerts.

### Unit-Test

Im Test Explorer gezielt **Debuggen** auf dem Test wählen. Nicht den Test als normales Startup-Projekt starten. Hardwarefreie Tests erlauben dieselben Breakpoints und Variablenansichten. Bei Deadlocks UI-Thread, Task-Wartekette und Cancellation untersuchen, statt nur Timeoutwerte zu erhöhen.

### Pausen und Timeouts

Bei langen Debugpausen kann kontrolliert `Debug:DisableTimeouts=true` helfen. Separat vorhandene Treiber-/SAP-Timeouts bleiben aktiv. Nach der Untersuchung wieder zurücksetzen. Während Pausen können Geräteausgänge eingeschaltet bleiben.

## Remote-Debugging ohne Visual Studio am Prüf-PC

Am Zielrechner ist kein Sondercode nötig; die .NET-Anwendung wird mit dem Visual-Studio-Remote-Debugger (`msvsmon.exe`) untersucht.

1. Für die verwendete Visual-Studio-Version passende **Remote Tools** aus offizieller Microsoft-Quelle installieren.
2. Passende Architektur wählen (normal x64; bei bestätigten 32-Bit-DLL-Prozessen x86).
3. Exakt zur Anwendung gehörige EXE/DLL und `.pdb` veröffentlichen. Quellstand und Symbole dürfen nicht zu einer anderen Version gehören.
4. Anwendung am Prüf-PC mit korrekter Konfiguration starten, zunächst Simulation und getrennte Daten.
5. Remote-Debugger mit vorgesehenem Benutzerkonto starten. Windows-Authentifizierung, Berechtigungen und freigegebene Firewallregeln einrichten; nicht pauschal Authentifizierung oder Firewall abschalten.
6. In Visual Studio **Debuggen → An den Prozess anfügen** wählen, Remoteverbindung/Server angeben und Testpilot.Operator.exe, Testpilot.RunPlan.exe oder den Editorprozess auswählen. Bei DLL-Start kann der Prozess `dotnet.exe` heißen; Kommandozeile kontrollieren.
7. .NET-Debugging aktivieren, Symbolstatus im Modulfenster prüfen, Breakpoint setzen.
8. Repro ausführen; optional Breakpoint-Flag im Plan oder Einzelschritt verwenden.
9. Nach Abschluss sauber trennen, Debugmodus/Timeoutänderungen zurücknehmen und Diagnosezugang nach Betriebsregel schließen.

Offizielle Anleitung: [Remote-Debugging mit Visual Studio](https://learn.microsoft.com/visualstudio/debugger/remote-debugging). Menünamen/Verbindungsdetails können je Visual-Studio-/Remote-Tools-Version abweichen.

**Achtung:** Anhalten eines produktiven Hardwareprozesses kann Ausgänge und Verriegelung aktiv lassen oder Kommunikationsfristen verletzen. Nur mit abgesichertem Prüfplatz und abgestimmter Arbeitsweise debuggen.

## Häufige Verständnisfragen

**Kann ich freigegebene Werte schnell per SQL korrigieren?** Nein. Neue Version anlegen. SQL umgeht Schutzregeln und beschädigt die Nachvollziehbarkeit.

**Warum ändert ein neuer Stationswert einen freigegebenen Plan?** Der Ablauf/Planwert ist eingefroren; Stationskonfiguration wird bewusst erst am Platz gebunden. Beides getrennt sichern und Änderungen kontrollieren.

**Ist ein Firmwarepfad im Snapshot gleich einer archivierten Firmware?** Nein. Dateiinhalt gesondert unveränderlich versionieren/sichern.

**Ist der Debug-Login eine Editor-Anmeldung?** Nein. Er erlaubt Einzelschritt im Operator. Editorzugriff muss separat geschützt werden.

**Kann ich mit Simulation bedenkenlos jeden Plan starten?** Nein. Externe Programme, CustomCode, SAP und andere eigene Nebenwirkungen bleiben möglich.

**Führt Rückzug einer Freigabe zum Abbruch laufender Prüfungen?** Nein. Er verhindert nachfolgende Vorbereitungen dieser Freigabe, nicht die bereits gestartete Ausführung.

**Kann man aus YAML alles wiederherstellen?** Nein. Typ-/Stations-YAML enthält nicht alle Pläne, Benutzer und Läufe; vollständiges Backup nötig.

**Was prüfen wir vor der nächsten produktiven Prüfung?** Fehlende Migrationen, wirksame Konfiguration, Simulation/real, Planfreigabe, echte Geräteverbindung und sicheren Abschluss. Eine behobene Bildschirmmeldung allein ist noch kein Hardware-Nachweis.
