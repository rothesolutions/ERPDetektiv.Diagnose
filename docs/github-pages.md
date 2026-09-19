# GitHub Pages

Die statische Projektseite liegt unter `site/`. Der Workflow
`.github/workflows/pages.yml` veröffentlicht sie automatisch nach jeder
Änderung an dieser Seite auf `master`.

## Einmalig nach dem Push zu GitHub

1. Das Repository in GitHub öffnen.
2. **Settings** → **Pages** öffnen.
3. Bei **Build and deployment** als Quelle **GitHub Actions** auswählen.
4. Den Workflow **Deploy GitHub Pages** einmal starten oder auf `master` pushen.
5. Die von GitHub angezeigte Pages-URL in Repository-Beschreibung, README und
   dem ersten Release verlinken.

Die Seite verwendet absichtlich keine Abhängigkeiten, kein JavaScript und kein
externes Tracking. Sobald der endgültige GitHub-Repositoryname feststeht,
sollten die Buttons für Quellcode und Releases auf dessen URLs ergänzt werden.
