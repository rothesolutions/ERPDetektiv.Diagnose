# Mitwirken

Danke für dein Interesse an ERPDetektiv Diagnose. Das Werkzeug entsteht aus
echten Sage-100-Analysen; Beiträge aus der Praxis sind ausdrücklich willkommen.

## Leitgedanken

1. **Fakten sammeln, nicht interpretieren.** Collector liefern Daten, Checks
   bewerten sie. Ein Collector erzeugt keine Findings, ein Check greift nicht
   auf das System zu.
2. **Nur lesen.** Das Werkzeug verändert Sage 100, SQL Server und Windows
   nicht – auch nicht "nur kurz zum Testen".
3. **Local first.** Keine Netzwerkübertragung, keine Telemetrie. Der Export
   erfolgt bewusst durch die Anwenderin oder den Anwender.
4. **Fehlende Daten sind ein Status, kein Absturz.** Fehlt eine Berechtigung
   oder eine Komponente, wird das als Collector- beziehungsweise
   Feature-Status dokumentiert; der Rest der Diagnose läuft weiter.
5. **Keine unbegründete Warnung.** Ein Check, der bei unvollständigen Daten
   warnt, schadet mehr als er nützt.

## Datenschutz bei neuen Collectorn und Checks

Vor jedem neuen Datenpunkt gilt die Frage: *Kann dieser Wert Geschäftsdaten,
Zugangsdaten oder personenbezogene Daten enthalten?* Im Zweifel nicht erfassen.

Welche Bezeichner pseudonymisiert werden und welche bewusst lesbar bleiben,
ist in [docs/collected-data-and-permissions.md](docs/collected-data-and-permissions.md)
festgelegt. Erfasst ein Beitrag einen neuen geschützten Bezeichner, muss er
über `IdentifierRegistry` registriert werden – dann wird er automatisch überall
ersetzt, auch in Finding-Freitext und Evidence.

Jeder neue Datenpunkt wird in `docs/collected-data-and-permissions.md`
dokumentiert, inklusive der dafür nötigen Berechtigung.

## Entwicklung

```powershell
dotnet build ERPDetektiv.Diagnose.slnx -warnaserror
dotnet test tests\ERPDetektiv.Tests\ERPDetektiv.Tests.csproj
```

Für Tests gegen eine echte SQL-Instanz siehe
[docs/development-sql-server.md](docs/development-sql-server.md). Ist
`ERPDETEKTIV_TEST_SQL_CONNECTION` gesetzt, laufen zusätzlich die
SQL-Integrationstests.

- Warnungen sind Fehler (`TreatWarningsAsErrors`); bitte keine Unterdrückung
  ohne Begründung im Code.
- Neue Checks brauchen Tests für den unauffälligen, den warnenden **und** den
  unvollständigen Fall.
- Deutschsprachige Texte in Oberfläche, Bericht und Findings; englische
  Bezeichner im Code.

## Pull Requests

Beschreibe, welches reale Problem der Beitrag sichtbar macht. Ein Satz aus der
Praxis ("das musste ich bei der letzten Analyse manuell heraussuchen") ist
wertvoller als eine Featureliste. Mit dem Beitrag stimmst du zu, dass er unter
der Apache License 2.0 veröffentlicht wird.
