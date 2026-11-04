# 8. Treiber und Geräteprofile

[Dokumentationsübersicht](README.md) · Zurück: [Optimizer/Sequencer](07-optimizer-und-sequencer.md) · Weiter: [Entwicklung](09-entwicklung-und-erweiterung.md)

## Inhalt

- [Modell, Profil, Treiber und Verbindung](#modell-profil-treiber-und-verbindung)
- [Interfaces, Sperren und Tracing](#interfaces-sperren-und-tracing)
- [Katalog und Implementierungsstand](#katalog-und-implementierungsstand)
- [Geräteverbindungen](#geräteverbindungen)
- [Modulprofile und Zustand](#modulprofile-und-zustand)
- [Hardware in Betrieb nehmen](#hardware-in-betrieb-nehmen)

## Modell, Profil, Treiber und Verbindung

| Begriff | Beispiel | Ort |
| --- | --- | --- |
| Modell | `34970A` | Typgerät (`SystemTypeDevice.Model`). |
| Modelloptionen | Slots 1=34901A, 2=34903A, 3=34907A | Typgerät `OptionsJson`. |
| Profil | Kanäle/Funktionen/Zustandsregeln der eingebauten Module | Abstractions, eingebettetes YAML oder verifiziertes eigenes Profil. |
| Treiber | `Ag34970Scpi` | Stationsgerät `Driver`, Registryname ist exakt. |
| Verbindung | `{"resource":"GPIB0::9::INSTR"}` | Stationsgerät `ConnectionParametersJson`, optional lokaler VISA-Override. |

Dasselbe Modell kann mehrere erlaubte Treiber haben; die Station bindet einen davon. Für typbezogene reale Mainframes ergänzt die Laufvorbereitung fehlende Slotdaten aus dem Typ, sofern nicht bereits Slots/ein Profilpfad vorgegeben sind.

## Interfaces, Sperren und Tracing

`IInstrument` enthält Profil sowie `ConnectAsync`, `DisconnectAsync`, `IdentifyAsync`, `SelfCheckAsync` und `SafeStateAsync`. Zusätzliche Interfaces beschreiben echte Fähigkeiten:

| Fähigkeit | Zweck |
| --- | --- |
| `ISwitchMultimeter` | Messkanal konfigurieren, messen, Relais schalten. |
| `IPowerSupply` | Ausgang einstellen/lesen. |
| `IProgrammer` | Firmwaredatei programmieren. |
| `IBarcodeScanner` | Code lesen. |
| `ITestFixture` | Adapterzustand, Verriegelung und Freigabe mit Ergebnisanzeige. |

Profilfähigkeiten und tatsächlich implementierte Interfaces müssen zusammenpassen. Die Registry lehnt Widersprüche ab.

`IDriverHost.AcquireAsync` liefert eine `IDeviceLease`. Der Standardhost verbindet, dekoriert und hält eine `SemaphoreSlim`-Sperre bis zur Freigabe der Lease. **Nicht** zwischen Prozessen und **nicht** anhand der VISA-/IP-Adresse sperren: Ausschlaggebend ist die genaue Gerätekennung. Keine zwei unabhängigen Testprozesse auf dieselbe Hardware loslassen.

Tracing-Decorator protokolliert Operation/Befehl, Antwort und Dauer und wirft Fehler weiter. Laufprotokolle sind Diagnosemittel, keine Garantie für Paketmitschnitt jedes intern im Vendorstack gesendeten Bytes. Vertrauliche Parameter können darin auftauchen.

## Katalog und Implementierungsstand

| Katalogmodell | Reale Registrytreiber | Simulation | Stand |
| --- | --- | --- | --- |
| 34980A | Ag34980Scpi, Ag34980Dll | SimulatedSwitchMultimeter | SCPI implementiert, DLL Gerüst. |
| 34970A | Ag34970Scpi | SimulatedSwitchMultimeter | SCPI implementiert. |
| HMP2020/2030/4030/4040 | HmpScpi, HmpDll | SimulatedPowerSupply | SCPI implementiert, DLL Gerüst. |
| SensoPart | SensopartTcp | SimulatedBarcodeScanner | Legacyprotokoll implementiert. |
| Matrix120/Matrix200 | DatalogicMatrixSerial | SimulatedBarcodeScanner | Serielles Legacyprotokoll implementiert. |
| PS05-Adapter | Ps05Fixture | SimulatedFixture | Bestehende PS05-Belegung implementiert. |
| J-Link | SeggerJFlashCli | SimulatedProgrammer | Konfigurierbarer Prozessadapter. |
| SimDmm/SimPowerSupply | keine | entsprechender simulierter Treiber | Kanaloptionen erforderlich. |
| SimProgrammer/SimCodeReader/SimFixture | keine | entsprechender simulierter Treiber | Hardwarefreie Entwicklung. |

Zusätzlich ist `VisaGeneric` registriert. Er ist **kein allgemein im Editor auswählbares Messmodell** und deklariert keine fachlichen Mess-/Schaltfähigkeiten.

Alle physischen Implementierungen benötigen Prüfung am konkreten Gerät und Aufbau. „Implementiert“ heißt nicht „für jede Firmware/Modulrevision produktiv abgenommen“.

## Geräteverbindungen

### VISA und Raw-TCP

SCPI-Treiber akzeptieren genau eine Verbindung:

```json
{"host":"192.0.2.10","port":5025,"timeoutMilliseconds":5000}
```

oder:

```json
{"resource":"TCPIP0::192.0.2.10::inst0::INSTR","timeoutMilliseconds":5000}
```

`host` und `resource` nicht gleichzeitig setzen. VISA-Beispiele: `GPIB0::9::INSTR`, `TCPIP0::…::5025::SOCKET`, `TCPIP0::…::inst0::INSTR` oder herstellerspezifische USB-Ressource. Raw-TCP benötigt keine VISA-Runtime, das Gerät muss aber den Socketdienst unterstützen.

Das NuGet-Paket `IviFoundation.Visa` liefert **keine Vendor-Runtime**. Für VISA eine Implementierung mit VISA.NET ab 7.4 installieren. Die vorhandenen Plätze verwenden Keysight IO Libraries Suite. Ressource zuerst in Keysight Connection Expert mit `*IDN?` prüfen. Ohne Hardware verwenden Tests Fake-Sessions.

### 34980A und 34970A

Typoptionen:

```json
{"slots":{"1":"34901A","2":"34903A","3":"34907A"}}
```

Dieser Ausschnitt gehört zum **34970A**, nicht zum 34980A. Beim 34980A sind derzeit 34921A/34937A katalogisiert. Direkte Treiberkonfiguration verlangt Slots oder `profilePath`; offene Datenblatt-TODOs werden von den entsprechenden realen Treibern abgelehnt.

| Verhalten | 34980A | 34970A |
| --- | --- | --- |
| Kanäle | Slot×1000 + Kanal, z. B. 1003 | Slot×100 + Kanal, z. B. 103 |
| Reset bei Connect | Standard `*RST`, mit `resetOnConnect:false` abschaltbar | Standard kein Reset; nur explizit `resetOnConnect:true` |
| Messung | CONF/NPLC, ROUT:SCAN, READ? | Entsprechender Ablauf |
| Fehlerqueue | SYST:ERR? nach Befehlsgruppen | SYST:ERR? nach Befehlsgruppen |
| Safe State | ROUT:OPEN:ALL | Nur profilierte Relais-/Multiplexerkanäle öffnen |
| Digitale Ausgänge | Gerätespezifisch prüfen | 34907A Digital-I/O/DAC nicht durch Mess-Safe-State zurücksetzen |

Der 34970A vermeidet unbedachten Reset, weil dieser die 34907A-Ausgänge verändert. SelfCheck verwendet dort Identifikation/Fehlerqueue statt `*TST?`. Multiplexer in der Scanliste sind nicht als beliebig gleichzeitig geschlossene Relais zu verwenden; dafür den vorgesehenen Relaisblock nutzen.

### HMP-Netzgeräte

```json
{"resource":"TCPIP0::192.0.2.20::5025::SOCKET","model":"HMP2030","timeoutMilliseconds":5000}
```

IDN-Modell muss zum konfigurierten Modell passen. `SetPower` adressiert den Ausgang; Safe State schaltet global ab und überprüft anschließend jeden Ausgang einzeln auf OFF, bei Bedarf mit individuellem Nachschalten.

| Modell | Ausgänge | Spannung | Strom pro Ausgang | Leistung pro Ausgang / gesamt |
| --- | --- | --- | --- | --- |
| HMP2020 | 2 | 0–32 V | OUT1 10 A, OUT2 5 A | 160/80 W; gesamt 188 W |
| HMP2030 | 3 | 0–32 V | 5 A | 80 W; gesamt 188 W |
| HMP4030 | 3 | 0–32 V | 10 A | 160 W; gesamt 384 W |
| HMP4040 | 4 | 0–32 V | 10 A | 160 W; gesamt 384 W |

Spannungs-/Stromgrenzen werden geprüft, **Leistungsgrenzen in W sowie Serien-/Parallelbetrieb nicht vollständig modelliert**. Aus den beiden Maximalwerten darf daher keine beliebige gleichzeitig erlaubte Betriebseinstellung abgeleitet werden. Werte nach R&S-Datenblatt 5215.4981.32 v02.01; Befehle nach Handbuch 1178.6833.02, Ausgabe 05. Für den konkreten Gerätestand gegenprüfen.

### SensoPart TCP

```json
{"host":"192.0.2.30","triggerPort":2006,"resultPort":2005,"timeoutMilliseconds":5000,"attempts":10}
```

Zwei TCP-Verbindungen; Trigger `TRG` ohne Zeilenende, Ergebnis zeilenweise. Bis zu zehn Versuche standardmäßig; Timeout schließt Verbindungen, um verspätete Ergebnisse nicht dem nächsten Trigger zuzuordnen. Erwarteter gültiger Code: zehn Ziffern. Kein automatischer Jobwechsel oder Aufruf eines Fokussierungsprogramms.

### Datalogic Matrix seriell

```json
{"model":"Matrix200","portName":"COM3","baudRate":115200,"trigger":"R","timeoutMilliseconds":5000}
```

Matrix120/Matrix200, Standard 115200 Baud, 8N1, kein Handshake. Trigger standardmäßig `R` ohne Terminator; zehnstellige Seriennummer; keine automatische Wiederholung wie bei SensoPart. Leser vorher mit dem passenden Herstellerwerkzeug einrichten (Bestandsaufbau: Matrix200/VisiSet, Matrix120/DL.CODE). Der Treiber konfiguriert keinen Readerjob.

### PS05-Adapter

```json
{"resource":"GPIB0::9::INSTR","timeoutMilliseconds":4000}
```

Aus der Legacybelegung übernommen:

| Funktion | Befehl/Wert |
| --- | --- |
| Geschlossen erkennen | `MEAS:VOLT:DC? (@301)`; 254/255 = geschlossen |
| Verriegeln | Byte 250 auf Kanal 302 |
| Gut-/Schlechtlampe und freigeben | Byte 253 / 254 |
| Ohne Lampe freigeben / Safe State | Byte 255 |

Bytes werden mit `SOUR:DIG:DATA:BYTE …,(@302)` geschrieben. Bitbedeutungen sind im Altcode nicht vollständig dokumentiert. Vor Betrieb am realen Platz prüfen, dass 255 tatsächlich freigibt und die Eingangsmessung dem Adapterzustand entspricht. Nicht auf andere Stationen übertragen.

### J-Flash

```json
{
  "executablePath":"{{station.JFlashExecutablePath}}",
  "workingDirectory":"C:\\Testpilot\\JFlashProjects",
  "arguments":["-openprj","{{plan.ProjectPath}}","-open","{{imagePath}}","-program","-exit"],
  "timeoutSeconds":600
}
```

Program-Schritt: `{"filePath":"{{plan.FirmwarePath}}"}`. `{{imagePath}}` ist ein **eigener Treibertoken**, kein Planalias. Der Treiber setzt den vollständigen Firmwarepfad ein, startet direkt, überwacht Exitcode und beendet bei Abbruch den Prozessbaum. CLI-Syntax gegen installierte J-Flash-Version/Projekt prüfen; das Beispiel ist kein universelles Flashrezept. Legacy-Aliase wie START_ADDRESS oder SERIAL_NUMBER setzen nicht automatisch Flags.

### VisaGeneric und DLL-Gerüste

`VisaGeneric` identifiziert, bietet aber keine Schrittfähigkeiten. Safe State besteht nur aus ausdrücklich angegebenen `safeStateCommands`, z. B. `["OUTP OFF"]`; keine erfundenen Standardbefehle und kein beliebiger Plan-SCPI-Executor.

`Ag34980Dll`/`HmpDll` laden bewusst keine native Bibliothek. Connect-/Fähigkeitsaufrufe melden `NativeDriverScaffoldException`. Für spätere Implementierung DLL-Pfad, Prozessbitness, Aufrufkonvention, Signaturen, Stringkodierung, Pointerbesitz und Freigabe verifizieren. Vorhandene Header/Wrapper in `Externes` sind Referenz, kein Freibrief für ungeprüfte P/Invoke-Signaturen.

## Modulprofile und Zustand

Profile liegen unter [Profiles](../src/Testpilot.Drivers.Abstractions/Profiles). Der [Formatbeispielvertrag](../src/Testpilot.Drivers.Abstractions/DeviceProfile.schema.yaml) ist **ein kommentiertes YAML-Beispiel**, kein maschinell ausführbares JSON Schema und kein konkretes Gerät.

Wichtige Felder:

- `id`, `model`, `channelCount`, `channelNumbering`, `capabilities`.
- `state.settings`: welche Einstellungen bekannt/verfolgbar sind.
- `state.invalidatedByCommand`: welche Einstellungen ein Befehl ungültig macht.
- `state.unknownCommandInvalidatesAll`: konservative Regel für fehlende Befehle.
- `channels`: Nummer/Typ, unterstützte Funktionen, Bereiche, Konfliktkanäle und optional `fourWireSenseChannel`.
- `specialCases`, `todos`: belegte Sonderfälle und offene Prüfpunkte.

```yaml
state:
  settings: [function, range, nplc]
  invalidatedByCommand:
	relay: [function, range, nplc]
	configure: []
	measure: []
  unknownCommandInvalidatesAll: true
```

Das ist nur ein **Zustandsausschnitt**. Keine generelle Behauptung, dass jeder Relaisbefehl jedes Gerät genau so beeinflusst. Regeln müssen zum Handbuch passen.

| Modul | Profilierte Besonderheit |
| --- | --- |
| 34921A | Vierdrahtpaare 1–20 mit Sensekanal +20; Stromkanäle 41–44. |
| 34937A | 32 Relaiskanäle. |
| 34901A | Vierdrahtpaare 1–10 mit Sensekanal +10; Stromkanäle 21–22. |
| 34903A | 20 Relaiskanäle. |
| 34907A | Digital-I/O 1/2, Totalizer 3, DAC 4/5; keine normalen Mess-/Relaiskanäle. |

Modulnummern werden beim Zusammensetzen auf Slotnummerierung umgesetzt. Nicht belegte Eigenschaften nicht erfinden: `TODO: aus Datenblatt prüfen` setzen und vor realer Verwendung klären. Konflikt-/Sensekanäle müssen im zusammengesetzten Profil tatsächlich existieren.

## Hardware in Betrieb nehmen

1. Modell, Firmware, Module und Kanalverdrahtung gegen Herstellerunterlagen aufnehmen.
2. Geeignete Treibervariante und Bitness wählen; Ressourcen zuerst außerhalb Testpilot prüfen.
3. Unbelasteten beziehungsweise sicher abgesicherten Aufbau verwenden; keine produktive Baugruppe als ersten Versuch.
4. Identify, zulässige Messung, Bereich/NPLC, Relais und Fehlerqueue prüfen.
5. Safe State einschließlich aller Netzteilausgänge und Adapterfreigabe nachweisen.
6. Verbindungsfehler, Timeout und Abbruch gezielt unter kontrollierten Bedingungen testen.
7. Profilregeln gegen tatsächliche Invalidierung prüfen; unbekannte Eigenschaften konservativ behandeln.
8. Abnahme mit Hardware-/Software-/Profilversion dokumentieren; danach erst Simulation bewusst deaktivieren.

**Quellen:** [Katalog](../src/Testpilot.Drivers.Abstractions/DeviceCatalog.cs), [Interfaces](../src/Testpilot.Drivers.Abstractions/InstrumentContracts.cs), [DriverHost](../src/Testpilot.Drivers/DriverHost.cs), [Standardregistrierung](../src/Testpilot.Drivers/StandardDrivers.cs), [Implementierungen](../src/Testpilot.Drivers).
