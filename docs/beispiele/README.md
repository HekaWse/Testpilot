# Beispieldateien

[Dokumentationsübersicht](../README.md) · [Schritt-für-Schritt-Anleitung](../01-einstieg.md)

| Datei | Verwendung |
| --- | --- |
| [demo-pruefsystemtyp.yaml](demo-pruefsystemtyp.yaml) | Inhalt im Editor unter **Prüfsystemtypen → YAML-Import und -Export** einfügen; als neuen Typ importieren. |
| [demo-pruefsystem.yaml](demo-pruefsystem.yaml) | Danach unter **Prüfsysteme → YAML-Import und -Export** einfügen; als neues System importieren. Dieser Bereich wird bei ausgewähltem System angezeigt. Ohne bestehendes System zunächst DEMO-1 manuell nach Kapitel 1 anlegen. |
| [demo-operator-appsettings.json](demo-operator-appsettings.json) | Vollständiges Beispiel für das Bedienprogramm mit SAP-Mock und Simulation. Nur in einer separaten Demo-Installation als `appsettings.json` verwenden; Datenbankpfad bei Bedarf anpassen. |

Die YAML-Dateien enthalten keine echten Geräteadressen. Der simulierte Messwert beträgt 3,3 V auf Kanal 1003 bei `VOLT:DC`. Den zugehörigen Prüfplan mit Grenzen 3,2 bis 3,4 V im Editor erstellen; es gibt keinen Prüfplan-YAML-Import.

Die JSON-Beispielkonfiguration verwendet die separate Datenbank `C:\TestpilotDemo\testpilot.db`. Den Ordner zuerst anlegen und genau diese Datenbank migrieren. **Keine produktive Konfiguration überschreiben.** Bei Verwendung im Quellcodeprojekt ist der Ausgabeordner des Bedienprogramms nicht dauerhaft: Ein Build kann dort `appsettings.json` erneut aus dem Projekt kopieren. Für Entwicklung daher die Umgebungsvariablen aus Kapitel 1 verwenden.

`Simulation:Enabled=true` verhindert keine SAP-Netzwerkaufrufe und keine `External`-/`CustomCode`-Ausführung. Deshalb sind in diesem Beispiel SAP-REST und externer SAP-Adapter deaktiviert; der Beispielplan benötigt ausschließlich eine Messung mit simuliertem Treiber.
