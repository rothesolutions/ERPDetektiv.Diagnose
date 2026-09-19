# ERPDetektiv Diagnose Community

ERPDetektiv Diagnose hilft dabei, den technischen Zustand einer Sage-100- und
SQL-Server-Umgebung nachvollziehbar festzuhalten. Das Tool sammelt einen
Snapshot von Windows, SQL Server und Sage 100, prüft einige klar belegbare
Grundlagen und schreibt alles in ein ZIP-Paket mit HTML-Zusammenfassung.

Die Daten bleiben auf dem Rechner, auf dem das Tool läuft. Es gibt keine
Telemetrie und keinen automatischen Versand.

Lizenz: [Apache License 2.0](LICENSE).

## Schnellstart

```powershell
dotnet run --project .\src\ERPDetektiv.Cli -- --output .\diagnose.zip --pseudonymize
```

Wer lieber mit einer Oberfläche arbeitet, öffnet
`ERPDetektiv.Diagnose.slnx` in Visual Studio und wählt `ERPDetektiv.Gui` als
Startprojekt. Die Anwendung führt durch Server, Anmeldung, Verbindungstest,
Datenbankauswahl und Export. Ein Kennwort bleibt dabei nur so lange im Speicher,
wie die Verbindung besteht.

Details zum Ablauf stehen unter [GUI-Workflow](docs/gui-workflow.md).

## SQL-Snapshot

Der Connection String kommt über eine Umgebungsvariable. Er wird weder
protokolliert noch in das Diagnosepaket übernommen:

```powershell
$env:ERPDETEKTIV_SQL_CONNECTION = "Server=sqlsrv01;Database=master;Integrated Security=True;Encrypt=True"
dotnet run --project .\src\ERPDetektiv.Cli -- --sql-server sqlsrv01 --database ERPDaten --output .\diagnose.zip
```

Benötigt werden `VIEW SERVER STATE` und `VIEW DATABASE STATE` in den gewählten
Datenbanken; `sysadmin` ist nicht nötig. Fehlt eine Berechtigung, bleibt der
Rest der Diagnose nutzbar. Im Erfassungsstatus steht dann nachvollziehbar,
welcher Bereich fehlt.

## Vorabcheck

Mit `--preflight` lässt sich vorab prüfen, ob Exportpfad, Windows-Inventur,
SQL-Verbindung, Mindestberechtigungen und Sage-Erkennung passen. Dabei wird
noch kein Paket geschrieben:

```powershell
dotnet run --project .\src\ERPDetektiv.Cli -- --preflight --database ERPDaten
```

In der Oberfläche heißt derselbe Schritt **Vorabcheck**. Gerade auf einem
fremden System zeigt er früh, welche Teile der Diagnose vollständig vorliegen
werden.

## Fallnotizen

Bei Bedarf lassen sich Symptom, Beginn, betroffener Bereich und letzte
Änderungen mitgeben. Diese freiwilligen Angaben landen als `notes.json` im
Paket und stehen im HTML-Bericht vor den Messwerten. Oft ist genau dieser
Kontext für die spätere Analyse entscheidend.

```powershell
dotnet run --project .\src\ERPDetektiv.Cli -- --symptom "Auftragserfassung hängt beim Speichern"
```

Für ausführlichere Angaben gibt es `--notes-file <pfad>` mit JSON-Inhalt und
in der Oberfläche den Reiter **Fallnotizen**. Der Text durchläuft dieselbe
Secret-Filterung und Pseudonymisierung wie der übrige Export.

## CLI-Optionen und Exit-Codes

`--help` zeigt alle Optionen. Die folgenden Exit-Codes helfen bei Skripten und
Support-Fällen:

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

Mit `--pseudonymize` werden **Host- und Servernamen** im gesamten Paket durch
stabile Kennungen ersetzt – auch in Findings und Evidence. **Datenbank-,
Dienst- und Komponentennamen bleiben lesbar**, weil sie für die technische
Einordnung wichtig sind. Die vollständige Abgrenzung steht in
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
