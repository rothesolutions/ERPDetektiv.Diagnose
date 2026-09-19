# Sicherheit

## Sicherheitslücken melden

Bitte melde Sicherheitslücken **nicht** über ein öffentliches GitHub-Issue.

Schicke stattdessen eine E-Mail an **security@erpdetektiv.de**. Hilfreich sind:

- eine Beschreibung der Lücke und ihrer Auswirkung,
- die betroffene Version beziehungsweise der Commit,
- eine möglichst knappe Reproduktionsanleitung,
- falls vorhanden ein Diagnosepaket, aus dem du zuvor alle realen Host-,
  Server- und Kundenbezeichner entfernt hast.

Wir bestätigen den Eingang innerhalb von fünf Werktagen und melden uns
danach mit einer Einschätzung. Bitte gib uns Gelegenheit, eine korrigierte
Version bereitzustellen, bevor du Details veröffentlichst.

## Was wir als Sicherheitslücke betrachten

ERPDetektiv Diagnose läuft mit den Rechten des ausführenden Technikers auf
produktiven ERP-Systemen und erzeugt Diagnosepakete, die weitergegeben
werden. Besonders relevant sind deshalb:

- **Datenabfluss in den Export:** Zugangsdaten, Connection-String-Geheimnisse,
  Secrets, Zertifikat-Thumbprints, Geschäfts- oder Personendaten in ZIP,
  HTML-Bericht oder NDJSON-Aufzeichnung.
- **Wirkungslose Pseudonymisierung:** Bezeichner, die trotz aktivierter
  Pseudonymisierung im Klartext im Paket landen. Welche Bezeichner geschützt
  sind und welche bewusst lesbar bleiben, steht in
  [docs/collected-data-and-permissions.md](docs/collected-data-and-permissions.md).
- **Schreibende oder verändernde Zugriffe** auf Sage 100, SQL Server oder das
  Betriebssystem. Das Werkzeug ist ausschließlich lesend.
- **Ausführung fremden Codes**, etwa über manipulierte Pfade, Konfigurations-
  oder Eingabedaten.
- **Rechteausweitung** oder unnötig hohe Rechteanforderungen.

## Kein Sicherheitsproblem im Sinne dieser Richtlinie

- Fehlende Daten oder Fehlalarme in Findings – das sind normale Bugs.
- Dass lesbar gehaltene Bezeichner wie Datenbank-, Dienst- und
  Komponentennamen im Export erscheinen. Das ist eine dokumentierte
  Produktentscheidung, weil diese Namen das technische Umfeld erkennbar
  machen.
