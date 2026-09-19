# Lokaler SQL Server für Entwicklung und Tests

Die Compose-Datei in `infra/docker-compose.dev.yml` startet eine eigene
SQL-Server-2025-Developer-Instanz für ERPDetektiv. Sie ist nur über
`127.0.0.1:1533` erreichbar: Das kollidiert nicht mit einem lokalen SQL Server
auf Port 1433 und bleibt aus dem Netzwerk heraus unsichtbar.

## Starten

Am einfachsten ist eine lokale `.env`-Datei im Projektstamm:

```powershell
Copy-Item .\.env.example .\.env
# Danach in .env das Beispiel durch ein eigenes starkes Kennwort ersetzen.
docker compose --env-file .\.env -f .\infra\docker-compose.dev.yml up -d
```

`.env` steht in `.gitignore` und wird nicht eingecheckt. Der Container nutzt
das eigene Docker-Volume `erpdetektiv_sql_data`. `docker compose down` beendet
und entfernt den Container, lässt die Entwicklungsdaten aber liegen.

Alternativ kann `MSSQL_SA_PASSWORD` als kurzlebige PowerShell-
Umgebungsvariable gesetzt werden. Die Compose-Datei benötigt in beiden Fällen
keine Änderung.

## Testdatenbank erzeugen

```powershell
docker exec erpdetektiv-sql-dev /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $env:MSSQL_SA_PASSWORD -Q "CREATE DATABASE OLDemoReweAbfD"
```

Im aktuellen Entwicklungsstand ist das bereitgestellte Backup bereits als
`OLDemoReweAbfD` wiederhergestellt. Der Container ist erreichbar unter
`tcp:127.0.0.1,1533`; das Kennwort liegt ausschließlich in der ignorierten Datei
`.env` im Projektstamm.

Für einen Lauf den Collector anschließend so konfigurieren:

```powershell
$env:ERPDETEKTIV_SQL_CONNECTION = "Server=localhost,1533;Database=OLDemoReweAbfD;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True"
dotnet run --project .\src\ERPDetektiv.Cli -- --sql-server localhost,1533 --database OLDemoReweAbfD --output .\diagnose.zip --pseudonymize
```

Der Collector ist gegen diese Datenbank integriert getestet. Für Docker oder
SSMS am besten immer `tcp:127.0.0.1,1533` verwenden; damit ist IPv4/TCP
eindeutig gewählt.

## Beenden und bereinigen

```powershell
docker compose --env-file .\.env -f .\infra\docker-compose.dev.yml down
docker volume rm erpdetektiv_sql_data
```

Der zweite Befehl löscht die persistenten Entwicklungsdaten. Er ist deshalb
separat aufgeführt.
