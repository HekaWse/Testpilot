# Migration PS05 – FB-HAT (Programm 11010)

**Branch `feature/ps05-fb-hat-pruefplan` · Stand: 09.11.2026**

Ziel: Den bisherigen LabWindows/CVI-Prüfablauf der FB-HAT-Baugruppe (ArtNr **4001430-01**, FZ **332351**) als Testpilot-Prüfplan abbilden. Der Plan wird ohne Programmcode im Editor gepflegt und im Bedienprogramm ausgeführt.

## Stand

| Thema | Stand |
| --- | --- |
| Prüfplan | Liegt als **Entwurf** vor. Er wird über den Seed angelegt (`Ps05FbHatPlanSeed`) und enthält alle Prüfpunkte 2.1–2.20 aus test.c, außerdem die Aktionen dazwischen. |
| Simulation | Ein Lauf auf dem Prüfsystem **PS05-Sim** ist durchgehend Pass. Er wird gespeichert und als JSON exportiert (automatischer Test `Ps05FbHatPlanTests`). |
| Prüfsystem PS05-1 | Die Geräte sind eingetragen: Multiplexer, Netzteil, Adapter, Prüfling COM9, Golden Sample COM3, J-Link, Scanner. Am echten Aufbau wurde noch **nicht getestet**. |
| Neue Bausteine | DUT-Befehl, Berechnung, Mehrfachmessung, FREQ und neue Einheiten, Wiederholung bei Fehler, FSA-Treiber (real und simuliert). Alles ist allgemein gebaut, nicht nur für FB-HAT. |
| Ergebnisablage wie `SaveData` | Noch offen (siehe offene Punkte). |

### Ausprobieren

1. Seed ausführen: im Editor unter **Prüfsystemtypen** auf „Standardtypen anlegen“ klicken oder `dotnet run --project src\Testpilot.Editor -- --seed-system-types` aufrufen. Dabei entstehen PS05, PS05-1, PS05-Sim und der Planentwurf.
2. Unter **Prüfpläne** den Plan 4001430-01 / 332351 öffnen, die Vorschau des Ausführungsplans prüfen und den Plan freigeben.
3. Im Bedienprogramm (oder mit `Testpilot.RunPlan`) auf **PS05-Sim** ausführen. Die Simulation liefert plausible Werte für alle Prüfpunkte, auch für die Wiederholung bei 2.3.1. Die Wartezeiten des Plans laufen auch in der Simulation in Echtzeit.

## Quellen

| Datei | Inhalt |
| --- | --- |
| `H:\ATE\PS05\cvi_prog\11010\11010_329431\test.c` | Altprogramm (Version 01.01.10). **Maßgeblich für Ablauf und Grenzwerte.** |
| `...\Prüfplan\gen_tex_pass_4001430-01_332351.txt` | Offizielle Schrittliste mit Nummern und Namen. Die Grenzwerte weichen an drei Stellen ab (siehe unten). |
| `H:\ATE\Ini-Files\4001430-01_332351\4001430-01_332351.ini` | Firmwareversion und J-Flash-Pfade. Daraus wurden die benannten Einstellungen des Plans. |
| `...\Treiber\Ps05Intern.c` | Interne Relais: Relais n → Kanal 200+n |
| `D:\sys\FSACommunication.dll` | Produktions-DLL für Prüfling (COM9) und Golden Sample (COM3) |

## Festgelegte Entscheidungen

| Thema | Entscheidung |
| --- | --- |
| `FSACommunication.dll` | Ist die Produktions-DLL. Der Treiber `FsaCommunication` verwendet sie direkt als .NET-Assembly. |
| `CRANGE_BYPASS` (Kanal 121) | Keine Strommessung; das Messgerät darf keinen Widerstand im Strompfad erzeugen (siehe „BYPASS“). |
| Golden Sample | Wird nur über USB versorgt. Es gibt dafür keinen Versorgungsschritt, nur die COM-Verbindung ist ein Gerät. |
| FZ in der Varianten-INI | Ist historisch und wird nicht verwendet. Maßgeblich ist der FZ des Plans. |
| Auftragsterminal, `db_auftrag`, Logged-in-Terminal | Entfallen ersatzlos. |
| `Retry.txt` | Entfällt. Wiederholungen stehen im Log. |
| Alte Rückverfolgbarkeit | Nur `frameFP_SaveData` wird weiter benötigt. Ort und Format sind noch offen. |

## Abbildung des Altablaufs

| Altprogramm | Testpilot |
| --- | --- |
| `hmp_Config/enable/disable` (Kanal 1, 3,0 V, 0,5 A) | `SetPower` |
| `Ps05Intern_Relais(1..7)` | `Relay` auf 201–207 |
| `Sleep(x)` | `Wait` |
| `ag_messung` (Spannung, Widerstand, Strom auf 121) | `Measure` |
| `ag_messung` mehrfach mit Verwerfen der höchsten Werte (2.5.1) | `Measure` mit „Anzahl Messungen“ = 5 und „Höchste Werte verwerfen“ = 2 |
| Frequenz auf 108 (2.7.1) | `Measure` mit Funktion `FREQ`, Einheit Hz |
| `seggerJFlash_start` mit Wiederholung | `Program` mit `{{plan.PROGFILE_PATH_1}}`, 1 Wiederholung |
| FSA-Befehle (Prod-Mode, Reset, Config-Buffer, Selbsttest, Firmware, HF, Taster, generische Befehle) | `DutCommand` auf Gerät `Dut` bzw. `GoldenSample` |
| Rückgelesene Werte prüfen (2.4.x, 2.9.8, Taster) | `DutCommand` mit Ausgabeformel und Grenzwerten, oder `Calculate` |
| Differenzen, Verhältnis, PT1000 → °C, RSSI-Mittelwert + Korrektur | `Calculate` mit Bezug auf vorherige Schritte (`{Schrittname}`, `{Schritt:ausgabe}`) |
| Schleifen „messen bis Schwelle, maximal n-mal“ (2.3.1, Entladen LED) | `Measure` mit Wiederholung bei Fehler |
| Clipping 2.9.7 (einzige Verzweigung) | `Calculate` mit `max(0, …)` statt `CustomCode` |
| `GetSerNr` | Seriennummer des Laufs; im Plan als `{serial}` |

