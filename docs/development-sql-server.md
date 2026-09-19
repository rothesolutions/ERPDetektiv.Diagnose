# Lokaler SQL Server für Entwicklung und Tests

Die Compose-Datei in `infra/docker-compose.dev.yml` startet eine isolierte
SQL-Server-2025-Developer-Instanz ausschließlich für ERPDetektiv. Der Host-Port
ist bewusst `127.0.0.1:1533`; er kollidiert nicht mit einer möglichen lokalen
SQL-Server-Instanz auf 1433 und ist nicht aus dem Netzwerk erreichbar.

## Starten

Empfohlen ist eine nicht versionierte `.env`-Datei im Projektstamm:

```powershell
Copy-Item .\.env.example .\.env
# Danach in .env das Beispiel durch ein eigenes starkes Kennwort ersetzen.
docker compose --env-file .\.env -f .\infra\docker-compose.dev.yml up -d
```

`.env` ist in `.gitignore` eingetragen und wird nicht versioniert. Der
Container verwendet ein eigenes Docker-Volume namens `erpdetektiv_sql_data`;
`docker compose down` entfernt den Container, behält aber die
Entwicklungsdaten.

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

Den Collector anschließend nur für die Laufzeit konfigurieren:

```powershell
$env:ERPDETEKTIV_SQL_CONNECTION = "Server=localhost,1533;Database=OLDemoReweAbfD;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True"
dotnet run --project .\src\ERPDetektiv.Cli -- --sql-server localhost,1533 --database OLDemoReweAbfD --output .\diagnose.zip --pseudonymize
```

Der Collector wurde gegen diese Datenbank erfolgreich integriert getestet. Für
eine robuste Docker-/SSMS-Verbindung stets `tcp:127.0.0.1,1533` verwenden;
damit wird IPv4/TCP erzwungen.

## Beenden und bereinigen

```powershell
docker compose --env-file .\.env -f .\infra\docker-compose.dev.yml down
docker volume rm erpdetektiv_sql_data
```

Der zweite Befehl löscht die persistenten Entwicklungsdaten und ist deshalb
absichtlich getrennt.
