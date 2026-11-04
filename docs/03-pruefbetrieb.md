# 3. Prüfbetrieb

[Dokumentationsübersicht](README.md) · Zurück: [Editor](02-editor.md) · Weiter: [Konfiguration und Betrieb](04-konfiguration-und-betrieb.md)

## Inhalt

- [Vor dem Start](#vor-dem-start)
- [Manuell prüfen](#manuell-prüfen)
- [Ergebnisse verstehen](#ergebnisse-verstehen)
- [Automatik mit Prüfadapter](#automatik-mit-prüfadapter)
- [Debugmodus und Einzelschritt](#debugmodus-und-einzelschritt)
- [Konsole Testpilot.RunPlan](#konsole-testpilotrunplan)

## Vor dem Start

Das WPF-Programm **Testpilot.Operator** ist die Bedienoberfläche am Prüfplatz. Es benötigt eine konfigurierte Station und eine aktuelle Datenbank. Es darf nicht mit dem Browsereditor verwechselt werden.

1. Messplatzname im Fenster kontrollieren.
2. Auf den Simulationshinweis achten. Eine Simulation ist **keine reale Prüfung**.
3. Prüfling, Adapter, Verdrahtung und Messmittel gemäß Arbeitsanweisung prüfen.
4. FAUF und Seriennummer bereithalten.
5. Bei unklarer Konfiguration nicht durch Probieren mit realer Hardware fortfahren, sondern den Verantwortlichen hinzuziehen.

Fehlende Migrationen, unbekannte Stationen oder ungültige Verbindungen werden beim Start beziehungsweise bei der Laufvorbereitung gemeldet. Eine laufende Prüfung erfordert keinen geöffneten Browsereditor.

## Manuell prüfen

1. **FAUF** scannen oder eingeben.
2. **Enter** oder **Auftrag laden**. SAP liefert Artikelnummer, FZ und optional Materialbezeichnung. Daraus wird der freigegebene Prüfplan ermittelt.
3. Angezeigte Artikel-/FZ-/Versionsdaten prüfen. Bei falschen Daten nicht starten.
4. **Seriennummer** scannen oder eingeben. Standardmäßig sind genau zehn Ziffern zulässig; die Installation kann ein anderes Muster vorgeben.
5. **Enter** im Seriennummernfeld oder **F5** startet den Lauf.
6. Fortschritt und Einzelwerte beobachten. Bei einer Bedienerfrage bewusst **Ja** oder **Nein** wählen.
7. Gesamtergebnis und eventuelle Speicher-/Exportmeldung beachten.

Beim Start werden Auftrag und Plan erneut vorbereitet. Die vor dem vorherigen Lauf geladene Version wird nicht unbegrenzt wiederverwendet. Geänderte FAUF verwirft den zuvor geladenen Auftrag.

**Abbrechen:** **Esc** beziehungsweise Abbrechen fordert einen kontrollierten Abbruch an. Das Schließen des Fensters fordert ebenfalls Abbruch und Aufräumen an. Nicht einfach den Prozess beenden: Dann kann der gerätespezifische Abschluss ausfallen. Ein Softwareabbruch ist kein sicherheitsgerichteter Hardware-Not-Aus.

Nach **i.O.** oder **n.i.O.** wird im manuellen Modus die Seriennummer für das nächste Teil geleert. Nach Fehler/Abbruch bleibt sie zur bewussten Wiederholung erhalten. Wiederholungen erzeugen eigene Läufe und überschreiben keine alten Messergebnisse.

## Ergebnisse verstehen

| Anzeige | Technischer Wert | Bedeutung und Reaktion |
| --- | --- | --- |
| i.O. | `Pass` | Alle ausgeführten Prüfkriterien bestanden und kein höherpriorisierter Fehler. Trotzdem Speicher-/Exportwarnung separat prüfen. |
| n.i.O. | `Fail` | Mindestens ein fachlicher Prüfschritt nicht bestanden, etwa Grenzwert verletzt oder Bedienerfrage verneint. Teil nach Arbeitsanweisung behandeln. |
| Fehler | `Error` | Technisches Problem, z. B. Kommunikation, Konfiguration, ungültiger Messwert oder Fehler beim Safe State. Ursache untersuchen, nicht als normalen Ausschuss behandeln. |
| Abgebrochen | `Aborted` | Abbruchanforderung ohne übergeordneten technischen Fehler. Keine abgeschlossene Gutprüfung. |
| Übersprungen | `Skipped` | Dieser Schritt wurde nicht ausgeführt, z. B. nach technischem Fehler oder Abbruch. Kein bestandener Test. |

Ein fachliches **Fail** beendet die Schrittfolge nicht automatisch. Ein technischer **Error** beendet die restlichen Schritte; sie werden als übersprungen gespeichert. Ein Fehler im Abschluss kann ein ansonsten gutes oder abgebrochenes Ergebnis zu **Error** machen.

Die Grenzwerte sind einschließlich: 3,2 V ist bei Untergrenze 3,2 V erlaubt, ebenso die exakte Obergrenze. Nicht endliche Messwerte sind Fehler, nie Gutwerte.

### Speichern und Export sind getrennte Ergebnisse

Nach dem Geräteabschluss versucht das Programm unabhängig voneinander:

1. Lauf und Einzelwerte in der Datenbank zu speichern.
2. Ein JSON-Ergebnis in den Exportordner zu schreiben.

Eine rote/gesonderte Speicher- oder Exportmeldung **nicht ignorieren**, auch wenn die eigentliche Messung i.O. war. Eine erfolgreiche JSON-Datei beweist nicht automatisch eine erfolgreiche Datenbankspeicherung und umgekehrt. Es gibt keinen verteilten Commit und keinen automatischen Hintergrund-Nachversand. Fehlermeldung, Zeitpunkt, Seriennummer und vorhandene Datei/Logs dem Verantwortlichen übergeben.

Die JSON-Datei heißt `<Lauf-ID ohne Bindestriche>.json`. Einheiten und Grenzwerte sind SI-Werte; `simulated` kennzeichnet simulierte Läufe. Der Export enthält außerdem Geräteidentifikation, Plan- und Softwareversion. Einzelheiten: [Exportformat](06-datenmodell-und-formate.md#ergebnisexport).

Es gibt derzeit keinen historischen Ergebnisbrowser im Editor. Für Auswertung und Archivierung Datenbank/JSON nach betrieblichen Vorgaben verwenden.

## Automatik mit Prüfadapter

Nur verfügbar, wenn `Operator:FixtureDevice` konfiguriert ist. Optional liest `Operator:SerialReaderDevice` die Seriennummer fest eingebaut. Die manuellen Startmöglichkeiten sind im Adaptermodus nicht der normale Startweg.

### Normaler Ablauf

```text
Auftrag laden
  -> Adapter muss geöffnet erkannt worden sein
  -> Prüfling einlegen / Adapter schließen
  -> verriegeln
  -> konfigurierte Beruhigungszeit abwarten
  -> Seriennummer lesen oder zuvor handgescannte Nummer verwenden
  -> Prüfung
  -> Ergebnis anzeigen / Lampe setzen / freigeben
  -> Adapter öffnen
  -> nächstes Teil
```

- Adapterzustand wird ungefähr alle 200 ms abgefragt.
- Ein bereits beim Programmstart geschlossener Adapter startet nicht unbesehen: Zuerst muss „offen“ erkannt werden.
- Eine manuell eingegebene gültige Seriennummer hat Vorrang vor dem festen Leser.
- Nach einem Lesefehler wird freigegeben und zur Eingabe mit dem Handscanner aufgefordert. Anschließend den normalen Öffnen-/Schließenablauf erneut durchführen.
- Nach Freigabe und abgeschlossenem Adapterzyklus wird die Seriennummer für das nächste Teil verworfen; dies unterscheidet sich vom manuellen Fehlerfall.
- Ohne passenden freigegebenen Plan startet keine Prüfung.

### Simulation des Adapterablaufs

Bei Simulation und konfiguriertem Fixture erscheint **Adapter schließen (Simulation)**. Zusätzlich lässt sich ein Lesefehler des Code-Lesers simulieren. Damit werden Bedienabläufe erprobt, nicht die tatsächlichen Adapterausgänge.

### Sicherheitsgrenze

Die Software arbeitet mit gemeldeten Adapterzuständen. Verdrahtung, Verriegelung, Schutzhaube und Freigabe müssen am realen Aufbau unabhängig bewertet werden. Gerätespezifische PS05-Bytes sind keine allgemeine Sicherheitsnorm und dürfen nicht auf andere Adapter übertragen werden.

## Debugmodus und Einzelschritt

1. **Debugmodus …** oder **Alt+D** wählen.
2. Im Editor angelegtes Kürzel und Passwort eingeben.
3. **Einzelschritt** aktivieren.
4. Prüfung starten. Vor jedem eigentlichen Prüfschritt hält die Software an.
5. **Weiter** oder **Strg+Leertaste** setzt bis zur nächsten Pause fort.
6. Nach der Untersuchung **Debugmodus beenden**.

Debugmodus ist eine Bedienberechtigung, **kein** Visual-Studio-Debugger. Er verändert nicht automatisch Timeouts und setzt auch keine C#-Breakpoints. Während einer Pause können bereits initialisierte Geräte und Versorgungen aktiv sein; nicht unbeaufsichtigt stehen lassen.

Im Debugmodus wird das Kürzel in `Run.Operator` übernommen. Ohne Debuganmeldung ist dieser Wert derzeit leer; es gibt keine allgemeine Bedieneranmeldung.

Ein Schritt mit Breakpoint-Flag ruft `Debugger.Break()` nur auf, wenn ein Debugger angehängt ist. Einzelschritt funktioniert dagegen auch ohne Visual Studio. Remote-Debugging: [Kapitel 10](10-fehlersuche-und-debugging.md).

## Konsole Testpilot.RunPlan

Geeignet für Entwickler, Tischtests und gezieltes Debugging. Die Konsole benutzt denselben Sequencer wie das Bedienprogramm, aber keine WPF-Seriennummernprüfung oder automatische Adapterbedienung.

| Argument | Bedeutung |
| --- | --- |
| `--station <Name>` | Erforderlicher Stationsname. |
| `--plan <GUID>` | Optional gezielt eine **freigegebene** Plan-ID auswählen. Der SAP-Schlüssel muss dazu passen. |
| `--fauf <FAUF>` | Auftrag; fehlt er, fragt die Konsole. |
| `--serial <Seriennummer>` | Fehlt sie, fragt die Konsole. |
| `--operator <Text>` | Bedienerkennung für dieses Werkzeug. |
| `--single-step` | Vor jedem Schritt auf Enter warten. |

```powershell
dotnet run --project tools\Testpilot.RunPlan -- --station DEMO-1 --fauf FAUF-DEMO --serial 1234567890 --single-step
```

Voraussetzungen und SAP-Mock-Konfiguration siehe [Beispielprüfung](01-einstieg.md). Für `--plan` die echte GUID aus dem Datenbestand verwenden; eine Artikelnummer ist keine Plan-ID.

- **Enter**: Einzelschritt fortsetzen.
- **j/ja/n/nein**: Bedienerfrage beantworten.
- **Strg+C**: Abbruch anfordern und Abschluss abwarten.
- Exitcode **0**: Pass und keine Speicher-/Exportfehlermeldung.
- Exitcode **1**: Fail, Error, Aborted, Speicher-/Exportfehler oder Fehler bei Vorbereitung/Argumenten.

Die Konsole verwendet standardmäßig Simulation; für reale Versuche `Simulation:Enabled=false` ausdrücklich konfigurieren und die Station vorher prüfen. Die CLI versteht keine allgemeinen Konfigurationsflags wie `--Simulation:Enabled`; stattdessen JSON/Umgebungsvariablen nutzen.

**Quelleneinstieg:** [MainViewModel](../src/Testpilot.Operator/MainViewModel.cs), [OperatorRunService](../src/Testpilot.Operator/OperatorRunService.cs), [RunPlan](../tools/Testpilot.RunPlan/Program.cs).
