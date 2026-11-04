# Testpilot – Dokumentation

**Anwenderhandbuch und Softwaredokumentation · Stand: 04.11.2026 · Softwarebasis: 0.1.0**

Testpilot erstellt, verwaltet und führt Prüfabläufe für elektronische Baugruppen aus. Der Editor plant die Prüfung; das Bedienprogramm führt sie an einem konkreten Prüfsystem aus. Diese Dokumentation erklärt beides und zeigt Entwicklern, wie die Software aufgebaut ist und erweitert wird.

## Der passende Einstieg

| Ich möchte … | Hier beginnen |
| --- | --- |
| Testpilot verstehen und ohne Hardware ausprobieren | [1. Einstieg und Beispielprüfung](01-einstieg.md) |
| Prüfsystemtypen, Prüfsysteme und Prüfpläne einrichten | [2. Editor bedienen](02-editor.md) |
| Baugruppen prüfen, Ergebnisse verstehen, Einzelschritt nutzen | [3. Prüfbetrieb](03-pruefbetrieb.md) |
| Eine Installation konfigurieren, aktualisieren oder sichern | [4. Konfiguration und Betrieb](04-konfiguration-und-betrieb.md) |
| Den Aufbau und die Zuständigkeiten der Software verstehen | [5. Architektur](05-architektur.md) |
| Datenbank, JSON, YAML und Schrittparameter nachschlagen | [6. Datenmodell und Datenformate](06-datenmodell-und-formate.md) |
| Verstehen, wie Pläne optimiert und ausgeführt werden | [7. Optimizer und Sequencer](07-optimizer-und-sequencer.md) |
| Geräte, Kanäle, Treiber und Modulprofile konfigurieren | [8. Treiber und Geräteprofile](08-treiber-und-profile.md) |
| Entwickeln, testen oder eigene Funktionen ergänzen | [9. Entwicklung und Erweiterung](09-entwicklung-und-erweiterung.md) |
| Einen Fehler untersuchen oder remote debuggen | [10. Fehlersuche und Debugging](10-fehlersuche-und-debugging.md) |

**Empfohlene Lesereihenfolge:** Bediener: 1 → 3 → 10. Prüfplanersteller: 1 → 2 → 6 → 8. Administratoren: 1 → 4 → 10. Entwickler: 5 → 6 → 7 → 8 → 9.

## Das Grundprinzip

```text
Prüfsystemtyp         Prüfsystem              Prüfplan
Modelle und Kanäle -> eingebaute Geräte       Artikel + FZ + Version
					 Treiber und Adressen           |
													v
											Vorschau / Freigabe
													|
													v
										 eingefrorener Ausführungsplan
													|
FAUF -> SAP -> Artikel/FZ -> freigegebenen Plan suchen|
													v
								   an das Prüfsystem binden
													|
													v
								   Init -> Schritte -> Abschluss
													|
													v
									  Datenbank + JSON + Log
```

Navigation und Einrichtung folgen immer **Prüfsystemtypen → Prüfsysteme → Prüfpläne**. Ein Prüfplan ist ein Datensatz, kein C#-Programm. Der Optimizer arbeitet deterministisch; es gibt keine KI im Programm und keine Codegenerierung.

## Begriffe

| Begriff | Bedeutung |
| --- | --- |
| Prüfsystemtyp | Gemeinsame Ausstattung einer Systemfamilie, z. B. PS05: Modelle, Module, mögliche Kanäle. Im Code `TestSystemType`. |
| Prüfsystem / Messplatz / Station | Konkreter Aufbau, z. B. PS05-1. Hier stehen tatsächlich eingebaute Geräte, Treiber und Verbindungen. Im Code `Station`. |
| Gerätekennung | Stabiler Schlüssel, z. B. `dmm1`. Verbindet Typgerät, Stationsgerät und Prüfschritt. Im Code `DeviceKey`. |
| Geräteprofil | Maschinenlesbare Beschreibung von Fähigkeiten, Kanälen, Grenzen und Konfigurationszustand. |
| Artikelnummer / ArtNr | Artikel der zu prüfenden Baugruppe; zusammen mit FZ Suchschlüssel für den Prüfplan. |
| Fertigungszustand / FZ | Zustandskennung des Artikels, nicht die Version des Prüfplans. |
| FAUF | Fertigungsauftrag. SAP liefert dazu Artikelnummer, FZ und optional eine Bezeichnung. |
| Prüfplan | Bearbeitbare Schrittfolge im Entwurf; nach Freigabe unveränderlich. |
| Ausführungsplan | Ergebnis des Optimizers mit Init-, Schritt- und Abschlussphase; bei Freigabe als JSON eingefroren. |
| Lauf / Prüflauf | Eine Ausführung für eine Seriennummer; Ergebnisse werden unabhängig vom späteren Planzustand gespeichert. |
| Benannte Einstellung | Wert mit Alias in einem Prüfplan oder Prüfsystem, z. B. Firmwarepfad oder COM-Port. Keine Geheimnisablage. |
| Safe State | Gerätespezifischer sicherer Zustand, z. B. Versorgung aus und Relais offen. Kein Ersatz für einen Hardware-Not-Aus. |
| Simulation | Ersetzt verwendete Gerätetreiber durch simulierte Implementierungen. Ergebnisse sind keine realen Prüfungen. |
| i.O. / n.i.O. | In Ordnung / nicht in Ordnung. Technisch `Pass` / `Fail`. Ein technischer `Error` ist davon verschieden. |
| NPLC | Integrationszeit als Anzahl Netzperioden. Für Gleichspannungs-, Gleichstrom- und Widerstandsmessungen, nicht für AC-Funktionen. |

