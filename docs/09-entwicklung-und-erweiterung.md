# 9. Entwicklung und Erweiterung

[Dokumentationsübersicht](README.md) · Zurück: [Treiber](08-treiber-und-profile.md) · Weiter: [Fehlersuche](10-fehlersuche-und-debugging.md)

## Inhalt

- [Entwicklungsumgebung und Konventionen](#entwicklungsumgebung-und-konventionen)
- [Bauen und testen](#bauen-und-testen)
- [Einen Custom-Schritt ergänzen](#einen-custom-schritt-ergänzen)
- [Einen neuen Schritttyp ergänzen](#einen-neuen-schritttyp-ergänzen)
- [Treiber oder Modell ergänzen](#treiber-oder-modell-ergänzen)
- [Datenmodell ändern](#datenmodell-ändern)
- [Änderungscheckliste](#änderungscheckliste)

## Entwicklungsumgebung und Konventionen

.NET 10 LTS, C# mit nullable und impliziten Usings. [Directory.Build.props](../Directory.Build.props) aktiviert deterministische Builds und Warnungen als Fehler; [global.json](../global.json) steuert den SDK-Bereich. Versionsbasis ist dort 0.1.0; die Build-Informationsversion kann zusätzlich Commitinformation enthalten.

- Code, Bezeichner und XML-Dokumentation **Englisch**.
- Oberfläche und Anwenderdokumentation **Deutsch**.
- Kleine, klar benannte Methoden/Klassen; vorhandene Hilfsfunktionen nutzen, keine unnötigen Frameworkschichten.
- `Microsoft.Extensions.Logging` verwenden, Hosts binden Serilog an.
- CancellationToken durchreichen; Exceptions mit aussagekräftigem Kontext, keine stillen Ersatz-Erfolgsergebnisse.
- Gerätedaten nicht in UI-Code duplizieren; Katalog/Profile verwenden.
- Keine realen Geräte/SAP-Zugänge in Unit-Tests voraussetzen.
- Keine Zugangsdaten, Produktionsdaten oder lokalen Pfade mit Geheimnissen versionieren.

Die Entwicklungsdatenbank wird nicht durch `EnsureCreated` an Migrationen vorbei angelegt. Bestehende Tests benutzen migrierte SQLite-Datenbanken; dieses Muster beibehalten.

## Bauen und testen

Alle Befehle aus dem Repository-Hauptverzeichnis:

```powershell
dotnet tool restore --tool-manifest .\dotnet-tools.json
dotnet restore Testpilot.slnx
dotnet build Testpilot.slnx
dotnet test tests\Testpilot.Tests\Testpilot.Tests.csproj
```

Für einen gezielten Durchlauf, beispielsweise Editorpräsentation und Einstellungen:

```powershell
dotnet test tests\Testpilot.Tests\Testpilot.Tests.csproj --filter 'FullyQualifiedName~EditorPresentationTests|FullyQualifiedName~NamedSettingsEditorTests|FullyQualifiedName~NamedSettingsTests'
```

In Visual Studio Test Explorer lassen sich xUnit-Tests einzeln ausführen und debuggen. Wenn Tests noch nicht angezeigt werden, zunächst bauen und auf Wiederentdeckung warten. Test- und Projektfilter bewusst wählen: Ein grüner Lauf mit **null gefundenen Tests** ist kein Nachweis.

### Welche Tests brauche ich?

| Änderung | Mindestens prüfen |
| --- | --- |
| Einheiten/Grenzen | Präfixe, Kultur, leere/einseitige Grenzen, inklusive Randwerte, NaN/Unendlich. |
| Freigabe/Versionierung | Eine Freigabe pro Artikel/FZ, unveränderliche Snapshots, neue Version, konkurrierende Bearbeitung. |
| Einstellungen | Typvalidierung, Aliasvergleich, fehlende/falsche Referenzen, JSON-Escaping, Station vs Plan, Freigabesnapshot. |
| Profile/Optimizer | Einmalige/wechselnde Konfiguration, Invalidierung, Kanalpaar/Konflikt, deterministische Reihenfolge. |
| Treiber | Erfolgsantwort, ungültige Antwort, Gerätefehler, Timeout/Abbruch, Disconnect/Safe State; Fake-Transport statt Hardware. |
| Sequencer | Pass/Fail/Error/Abort, Skipped, Cleanup bei Teilinitialisierung, Speicher-/Exportfehler, Simulation. |
| Editor | Rendering, Auswahl/Bearbeiten/Abbrechen, ReadOnly, Fehlermeldungen, Navigation; ergänzend echte Browserbedienung. |
| Operator | Auftrag/Plan-Neuladen, Seriennummer, UI-Zustand, Debuglogin und Adapterautomat ohne Hardware. |
| Datenbank | Migration je Provider und Unique-/Concurrency-Regeln; echte Serverintegration separat. |

Das Testprojekt enthält unter anderem Phasen-/Treiber-/Planungs-/Operator-Tests sowie `NamedSettingsEditorTests`, `EditorPresentationTests` und `NamedSettingsTests`. Testnamen/Anzahl können sich ändern; den tatsächlich gelaufenen Umfang protokollieren.

### Blazor-Falle bei Stringparametern

Eine C#-Eigenschaft nicht versehentlich als Stringliteral übergeben:

```razor
<NamedSettingsEditor SettingsJson="@selectedStation.SettingsJson" />
```

Ohne `@` würde der String `selectedStation.SettingsJson` statt des gespeicherten JSON übergeben. Vollständige Komponenten benötigen zusätzlich ihre jeweiligen Event-/Statusparameter; der Ausschnitt erklärt nur die Bindung. `OnParametersSet` kann bei Rerendern mehrfach laufen: Fehlerzustand und ungespeicherte Eingaben nicht unbeabsichtigt zurücksetzen.

## Einen Custom-Schritt ergänzen

Für einen Sonderfall zunächst `ICustomStep` verwenden, bevor ein neuer allgemeiner Schritttyp eingeführt wird. Der Handler ist normaler geprüfter C#-Code im Sequencerprozess.

```csharp
using System.Text.Json;
using Testpilot.Core;
using Testpilot.Sequencer;

public sealed class ExampleStep : ICustomStep
{
	public Task<StepExecutionData> ExecuteAsync(
		JsonElement parameters,
		DeviceSession devices,
		RunContext runContext,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var message = parameters.GetProperty("message").GetString()
			?? throw new ArgumentException("The message parameter must be a string.");
		return Task.FromResult(new StepExecutionData(
			TextValue: message,
			Result: StepOutcome.Pass));
	}
}
```

Dies ist ein bewusst harmloses Schnittstellenbeispiel, keine echte Sonderprüfung. Für reale Handler Parameter vollständig validieren, Grenzen fachlich auswerten, Logging einbinden und Fehler nicht als Pass maskieren. `runContext.Simulated` beachten, wenn der Handler selbst externe Nebenwirkungen hat.

### Registrierung und Verwendung

```csharp
var customSteps = new CustomStepRegistry();
customSteps.Register("Example", new ExampleStep());
```

Diese Registry tatsächlich an den Host/Runner weitergeben. Derzeit erzeugen [Operatorstart](../src/Testpilot.Operator/App.xaml.cs) und [RunPlan](../tools/Testpilot.RunPlan/Program.cs) jeweils eine leere `CustomStepRegistry`. Für beide gewünschten Startwege registrieren; eine nur im Test angelegte Registry ändert den ausgelieferten Host nicht.

Schrittparameter:

```json
{"handlerKey":"Example","parameters":{"message":"Testnachricht"}}
```

Schlüssel ist ordinal und groß-/kleinschreibungsabhängig. Keine automatische Plugin-/Assemblysuche.

Geräte nur über `DeviceSession.GetCapability<T>(deviceKey)` verwenden und benötigte Geräte im Plan verfügbar machen. Keine eigene zweite Verbindung auf dieselbe Hardware öffnen. Die derzeitige Simulation kann ein nur aus CustomCode benötigtes Gerät nicht immer automatisch ableiten; entsprechende Einschränkungen testen, nicht still umgehen.

Nach CustomCode verwirft der Optimizer bekannte Messzustände konservativ. Ein Handler darf Hardware nicht unbeaufsichtigt in einem gefährlichen Zustand hinterlassen; Runner-Cleanup allein ersetzt keine lokale Fehlerbehandlung bei eigenen Ressourcen.

## Einen neuen Schritttyp ergänzen

Das ist eine Querschnittsänderung. Nicht allein einen neuen Enumwert und Executor hinzufügen:

1. `StepType` und typisierte Parameter in Core ergänzen.
2. Strikte Serialisierung/Deserialisierung, Pflichtfelder, Wertebereiche, Geräte-/Einheitenregeln erweitern.
3. Optimizer: Fähigkeit, Gerätesammlung, Parameter-/Profilprüfung und Zustandsinvalidierung ergänzen.
4. ExecutionPlan-Vertrag/Serializer und Rückwärtskompatibilität überprüfen.
5. Kleinen Executor samt `StepExecutorRegistry`-Registrierung ergänzen.
6. Simulationstreiberzuordnung und gegebenenfalls Cleanup-Priorität ergänzen.
7. Editor: deutsche Anzeige, Eingabe, Gerätefilter und Kanalbelegung anpassen.
8. Fortschritts-/Ergebnisanzeigen in Operator/Konsole und Export prüfen.
9. Negative Tests, Abbruch/Timeout/Cleanup und ältere freigegebene Pläne testen.
10. [Schrittparameterreferenz](06-datenmodell-und-formate.md#schrittparameter) und Anwenderhinweise aktualisieren.

Wenn Änderungen an bestehenden Ausführungsplänen unvermeidlich sind, nicht ohne explizite Migrations-/Kompatibilitätsstrategie alte Freigaben umdeuten.

## Treiber oder Modell ergänzen

1. Gerätefähigkeit gegen vorhandene Interfaces abgleichen; nur bei echtem Bedarf neue Abstraktion einführen.
2. Profil in **Drivers.Abstractions** anlegen und ohne Hardware laden/validieren können.
3. Katalogmodell mit erlaubten Registrytreibern, Simulationszuordnung und Optionen ergänzen.
4. Treiber in **Drivers** implementieren: Connect/Disconnect/IDN/SelfCheck/SafeState und unterstützte Fähigkeiten.
5. Konfiguration streng validieren; `IInstrument.Profile` muss Interfaces entsprechen.
6. Über `DriverRegistry.Register(name, factory)` registrieren und im passenden Standardregistrierungspfad einbinden. Hosts verwenden `AddAllDrivers()`.
7. Bestehenden Trace-/Hostpfad verwenden; neue Fähigkeiten auch im Decorator berücksichtigen.
8. Fake-Transporttests und hardwarefreie Simulation ergänzen.
9. Hardwareabnahme separat ausführen und Einschränkungen in der [Treiberreferenz](08-treiber-und-profile.md) dokumentieren.

Keine Gerätekonstruktion in Editor oder Profil-Loader verschieben. Native DLL-Gerüste erst mit verifizierten Headern, Bitness und Ownershipregeln aktivieren.

## Datenmodell ändern

Es existieren drei providerbezogene Migrationen und Snapshots. Für eine gemeinsame Modelländerung **alle drei** aktualisieren:

```powershell
dotnet tool run dotnet-ef -- migrations add ChangeName --project src\Testpilot.Data --context SqliteTestpilotDbContext --output-dir Migrations\Sqlite
dotnet tool run dotnet-ef -- migrations add ChangeName --project src\Testpilot.Data --context SqlServerTestpilotDbContext --output-dir Migrations\SqlServer
dotnet tool run dotnet-ef -- migrations add ChangeName --project src\Testpilot.Data --context PostgreSqlTestpilotDbContext --output-dir Migrations\PostgreSql
```

`ChangeName` durch einen sprechenden englischen Namen ersetzen. Migrationen, Designerdateien und Snapshots gemeinsam versionieren. Generierten SQL-Code auf Providerunterschiede prüfen, insbesondere gefilterte Indizes und Datentypen.

Vor Anwendung auf echten Daten Upgrade auf Sicherungskopie testen. Tabellen nicht durch `EnsureDeleted`/`EnsureCreated` „reparieren“. Migrationen allein stellen weder Backups her noch erledigen sie einen Transfer zwischen Providern.

## Änderungscheckliste

- [ ] Projektgrenzen und nullable/type safety bleiben erhalten.
- [ ] Kein Hardware-/Netzwerkzwang in Unit-Tests.
- [ ] Fehlereingaben führen zu sichtbarer Diagnose, nicht stillen Defaults.
- [ ] Gleichzeitiger Zugriff und Cleanup sind getestet.
- [ ] Geänderte UI-Texte deutsch, Code/API englisch.
- [ ] Einheiten/Parameter/Profile haben genau eine maßgebliche Definition.
- [ ] Alle betroffenen Hosts, Registries, Simulationen und Anzeigen angeschlossen.
- [ ] Migrationen für alle Provider vorhanden, falls Modell geändert.
- [ ] Build und passende Tests tatsächlich erfolgreich; Umfang nennen.
- [ ] Dokumentation/Beispiele und gegebenenfalls Hardwareabnahme aktualisiert.
