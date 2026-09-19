# Mitwirken

Danke für dein Interesse an ERPDetektiv Diagnose. Das Werkzeug wächst an echten
Sage-100-Fällen. Wenn dir bei einer Analyse etwas fehlt oder du eine
Verbesserung beitragen möchtest, freuen wir uns darauf.

## Leitgedanken

1. **Fakten vor Deutung.** Collector sammeln Daten, Checks bewerten sie. Ein
   Collector erzeugt keine Findings, ein Check greift nicht selbst aufs System
   zu.
2. **Nur lesen.** Sage 100, SQL Server und Windows bleiben unverändert.
3. **Lokal arbeiten.** Keine Telemetrie, kein automatischer Versand. Den Export
   startet immer die Person am Rechner.
4. **Lücken sichtbar machen.** Fehlt eine Berechtigung oder Komponente, wird
   das als Status festgehalten; der übrige Lauf geht weiter.
5. **Warnungen müssen belastbar sein.** Ein unzuverlässiger Check hilft nicht.

## Datenschutz bei neuen Collectorn und Checks

Vor jedem neuen Datenpunkt bitte fragen: *Kann dieser Wert Geschäfts-,
Zugangs- oder personenbezogene Daten enthalten?* Wenn Zweifel bleiben, gehört
er nicht in die Erfassung.

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

Ist `ERPDETEKTIV_TEST_SQL_CONNECTION` gesetzt, laufen zusätzlich die
SQL-Integrationstests gegen diese Instanz.

- Warnungen sind Fehler (`TreatWarningsAsErrors`); bitte keine Unterdrückung
  ohne Begründung im Code.
- Neue Checks brauchen Tests für den unauffälligen, den warnenden **und** den
  unvollständigen Fall.
- Deutschsprachige Texte in Oberfläche, Bericht und Findings; englische
  Bezeichner im Code.

## Pull Requests

Beschreibe kurz, welches reale Problem dein Beitrag sichtbar macht. Ein Satz
aus der Praxis ("Das musste ich bei der letzten Analyse manuell heraussuchen")
hilft mehr als eine lange Featureliste. Mit deinem Beitrag stimmst du der
Veröffentlichung unter der Apache License 2.0 zu.
