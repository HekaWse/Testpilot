# 1. Einstieg und Beispielprüfung

[Dokumentationsübersicht](README.md) · Weiter: [Editor bedienen](02-editor.md)

## Inhalt

- [Was brauche ich?](#was-brauche-ich)
- [Software bauen und Demo-Datenbank vorbereiten](#software-bauen-und-demo-datenbank-vorbereiten)
- [Prüfsystemtyp → Prüfsystem → Prüfplan](#prüfsystemtyp--prüfsystem--prüfplan)
- [Die erste Prüfung ausführen](#die-erste-prüfung-ausführen)
- [Woran erkenne ich einen erfolgreichen Einstieg?](#woran-erkenne-ich-einen-erfolgreichen-einstieg)

## Was brauche ich?

Für Entwicklung und diese Anleitung:

- Windows, Visual Studio 2026 mit **ASP.NET und Webentwicklung** sowie **.NET-Desktopentwicklung**.
- .NET-10-SDK; [global.json](../global.json) fordert 10.0.403 und erlaubt neuere .NET-10-Feature-Bands, keine Vorabversionen.
- Das Repository in einem lokalen Arbeitsverzeichnis, beispielsweise `C:\GIT_REPOSITORIES_LOCAL\Testpilot`.
- Schreibrechte auf `C:\TestpilotDemo` oder einen entsprechend angepassten Demoordner.

**Nicht erforderlich:** echte Messgeräte, VISA, SAP-Zugang oder ein externer Datenbankserver. Das Beispiel nutzt SQLite, einen SAP-Mock und ein simuliertes Multimeter.

Für die reine Bedienung einer fertig veröffentlichten Installation ist Visual Studio nicht erforderlich. Siehe [Installation und Veröffentlichung](04-konfiguration-und-betrieb.md).

## Software bauen und Demo-Datenbank vorbereiten

1. In Visual Studio vorzugsweise **Testpilot.slnx** öffnen. Die klassische `Testpilot.sln` ist ebenfalls vorhanden; beim Hinzufügen von Projekten beide Projektmappen beachten.
2. PowerShell im Repository-Hauptverzeichnis öffnen:

```powershell
dotnet --version
dotnet tool restore --tool-manifest .\dotnet-tools.json
dotnet restore Testpilot.slnx
dotnet build Testpilot.slnx
dotnet test tests\Testpilot.Tests\Testpilot.Tests.csproj
```

3. Einen getrennten Demoordner verwenden, damit keine Produktionsdaten verändert werden:

```powershell
New-Item -ItemType Directory -Path C:\TestpilotDemo -Force
$env:Database__Provider = 'Sqlite'
$env:ConnectionStrings__Testpilot = 'Data Source=C:\TestpilotDemo\testpilot.db'
dotnet run --project src\Testpilot.Editor -- --migrate
```

`--migrate` legt das Datenbankschema an beziehungsweise aktualisiert es und beendet das Programm. Ein normaler Start migriert nicht automatisch. Das optionale `--seed-system-types` wird für diese Demo **nicht** benötigt.

4. Editor starten:

```powershell
dotnet dev-certs https --trust
dotnet run --project src\Testpilot.Editor --launch-profile https
```

5. `https://localhost:7000` öffnen. Alternativ ist das lokale HTTP-Profil auf Port 5234 vorhanden. Die Entwicklungszertifikat-Vertrauensabfrage gilt nur für die lokale Entwicklung, nicht als Produktions-TLS-Konfiguration.

Die gestartete PowerShell offen lassen. Umgebungsvariablen gelten für deren Kindprozesse, nicht automatisch für Visual Studio oder ein anderes Terminal.

## Prüfsystemtyp → Prüfsystem → Prüfplan

### A. Prüfsystemtyp DEMO

1. **Prüfsystemtypen** öffnen.
2. Im Bereich **YAML-Import und -Export** den Inhalt von [demo-pruefsystemtyp.yaml](beispiele/demo-pruefsystemtyp.yaml) einfügen.
3. **Als neuen Prüfsystemtyp importieren** wählen.
4. Typ **DEMO** auswählen und prüfen: Gerätekennung `dmm1`, Modell `SimDmm`, Kanal `1003`.

Alternativ manuell Typ DEMO anlegen, Gerät `dmm1` mit Modell `SimDmm` hinzufügen und als Kanäle `1003` eintragen. Das ist eine frei gewählte Simulationskanalnummer und keine Aussage über einen physischen Aufbau.

### B. Prüfsystem DEMO-1

1. **Prüfsysteme** öffnen.
2. Unter **Neues Prüfsystem anlegen** Name `DEMO-1` und Typ **DEMO** wählen; **Anlegen**.
3. DEMO-1 auswählen. Im Bereich **Geräte und Adressen** beim Gerät `dmm1` **Zuordnen** wählen.
4. Im getrennten Eingabebereich den Treiber `SimulatedSwitchMultimeter` wählen.
5. Unter **Weitere Verbindungsparameter (JSON-Objekt)** eintragen:

```json
{"channels":[1003],"readings":{"1003|VOLT:DC":3.3}}
```

6. **Zuordnen**. Das Gerät muss anschließend als eingebaut erscheinen.

Der vorbereitete Wert ist immer 3,3 V. Ohne `readings` liefert der simulierte DMM für nicht belegte Messwerte **0**, nicht automatisch den erwarteten Sollwert.

Die Datei [demo-pruefsystem.yaml](beispiele/demo-pruefsystem.yaml) zeigt dieselbe Station als Export-/Importformat. Nicht zusätzlich unter demselben Namen importieren. Ein Import legt ein neues System an, er aktualisiert kein vorhandenes.

### C. Prüfplan DEMO-001 / A

1. **Prüfpläne** öffnen.
2. Artikelnummer `DEMO-001`, Fertigungszustand `A`, **Entwurf erstellen**.
3. Im ausgewählten Entwurf Typ **DEMO** wählen und **Stammdaten speichern**.
4. Unter **Neuen Schritt hinzufügen**:

| Feld | Wert |
| --- | --- |
| Schrittname | `VCC_3V3` |
| Typ | Messung (`Measure`) |
| Gerät | `dmm1` |
| Kanal | `1003` |
| Messgröße | `VOLT:DC` |
| Einheit | automatisch V |
| Bereich | `10 V` |
| NPLC | `1` |
| Grenzwert unten | `3,2 V` |
| Grenzwert oben | `3,4 V` |

5. **Schritt hinzufügen**. Der Schritt muss in **Vorhandene Schritte** erscheinen.
6. **Vorschau erstellen**. Bei dieser einen Kanalkonfiguration ist sie in der Init-Phase zu erwarten.
7. Warnungen und Kanalbelegung prüfen; **Prüfplan freigeben**.

Die Simulation erwartet keine Hardware; trotzdem gelten Typzuordnung, Gerätebelegung und Freigaberegeln. Ein Entwurf allein ist nicht ausführbar.

## Die erste Prüfung ausführen

### Variante 1: Konsole – besonders einfach für den Einstieg

Editor weiterlaufen lassen. Ein **zweites PowerShell-Fenster im Repository-Hauptverzeichnis** öffnen und dessen Konfiguration ausdrücklich setzen:

```powershell
$env:Database__Provider = 'Sqlite'
$env:ConnectionStrings__Testpilot = 'Data Source=C:\TestpilotDemo\testpilot.db'
$env:Export__Directory = 'C:\TestpilotDemo\exports'
$env:Logging__Directory = 'C:\TestpilotDemo\logs'
$env:Simulation__Enabled = 'true'
$env:Sap__BaseUrl = ''
$env:Sap__Command = ''
${env:Sap__MockOrders__FAUF-DEMO__ArtNr} = 'DEMO-001'
${env:Sap__MockOrders__FAUF-DEMO__Fz} = 'A'
dotnet run --project tools\Testpilot.RunPlan -- --station DEMO-1 --fauf FAUF-DEMO --serial 1234567890
```

Erwartung: Ergebnis `Pass`, ein Lauf in der Demo-Datenbank und eine JSON-Datei unter `C:\TestpilotDemo\exports` mit `value: 3.3` und `simulated: true`.

Für Pause vor dem Schritt denselben Befehl um `--single-step` ergänzen; **Enter** setzt fort, **Strg+C** bricht ab.

### Variante 2: Bedienprogramm

Im zweiten Terminal dieselben Variablen wie oben setzen und ergänzen:

```powershell
$env:Operator__Station = 'DEMO-1'
$env:Operator__FixtureDevice = ''
$env:Operator__SerialReaderDevice = ''
dotnet run --project src\Testpilot.Operator --no-launch-profile
```

1. Prüfen, dass der orange Simulationshinweis angezeigt wird.
2. FAUF `FAUF-DEMO` eingeben, **Enter** beziehungsweise **Auftrag laden**.
3. Artikel DEMO-001, FZ A und freigegebene Version kontrollieren.
4. Seriennummer `1234567890` eingeben und **Enter** oder **F5**.
5. Erwartung: **i.O.**, Messwert 3,3 V und angezeigter Exportpfad.

Das Beispiel [demo-operator-appsettings.json](beispiele/demo-operator-appsettings.json) enthält dieselben Einstellungen für eine getrennte veröffentlichte Demo-Installation. Den Pfad anpassen und nicht über eine produktive Konfiguration kopieren.

> Das Bedienprogramm hat in seiner ausgelieferten JSON-Konfiguration einen realen SAP-Endpunkt. Simulation allein deaktiviert SAP nicht. Deshalb werden hier `Sap:BaseUrl` und `Sap:Command` ausdrücklich geleert. Eventuell schon gesetzte Umgebungsvariablen oder zusätzliche lokale Konfiguration vorher prüfen.

## Woran erkenne ich einen erfolgreichen Einstieg?

- [ ] Navigation: Prüfsystemtypen → Prüfsysteme → Prüfpläne.
- [ ] DEMO-1 besitzt `dmm1`, und der freigegebene Plan gehört zu DEMO.
- [ ] Der Auftrag findet genau diesen Plan; ein Entwurf wird nicht ersatzweise verwendet.
- [ ] Der Lauf ist sichtbar als Simulation gekennzeichnet.
- [ ] Messung 3,3 V liegt innerhalb 3,2–3,4 V und ergibt i.O.
- [ ] JSON enthält den erwarteten Wert, Grenzwerte, Version und `simulated: true`.
- [ ] Im Logordner liegt ein Laufprotokoll.

Bei Problemen: [Fehlersuche](10-fehlersuche-und-debugging.md). Terminal schließen, um die nur dort gesetzten Umgebungsvariablen zu verwerfen. Demo-Daten nicht als Produktionsnachweis verwenden.
