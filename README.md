# ERPDetektiv Diagnose Community

Lokales, quelloffenes Diagnosewerkzeug für technische Snapshots von
Sage-100-/SQL-Server-Systemen. Es erfasst einen Windows-Basissnapshot, SQL- und
Sage-Metadaten, prüft objektive Basischecks und erzeugt ein versioniertes
Diagnosepaket samt lesbarer HTML-Zusammenfassung. Daten werden ausschließlich
lokal verarbeitet; es gibt keine Telemetrie und keine Netzwerkübertragung.

Lizenz: [Apache License 2.0](LICENSE).

## Schnellstart

```powershell
dotnet run --project .\src\ERPDetektiv.Cli -- --output .\diagnose.zip --pseudonymize
```

Alternativ lässt sich `ERPDetektiv.Diagnose.slnx` mit Visual Studio öffnen und
`ERPDetektiv.Gui` als Startprojekt ausführen. Die WPF-Anwendung führt durch
Server, Anmeldung, Verbindungstest, Datenbankauswahl und ZIP-/HTML-Export.
Datenbankliste und Export werden erst nach einem erfolgreichen Verbindungstest
freigeschaltet; ein eingegebenes Kennwort bleibt nur im Arbeitsspeicher.

Details zum Ablauf stehen unter [GUI-Workflow](docs/gui-workflow.md).

## SQL-Snapshot

Der Connection String wird ausschließlich als Umgebungsvariable übergeben. Er
wird weder protokolliert noch exportiert:

```powershell
$env:ERPDETEKTIV_SQL_CONNECTION = "Server=sqlsrv01;Database=master;Integrated Security=True;Encrypt=True"
dotnet run --project .\src\ERPDetektiv.Cli -- --sql-server sqlsrv01 --database ERPDaten --output .\diagnose.zip
```

Benötigt werden `VIEW SERVER STATE` und `VIEW DATABASE STATE` in den
ausgewählten Datenbanken – kein `sysadmin`. Fehlt eine Berechtigung, entfällt
nur der betroffene Bereich: Der Collector meldet `Partial` und benennt im
Erfassungsstatus, was nicht gelesen werden konnte.

## Vorabcheck

Vor dem eigentlichen Lauf prüft `--preflight` in wenigen Sekunden Exportpfad,
Windows-Inventur, SQL-Erreichbarkeit samt Mindestberechtigungen und
Sage-Erkennung, ohne ein Paket zu schreiben:

```powershell
dotnet run --project .\src\ERPDetektiv.Cli -- --preflight --database ERPDaten
```

In der Oberfläche leistet das der Knopf **Vorabcheck**. Auf einem fremden
Kundensystem ist damit sofort klar, welche Teile des Pakets vollständig werden.

## Fallnotizen

Freiwillige Angaben zum Fall - Symptom, seit wann, betroffener Bereich, letzte
Änderungen - werden als `notes.json` beigelegt und stehen im HTML-Bericht vor
den Messwerten. Für eine spätere Analyse ist das oft der entscheidende Kontext.

```powershell
dotnet run --project .\src\ERPDetektiv.Cli -- --symptom "Auftragserfassung hängt beim Speichern"
```

Umfangreichere Angaben nimmt `--notes-file <pfad>` als JSON entgegen; in der
Oberfläche gibt es dafür den Reiter **Fallnotizen**. Der Text durchläuft
dieselbe Secret-Filterung und Pseudonymisierung wie der übrige Export.

## CLI-Optionen und Exit-Codes

`--help` zeigt alle Optionen. Die Exit-Codes sind für Skripte und Support-Fälle
gedacht:

| Code | Bedeutung |
| --- | --- |
| 0 | Diagnose vollständig |
| 1 | Fehlerhafte Aufrufparameter |
| 2 | Diagnose oder Export fehlgeschlagen |
| 3 | Paket geschrieben, mindestens ein Collector unvollständig; Vorabcheck mit Einschränkungen |
| 4 | Abgebrochen |

## Diagnosepaket

Das ZIP enthält `manifest.json` mit Formatversion, Tool-Version,
SHA-256-Prüfsummen und Pseudonymisierungskennzeichen, die einzelnen Snapshots,
`findings.json`, `collection-status.json` und `summary.html`. Wurden
Fallnotizen eingetragen, kommt `notes.json` hinzu.

`--pseudonymize` ersetzt **Host- und Servernamen** durch stabile Kennungen –
im gesamten Paket, auch in Findings und Evidence. **Datenbank-, Dienst- und
Komponentennamen bleiben bewusst lesbar**, weil sie das technische Umfeld
erkennbar machen und für die Diagnose wertvoll sind. Die vollständige
Festlegung samt ihrer Grenzen steht in
[Erfasste Daten und Berechtigungen](docs/collected-data-and-permissions.md).

## Entwicklung

```powershell
dotnet build ERPDetektiv.Diagnose.slnx -warnaserror
dotnet test .\tests\ERPDetektiv.Tests --collect:"XPlat Code Coverage"
```

Beiträge sind willkommen – siehe [CONTRIBUTING](CONTRIBUTING.md).
Sicherheitslücken bitte nicht öffentlich melden, sondern über
[SECURITY.md](SECURITY.md).

CLI und GUI verwenden dieselbe zentrale Produktzusammenstellung; damit laufen
bei gleichen Eingaben dieselben Collector, Vorabprüfungen und Checks.

## Weitere Dokumentation

- [Entwicklungsfahrplan](docs/development-roadmap.md)
- [GitHub Pages einrichten](docs/github-pages.md)
- [Release- und Runtime-Strategie](docs/release-packaging.md)
- [Wenn Windows die Ausführung blockiert](docs/windows-ausfuehrung-blockiert.md) –
  Diagnose und Freigabe bei ASR, Mark of the Web, AppLocker und Virenschutz
- [Entwicklungs-SQL-Server](docs/development-sql-server.md)