Hilfsmessungen ohne eigene Prüfpunktnummer, zum Beispiel „VCC Idle 103“, sind eigene Schritte ohne Grenzwerte. Sie liefern die Werte für die Berechnungen und stehen auch im Ergebnis.

## Abweichungen vom Altprogramm

| Punkt | Altprogramm | Testpilot |
| --- | --- | --- |
| Abbruch bei n.i.O. | Abbruch nach jedem Abschnitt mit Fehler | Ein Fail beendet den Lauf **nicht**, nur Error bricht ab. Eine Option „bei Fail stoppen“ gibt es noch nicht. |
| Grenzwerte | test.c | Aus test.c übernommen. In gen_tex stehen andere Werte: 2.6.3 3,000–3,015 V (test.c 2,85–3,015 V), 2.16.4/2.16.7 obere Grenze 80,1 mV (test.c 82 mV), 2.7.1 32,765–32,771 kHz (test.c 32,7647–32,7713 kHz). |
| 2.4.16 | gen_tex nennt „V“ | Ist ein Verhältnis und wird darum als Zahl ohne Einheit geführt. |
| 2.2.2 ROP Level 1 | ROP wird über das J-Flash-Projekt gesetzt | Konstanter Berechnungsschritt mit Wert 1, der nichts am Gerät prüft. |
| 2.9.6 Clipping-Wert | Konstante −55 | Konstanter Berechnungsschritt mit −55 dBm |
| Identifikation des Prüflings | – | Beim Laufstart wird kein FSA-Befehl gesendet, weil der Prüfling dann noch nicht programmiert ist. Die Firmwareversion prüft Schritt 2.2.4. |
| BYPASS | `CRANGE_BYPASS` vor bestimmten Messungen | Kein eigener Schritt (siehe „BYPASS“). |
| Shunt-Prüfung zu Beginn | Prüfung ohne Prüfpunktnummer | Messschritt „Shunt 1 kOhm“ (999–1001 Ω). Ob er als Prüfschritt bleiben soll, ist offen. |

### BYPASS

Im Altprogramm setzt `CRANGE_BYPASS` nur die Messfunktion des Kanals 121 um und behält den Bereich. Ein geschlossener Pfad ohne Messgerät-Widerstand entsteht dadurch nicht sicher. Der Plan misst den Laststrom deshalb in 2.6.2 mit `CURR:DC` (Bereich 1 A) und hat sonst keinen BYPASS-Schritt. Am echten Aufbau muss geklärt werden, ob der Strompfad über 121 außerhalb der Strommessung geschlossen bleibt (Verdrahtung, Brücke oder Relais). Falls nicht, wird ein Relaisschritt ergänzt.

## Offene Punkte

- [ ] **Ergebnisablage wie `frameFP_SaveData`:** Ort und Format klären (die `framefp.dll` ist nicht dokumentiert).
- [ ] **BYPASS und Verdrahtung** am PS05-1 prüfen: Strompfad über 121, 4-Draht-Paar 109/119.
- [ ] **FSACommunication.dll unter .NET 10:** Am Prüfplatz testen, ob sie lädt und ob `Open`/`Close` und die Befehle funktionieren.
- [ ] **Generische FSA-Befehlscodes** (`8/3`, `8/4`, `8/5`, `5/1…5/5`, `21/1…21/6`, `2/8`, `2/9`, `9/5`): Bedeutung bestätigen. Übernommen sind sie unverändert aus test.c.
- [ ] **Shunt-Prüfung** zu Beginn als Prüfschritt behalten oder entfernen.
- [ ] **2.2.2 ROP:** Soll der Schritt etwas am Gerät prüfen (zum Beispiel die J-Flash-Ausgabe)?
- [ ] **Scanner** am PS05-1: Matrix200 oder DP8530?
- [ ] **J-Link-Seriennummer:** In der INI steht 851004380, am Prüfsystem PS05-1 ist 850102034 eingetragen.
- [ ] **COM-Port des Prüflings beim Programmieren:** Testpilot öffnet COM9 beim Laufstart, also vor dem Programmieren. Prüfen, ob das stört.
- [ ] **Erfolg von J-Flash:** Testpilot wertet nur den Exitcode aus, das Altprogramm las die Logdatei (`ERRORFILE_PATH_1`).
- [ ] **Abbruch bei Fail:** Wird eine Option „bei Fail stoppen“ gebraucht?
- [ ] **Grenzwerte test.c oder gen_tex:** Welche gelten?
- [ ] **Weitere Varianten:** Welche ArtNr/FZ-Kombinationen von Programm 11010 brauchen einen eigenen Plan?
- [ ] **Alte Firmwarepfade am PS05-1:** Die Prüfsystem-Einstellungen enthalten noch Pfade des Programms 11005. Der FB-HAT-Plan nutzt sie nicht (er verwendet `{{plan.*}}`), sie können aber verwirren.
- [ ] **Digits ↔ NPLC:** Zuordnung von 5,5 / 6,5 Digits zu NPLC am Datenblatt des 34970A prüfen.
- [ ] **Abnahme am PS05-1**, danach Merge nach `main`.