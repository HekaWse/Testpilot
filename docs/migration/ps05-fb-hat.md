# Migration PS05 – FB-HAT (Programm 11010)

**Arbeitsstand der Migration · Branch `feature/ps05-fb-hat-pruefplan` · Stand: 04.11.2026**

Ziel: Den bestehenden LabWindows/CVI-Prüfablauf für die FB-HAT-Baugruppen als Testpilot-Prüfplan abbilden, sodass der Prüfplan ohne Programmcode im Editor zusammengestellt und im Bedienprogramm ausgeführt werden kann. Auf diesem Branch wird gearbeitet, bis der Plan vollständig läuft; danach wird nach `main` gemergt.

## Quellen

| Datei | Inhalt |
| --- | --- |
| `H:\ATE\PS05\cvi_prog\11010\11010_329431\test.c` | Altprogramm (Version 01.01.10): Init, `Test_Pm`, `Test_End`, Hilfsfunktionen |
| `...\Prüfplan\gen_tex_pass_4001430-01_332351.txt` | Offizielle Schrittliste mit Nummern, Namen und Grenzwerten (59 Prüfpunkte, 2.1–2.20). Maßgeblich für Namen und Grenzwerte. |
| `H:\ATE\Ini-Files\<ArtNr>_<FZ>\<ArtNr>_<FZ>.ini` | Variantendaten: `Firm_ver_dut`, J-Flash-Pfade (`[SEGGER]`) |
| `...\Treiber\Ps05Intern.c` | Interne Relais: Relais n → 34903A-Kanal 200+n |
| `...\Treiber\Segger_JFlash.c` | Aufruf von J-Flash mit den INI-Werten |
| `D:\sys\FSACommunication.dll` | Produktive .NET-DLL für die Kommunikation mit Prüfling (COM9) und Golden Sample (COM3) |

`config.ini` im Programmordner gehört zu einem MSP430-Aufbau und ist für FB-HAT nicht relevant.

## Festgelegte Entscheidungen

| Thema | Entscheidung |
| --- | --- |
| `FSACommunication.dll` | Ist die Produktions-DLL und wird direkt (als .NET-Assembly) im FSA-Treiber verwendet. |
| `CRANGE_BYPASS` (Kanal 121) | Keine Strommessung; der Messkanal wird überbrückt, damit das Messgerät keinen Widerstand im Strompfad erzeugt. Wird als eigener Kanalzustand modelliert, nicht als Messbereich. |
| Golden Sample | Wird nur über USB versorgt. Kein Versorgungsschritt, nur die COM-Verbindung ist ein Gerät. |
| FZ in der Varianten-INI | Der Eintrag `Fertigungszustand` in `[Fertigungsparameter]` wird nicht verwendet (historisch). Maßgeblich ist der FZ im Ordner-/Dateinamen bzw. aus SAP. |
| Auftragsterminal / `db_auftrag` | Entfällt ersatzlos (Prüfung des Auftragsstatus „started/available“ über das Terminal wird nicht mehr benötigt). |
| `Retry.txt` | Entfällt. Wiederholungen werden nur im Geräte-Trace protokolliert. |
| Alte Rückverfolgbarkeit | Nur `frameFP_SaveData` wird weiterhin benötigt. Zielort und Format sind noch zu klären (siehe offene Punkte). |

## Abbildung des Altablaufs

### Direkt abbildbar

| Altprogramm | Testpilot-Schritt |
| --- | --- |
| `hmp_Config/enable/disable` (Kanal 1, 3,0 V, 0,5 A) | `SetPower` |
| `Ps05Intern_Relais(1..7)` (ShuntBridged, KEY, Demount, Reset, Alarm, CloserSense, USBSense) | `Relay` auf 201–207 |
| `Sleep(x)` | `Wait` |
| `ag_messung` auf 101/103/109–115 (Spannung, Widerstand), 121 (Strom) | `Measure` mit Grenzwerten aus gen_tex |
| `seggerJFlash_start` | `Program` (J-Flash), Pfade aus der Varianten-INI als benannte Einstellungen |
| `GetSerNr` | `Scan` |

### Benötigte neue Bausteine (einmalig, allgemein)

1. **FSA-Gerät und Schritt „Befehl an Prüfling“** – Prod-Mode, Factory-Reset, Config-Buffer lesen/schreiben, Selbsttest, Firmwareversion, LED-/Last-/Takt-Befehle, HF-Senden/-Empfangen mit Golden Sample, Tasterstatus. Rückgabewert vergleichbar als Zahl, Text oder Bit. Betrifft etwa 35 der 59 Prüfpunkte.
2. **Berechnete Werte** – Differenzen (2.3.2, 2.6.4–2.6.6, 2.16.5, 2.16.8), Verhältnis (2.4.16), PT1000 → °C, RSSI-Mittelwert + Korrektur; Bezug auf vorherige Ergebnisse.
3. **Mehrfachmessung** – Mittelwert mit Verwerfen der höchsten Werte (2.5.1), „messen bis Schwelle, maximal n-mal“ (Entladeschleifen).
4. **Frequenzmessung und Einheiten** – FREQ auf Kanal 108 (2.7.1); Einheiten Hz/kHz, dB, °C, STK.
5. **Wiederholung bei Fehler** – als Schrittoption (J-Flash, FSA-Befehle).
6. **Abbruch bei n.i.O.** – Plan- oder Abschnittsoption „bei Fail stoppen“ (Altverhalten nach jedem Abschnitt).
7. **Clipping 2.9.7/2.9.8** – einzige bedingte Verzweigung; vorerst als `CustomCode`-Schritt.
8. **Kanalzustand BYPASS** für Strommesskanäle.
9. **Ergebnisablage wie `frameFP_SaveData`** – zusätzlich zum JSON-Export, sobald Format und Ort bekannt sind.

## Offene Punkte

- [ ] Bedeutung der generischen FSA-Befehlscodes (`8/3`, `8/4`, `8/5`, `5/1…5/5`, `21/1…21/6`, `2/8`, `2/9`, …) – C#-Quelle oder Protokollbeschreibung der `FSACommunication.dll`.
- [ ] Zielort und Format der Daten aus `frameFP_SaveData` (geschlossene `framefp.dll`).
- [ ] Zuordnung Digits ↔ NPLC (5,5 / 6,5 Digits) am Datenblatt des 34970A prüfen.
- [ ] Verdrahtung am Aufbau bestätigen: 4-Draht-Paar 109/119, Stromkanal 121.
- [ ] Welche ArtNr/FZ-Kombinationen als Prüfpläne angelegt werden (Ordner unter `H:\ATE\Ini-Files`, Programm 11010).

## Arbeitsplan auf diesem Branch

1. FSA-Treiber (simuliert + real über `FSACommunication.dll`) und Schritttyp „Befehl an Prüfling“.
2. FREQ-Messfunktion, zusätzliche Einheiten, Kanalzustand BYPASS.
3. Berechnungsschritt mit Bezug auf vorherige Ergebnisse, Mehrfachmessung.
4. Schrittoptionen Wiederholung und „bei Fail stoppen“.
5. Prüfplan FB-HAT als Entwurf (Reihenfolge und Grenzwerte aus gen_tex), Simulation grün.
6. Ergebnisablage wie `SaveData`.
7. Abnahme am PS05-1, danach Merge nach `main`.
