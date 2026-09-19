# GitHub Pages

Die statische Projektseite liegt unter `site/`. Der Workflow
`.github/workflows/pages.yml` veröffentlicht sie automatisch, wenn sich auf
`main` etwas an ihr ändert.

## Einmalig nach dem Push zu GitHub

1. Das Repository in GitHub öffnen.
2. **Settings** → **Pages** öffnen.
3. Bei **Build and deployment** als Quelle **GitHub Actions** auswählen.
4. Den Workflow **Deploy GitHub Pages** einmal starten oder auf `main` pushen.
5. Die von GitHub angezeigte Pages-URL in Repository-Beschreibung, README und
   dem ersten Release verlinken.

Die Seite kommt ohne Abhängigkeiten, JavaScript und externes Tracking aus. Die
Buttons für Quellcode und Releases sollten auf die tatsächlichen GitHub-URLs
zeigen.
