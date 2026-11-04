# 2. Editor bedienen

[Dokumentationsübersicht](README.md) · Zurück: [Einstieg](01-einstieg.md) · Weiter: [Prüfbetrieb](03-pruefbetrieb.md)

## Inhalt

- [Orientierung und Speichern](#orientierung-und-speichern)
- [Prüfsystemtypen](#prüfsystemtypen)
- [Prüfsysteme](#prüfsysteme)
- [Benannte Einstellungen](#benannte-einstellungen)
- [Prüfpläne](#prüfpläne)
- [Freigabe und neue Versionen](#freigabe-und-neue-versionen)
- [Debug-Berechtigungen](#debug-berechtigungen)

## Orientierung und Speichern

Der Editor läuft im Browser und kommuniziert per Blazor Interactive Server mit dem Webprozess. Er steuert keine Geräte an. Für Änderungen muss die Verbindung zum Server bestehen.

Die Bereiche im Header und auf der Übersicht stehen in der Reihenfolge **Prüfsystemtypen → Prüfsysteme → Prüfpläne**. Daneben gibt es **Debug-Berechtigungen**.

- **Vorhandene …** zeigt gespeicherte Einträge. Auswahl öffnet deren Details.
- Eingerahmte Bereiche mit **Neue …**, **Neues …** oder **Neuen …** enthalten Eingaben für neue Einträge.
- Nach einem Bearbeiten-Klick wechselt die Formularüberschrift zu **… bearbeiten**. Gespeichert wird mit dem dazugehörigen Knopf.
- **Abbrechen**, **Bearbeitung abbrechen** oder **Eingabe leeren** beendet die jeweilige Eingabe, nicht den gesamten Prüfplan.
- Eine Änderung im Eingabefeld ist noch keine gespeicherte Änderung. Maßgeblich sind der jeweilige Speichern-Knopf, die Erfolgsmeldung und der anschließend angezeigte Datensatz.
- **Neu laden** bei einem Prüfplan liest den Datenbankstand erneut. Gleichzeitige Änderungen können zu einem Konflikt führen; dann zuerst neu laden und die Änderung erneut prüfen, nicht blind überschreiben.

Es gibt keinen allgemeinen „alles speichern“-Knopf. Stammdaten, Geräte, Einstellungen und Schritte sind getrennte Aktionen. Andere Zeilen auswählen oder die Seite wechseln kann noch nicht gespeicherte Eingaben verwerfen.

## Prüfsystemtypen

**Seite:** `/system-types`

Ein Typ beschreibt alle Geräte, die ein System dieser Familie besitzen kann. Er enthält **Modelle und Ausstattung**, keine IP-Adressen oder COM-Ports.

### Einen Typ anlegen

1. Unter **Neuen Prüfsystemtyp anlegen** einen eindeutigen Namen eingeben, etwa PS05 oder DEMO.
2. **Anlegen**, anschließend Typ auswählen.
3. Optional Beschreibung ergänzen und **Prüfsystemtyp speichern**.
4. Im Geräteformular eine Gerätekennung, ein Modell und optional eine Bezeichnung eintragen.
5. Bei modularen Geräten Slots und Module auswählen; bei Simulationsgeräten gegebenenfalls Kanäle eingeben.
6. **Gerät hinzufügen**. Die gespeicherten Geräte stehen im Bereich **Vorhandene Geräte des Prüfsystemtyps**.

**Gerätekennung sorgfältig wählen:** `dmm1` oder `Ag34970A` wird später in Schritten und Stationen verwendet. Die Groß-/Kleinschreibung für Gerätekennungen grundsätzlich beibehalten; sie ist nicht überall austauschbar.

### Modelle und Kanäle

Nur katalogisierte Modelle stehen zur Auswahl. Die Kanal-/Funktionslisten kommen aus den Profilen und den konfigurierten Modulen. Ein Gerät ohne benötigte Fähigkeit erscheint nicht als passende Auswahl für einen entsprechenden Prüfschritt.

Die Nummerierung ist geräteabhängig:

| Gerät | Regel | Beispiel |
| --- | --- | --- |
| 34980A | Slot × 1000 + Modulkanal (`sccc`) | Slot 1, Kanal 3 → **1003** |
| 34970A | Slot × 100 + Modulkanal (`scc`) | Slot 1, Kanal 3 → **103** |

Die zwei Gerätefamilien dürfen nicht verwechselt werden. Strom- und Vierdrahtkanäle sind nicht beliebig austauschbar. Siehe [Treiber und Profile](08-treiber-und-profile.md).

### Bearbeiten und Löschen

- Gerätekennung, Modell und Ausstattung über das Stiftsymbol bearbeiten.
- Verwendete Gerätekennungen können gesperrt sein, weil Stationen oder Pläne sie referenzieren.
- Modell/Ausstattung sind bei Verwendung durch freigegebene Prüfpläne geschützt. Eine solche Änderung ist ein kontrollierter Umbau, keine reine Textkorrektur.
- Ein noch von Prüfsystemen oder Prüfplänen verwendeter Typ kann nicht einfach gelöscht werden.

### Standardtypen und YAML

**Standardtypen anlegen** ergänzt die bekannten Typen und die vorhandene PS05-Beispielausstattung. Bestehende Namen und bereits vorhandene Geräte werden nicht pauschal überschrieben. Diese Funktion ist **kein Hardwaretest**. Übernommene Adressen, Slots und Altwerte müssen zum realen Aufbau passen.

**YAML-Import und -Export:** Export enthält Typname, Beschreibung und Geräte samt Modelloptionen. Import legt einen **neuen** Typ an; Namen dürfen nicht kollidieren. Beispiel und Feldreferenz: [Datenformate](06-datenmodell-und-formate.md). Die YAML-Felder werden streng geprüft; Dokumente über 1 MiB werden abgelehnt.

## Prüfsysteme

**Seite:** `/stations`

Ein Prüfsystem ist die konkrete Station, beispielsweise PS05-1. Hier wird festgelegt, welche Typgeräte dort eingebaut sind und wie sie erreicht werden.

### Ein System anlegen

1. Unter **Neues Prüfsystem anlegen** eindeutigen Namen und Typ auswählen.
2. Optional Geräte/Adressen von einem System desselben Typs übernehmen.
3. **Anlegen**, das neue System auswählen.
4. Name, Beschreibung und Typ prüfen. Änderungen mit **Prüfsystem speichern** übernehmen.

Der Prüf-PC findet sein System über `Operator:Station`. Beim Umbenennen muss daher auch die Konfiguration betroffener PCs geändert werden.

### Geräte und Adressen zuordnen

Die Tabelle zeigt Typgeräte, Modell, Einbaustatus, Treiber und Verbindung. „Nicht eingebaut“ heißt: Das Gerät ist im Typ bekannt, aber dieser Station noch nicht zugeordnet.

1. In der Zeile **Zuordnen** wählen, oder im getrennten Bereich **Neues Gerät zuordnen** ein Gerät des Typs wählen.
2. Einen zum Modell passenden Treiber auswählen.
3. Bei VISA-Treibern die VISA-Ressource eintragen; übrige Verbindungsparameter als JSON-Objekt angeben.
4. **Zuordnen** beziehungsweise **Gerät speichern**.
5. Status und Plausibilitätsmeldung in der Tabelle prüfen.

Die folgenden Beispiele zeigen den **vollständigen gespeicherten Verbindungsdatensatz**. Ist das separate Feld **VISA-Ressource** sichtbar, `resource` dort eintragen; in **Weitere Verbindungsparameter** nur die übrigen Angaben wie `model` oder Timeout ergänzen.

```json
{"resource":"GPIB0::9::INSTR"}
```

```json
{"model":"HMP2030","resource":"TCPIP0::192.0.2.20::5025::SOCKET"}
```

```json
{"model":"Matrix200","portName":"{{station.COMPort_Debug}}"}
```

`192.0.2.20` ist nur eine Beispieladresse. Die Modelleinstellung muss bei HMP und Matrix zum Katalogmodell passen. Für einen Matrix-Leser einen eigenen sinnvoll benannten Portalias verwenden; der letzte Ausschnitt demonstriert nur die Referenzsyntax.

Der Editor prüft Daten und Profilkompatibilität, **nicht** die Erreichbarkeit des Geräts. Eine Meldung „in Ordnung“ ist kein erfolgreicher `*IDN?`-Test und kein Verdrahtungsnachweis.

**Als nicht eingebaut markieren** entfernt die Stationszuordnung, nicht das Gerät aus dem Typ. Pläne, die es benötigen, können danach dort nicht laufen.

### Duplizieren und importieren

- **Duplizieren** kopiert Geräte, Adressen und Einstellungen auf einen freien Systemnamen. Adressen danach unbedingt korrigieren; zwei Prüfplätze sollen nicht versehentlich dieselben physischen Geräte bedienen.
- **Auswahl exportieren** erzeugt YAML im Textfeld. Inhalt extern als Datei sichern.
- **Als neues Prüfsystem importieren** legt aus dem Textfeld einen neuen Datensatz an. Zuerst muss der im YAML genannte Typ existieren.
- Der Stations-YAML-Bereich erscheint bei ausgewähltem System. Ohne bestehende Station zunächst eine Station manuell anlegen.
- Ein System ohne Typ wird für ältere Pläne unterstützt; für neue Anlagen den Typworkflow benutzen.

### `devices.json`

Dieses optionale Format enthält ausschließlich lokale VISA-Adressen pro Gerätekennung. Es ist kein vollständiger Stationsexport.

- **devices.json erzeugen** schreibt die Adressen der ausgewählten Station in das Textfeld.
- **Adressen übernehmen** speichert die eingetragenen Adressen bei dieser Station in der Datenbank.
- Eine `devices.json` neben dem Bedienprogramm ersetzt dagegen nur dessen lokale Adressen, **ohne** die Datenbank zu ändern.

Das sind bewusst unterschiedliche Aktionen. Details: [Konfiguration](04-konfiguration-und-betrieb.md).

## Benannte Einstellungen

Prüfsysteme und Prüfpläne besitzen jeweils **Benannte Einstellungen**. Vorhandene Werte stehen in der Tabelle; darunter liegt das getrennte Formular **Neue Einstellung hinzufügen**. Ein Stift lädt einen Eintrag in dasselbe Formular zum Bearbeiten.

| Feld | Bedeutung |
| --- | --- |
| Verzeichnis | Gruppierungs-/Anzeigetext, z. B. Kommunikation » Debug. Kein Dateisystemordner. |
| Name | Lesbare Bezeichnung. |
| Alias | Stabiler Schlüssel ohne Leerzeichen/Sonderzeichen; beginnt mit Buchstabe oder Unterstrich. Maximal 100 Zeichen. |
| Datentyp | Text, Ja/Nein, Ganzzahl oder Zahl. |
| Wert | Inhalt passend zum Datentyp. |

**Datentypen:** Ja/Nein verlangt `true` oder `false`, nicht `Ja`, `Nein` oder `No`. Ganzzahl ist vorzeichenbehaftet mit 64 Bit. Zahl muss endlich sein und nutzt hier einen **Dezimalpunkt**. Text eignet sich für COM-Ports, Pfade, Hexadressen und Seriennummern mit führenden Nullen.

### Wann gehört ein Wert wohin?

- **Prüfsystem:** lokale Verbindungen, z. B. COM-Port oder Pfad zur installierten J-Flash-Anwendung.
- **Prüfplan:** versionsbezogene Werte, z. B. Firmware- und Projektpfad für diese Baugruppe.

Referenzen stehen in JSON-**Textwerten**:

```json
{"filePath":"{{plan.FirmwarePath}}"}
```

```json
{"portName":"{{station.ScannerPort}}","model":"Matrix200"}
```

Die Scopenamen `plan` und `station` sind kleingeschrieben. Aliase werden innerhalb ihres Scopes ohne Beachtung der Groß-/Kleinschreibung verglichen. Werte dürfen nicht wiederum andere Einstellungsreferenzen enthalten.

**Wichtig:** Messkanäle, Messbereiche, NPLC und Grenzwerte müssen bei der Freigabe bekannt sein; diese numerischen Planparameter nicht durch Textplatzhalter ersetzen. Die Auflösung ist kein frei programmierbares Makrosystem.

Änderungen an Planwerten machen eine alte Vorschau unbrauchbar. Freigegebene Planwerte sind eingefroren und nur über eine neue Version änderbar. Stationswerte bleiben veränderlich; bei Änderungen am Prüfplatz das Bedienprogramm neu starten, insbesondere für permanent gehaltene Adapterverbindungen.

Ein eingefrorener **Pfad** friert nicht den **Dateiinhalt** ein. Firmware versioniert und unveränderlich ablegen. Keine Zugangsdaten eintragen: Einstellungen können in YAML, Ausführungsplänen und Logs sichtbar werden.

Bei beschädigtem gespeichertem JSON zeigt der Bereich eine Fehlermeldung. Seine Eingaben bleiben verborgen, damit die Daten nicht mit einer scheinbar leeren Liste überschrieben werden. Daten durch einen Verantwortlichen korrigieren lassen und neu laden; die übrige Editorverbindung bleibt bestehen.

## Prüfpläne

**Seite:** `/plans`

### Einen Entwurf erstellen

1. Artikelnummer und Fertigungszustand eingeben; **Entwurf erstellen**.
2. Entwurf auswählen, Prüfsystemtyp wählen, **Stammdaten speichern**.
3. Erst danach Geräte und Messkanäle auswählen.

Artikelnummer und FZ sind nach Erstellung nicht über das Formular änderbar. Die Planversion ist ein anderer Begriff als FZ. Sobald gerätebezogene Schritte existieren, ist die Typauswahl gesperrt.

### Schritte hinzufügen und bearbeiten

- Ein Schritt benötigt einen Namen und Typ; gerätebezogene Typen zusätzlich ein passendes Gerät.
- **Messung:** Gerät → Kanal → Messgröße auswählen. Einheit folgt aus der Messgröße. Bereich, NPLC und Grenzen eingeben. AC erlaubt keine NPLC-Angabe.
- **Andere Typen:** Parameter als JSON-Objekt eingeben. Die [vollständige Schrittreferenz](06-datenmodell-und-formate.md#schrittparameter) enthält Beispiele für alle neun Typen.
- **Breakpoint vor diesem Schritt** hat nur bei angehängtem .NET-Debugger eine Wirkung. Es ist kein bedienbarer Haltepunkt ohne Debugger; dafür gibt es Einzelschritt im Bedienprogramm.
- **Schritt hinzufügen** / **Änderungen speichern** übernehmen die Eingabe.
- Stift zum Bearbeiten; Pfeile zum Verschieben; Papierkorb zum Entfernen.

### Einheiten richtig eingeben

Messfelder des Editors unterstützen deutsche Dezimalkommas und Einheitenpräfixe. Beispiele: `3,3 V`, `10 mV`, `1 kOhm`. Intern werden SI-Werte gespeichert: 10 mV = 0,01 V. `m` und `M` sind verschieden. Leere Grenzen sind einseitig beziehungsweise ohne Grenzprüfung; ein leeres Bereichsfeld bedeutet AUTO/Gerätestandard, nicht null Volt.

Nicht verwechseln: **JSON-Zahlen und benannte Zahlenwerte verwenden Dezimalpunkte**, z. B. `3.3`.

### Kanalbelegung und Eignung

- Die Seite zeigt verfügbare und nicht lauffähige Stationen des gewählten Typs. Fehlende Geräte oder Einstellungsreferenzen werden genannt.
- Kanalbelegung berücksichtigt Messungen, geschlossene Relais und im Profil beschriebene Konflikte.
- Verschiedene Messfunktionen auf einem Kanal werden als Warnung angezeigt; sie können durch Umkonfigurieren zulässig sein.
- Explizite Kanalpaar-Konflikte und offene sicherheitsrelevante Profilangaben sperren die Freigabe.
- Bei Vierdrahtmessungen kann der Sense-Kanal zusätzlich belegt sein. Kein zweites Signal auf diesen Kanal legen.

Eine lauffähige Zuordnung ist keine Aussage darüber, ob Firmwaredateien existieren oder der physische Aufbau korrekt ist.

## Freigabe und neue Versionen

```text
Entwurf -> Vorschau prüfen -> Freigegeben -> Zurückgezogen
								  |
								  +-> Neue Version daraus erstellen -> neuer Entwurf
```

1. Gespeicherte Schritte, Reihenfolge, Einheiten, Grenzen und Einstellungen kontrollieren.
2. **Vorschau erstellen** und Init / Schritte / Abschluss prüfen.
3. Warnungen beheben; **Prüfplan freigeben**.
4. Es darf für `(ArtNr, FZ)` nur **eine** freigegebene Version geben, unabhängig vom Prüfsystemtyp.

Eine Freigabe ersetzt keine andere Version automatisch. Für eine neue Version den neuen Entwurf zunächst vorbereiten; zum Umschalten die alte Freigabe kontrolliert zurückziehen und die neue Version freigeben. Dazwischen gibt es keinen ausführbaren Plan. Das Umschalten ist keine atomare gemeinsame Aktion; den Produktionszeitpunkt abstimmen.

Freigegebene und zurückgezogene Versionen sind nicht editierbar. **Neue Version daraus erstellen** übernimmt Schritte und Planwerte, vergibt neue IDs und die nächste Version. Danach erneut Vorschau und Freigabe durchführen.

Bereits laufende Prüfungen verwenden ihren vorbereiteten Plan weiter. Das Bedienprogramm liest vor dem nächsten Lauf erneut; ein inzwischen zurückgezogener Plan ist dann nicht mehr startbar. Zurückziehen ist kein Not-Aus für aktive Läufe.

## Debug-Berechtigungen

Unter `/debug-users`:

1. Kürzel mit 2–20 Zeichen A–Z/0–9 eingeben.
2. Passwort mit mindestens 6 Zeichen und Wiederholung eingeben.
3. **Speichern**: legt die Berechtigung an oder setzt ein neues Passwort.
4. Über **Entfernen** und Bestätigung entziehen.

Gespeichert werden Hash und Salt, kein lesbares Passwort. Vergessene Passwörter werden neu gesetzt. Die Berechtigung erlaubt den **Debugmodus des Bedienprogramms**, nicht eine Editorrolle. Der Editor selbst besitzt derzeit keine eingebaute Anmeldung; Zugriff administrativ absichern.

## Vor einer Freigabe abhaken

- [ ] Artikel/FZ, Typ und Gerätekennungen stimmen.
- [ ] Reale Kanäle, Strom-/Vierdrahtzuordnung und Verdrahtung sind geprüft.
- [ ] Einheiten und SI-Werte sind plausibel; Grenzwerte sind fachlich freigegeben.
- [ ] Firmware-/Projektdateien sind versioniert, keine Geheimnisse in Einstellungen.
- [ ] Vorschau ist aktuell, Abschluss enthält alle benötigten Safe States.
- [ ] Geräte und benötigte Werte sind auf den vorgesehenen Stationen vorhanden.
- [ ] Hardwareprüfung und Freigabeentscheidung sind betrieblich dokumentiert.

**Quelleneinstieg:** [Editorseiten](../src/Testpilot.Editor/Components/Pages), [NamedSettingsEditor](../src/Testpilot.Editor/Components/NamedSettingsEditor.razor), [TestPlanService](../src/Testpilot.Data/TestPlanService.cs).