## Was ist vorhanden – und was nicht?

| Bereich | Aktueller Stand |
| --- | --- |
| Editor | Blazor Interactive Server mit MudBlazor, deutsche Oberfläche, weiße Flächen und klar getrennte Eingabebereiche; kein Hardwarezugriff. |
| Prüfbetrieb | WPF-Bedienprogramm und Konsolenhost; automatische Adaptersteuerung und Seriennummernleser optional. |
| Planverarbeitung | Typisierte Parameter, deterministischer Optimizer, Freigabe/Versionierung, Laufvorbereitung, Sequencer. |
| Daten | EF Core; SQLite als Standard, eigene Migrationen für SQLite, SQL Server und PostgreSQL. Providerwechsel über Konfiguration. |
| Integration | SAP-Mock, lesende REST-Anbindung und externer SAP-Adapter; JSON-Ergebnisexport und YAML für Typen/Systeme. |
| Treiber | Simulation sowie reale SCPI-, VISA-, Scanner-, Adapter- und J-Flash-Implementierungen. Hardwareabnahme ist separat erforderlich. |
| Native DLLs | `Ag34980Dll` und `HmpDll` sind absichtlich nicht betriebsfähige Gerüste. |
| Ergebnisarchiv | Ergebnisse in Datenbank und JSON; kein eigener historischer Ergebnisbrowser oder Reportdesigner im Editor. |
| Zugriffsschutz | Debug-Login für Einzelschritt im Bedienprogramm. Keine vollständige Benutzer-/Rollenverwaltung oder eingebaute Editor-Anmeldung. |
| Nicht vorhanden | Verteilte Treiberprozesse, automatischer Datentransfer bei Providerwechsel, automatische SAP-Rückmeldung und automatische Wiederholung fehlgeschlagener Exporte. |

## Vor einem produktiven Einsatz

> **Wichtig:** Erfolgreiche Softwaretests und eine grüne Simulationsprüfung sind keine Hardware-, Messmittel- oder Sicherheitsabnahme.

- Simulation und reale Prüfung eindeutig trennen; Kennzeichnung und gespeichertes `simulated` beachten.
- Verdrahtung, Kanäle, Geräteidentität, Messbereiche, Leistung und Safe State am tatsächlichen Aufbau prüfen.
- Datenbank, Exportordner, Protokolle und Konfiguration sichern; Berechtigungen und Editorzugriff betrieblich schützen.
- Keine Passwörter in benannten Einstellungen, Planparametern oder Geräteverbindungen hinterlegen.
- Externe Programme und `CustomCode` sind vertrauenswürdiger Anwendungscode, keine Sandbox.
- Bei Prozessabsturz, Stromausfall oder blockierendem Fremdcode kann Software-Cleanup keine Hardwareabsicherung garantieren.

## Beispiele und Quellen

Unter [beispiele](beispiele) liegen kopierbare Beispieldateien für den gefahrlosen Einstieg. Die einzelnen Anleitungen erklären, wo sie einzufügen sind. Alle Konsolenbefehle verwenden Windows/PowerShell; ohne anderslautenden Hinweis beginnen sie im Repository-Hauptverzeichnis. Platzhalter wie `<Plan-ID>` müssen ersetzt werden und sind keine fertigen Befehle.

Die Kapitel verlinken gezielt auf die Implementierung. Verbindlich für konkrete Feldnamen und Verhalten sind der aktuelle Quellcode, die Konfiguration der jeweiligen Installation und ihre freigegebenen Pläne. Die [bestehende technische README](../README.md) bleibt als ergänzende Referenz erhalten.

**Pflege:** Bei Änderungen an Oberfläche, Parametern, Konfiguration, Datenmodell oder Hardwareprofilen das zugehörige Kapitel und die Beispiele mit aktualisieren. Tests und Hardwareabnahme getrennt dokumentieren; keine pauschale Produktionsfreigabe aus der Dokumentation ableiten.
