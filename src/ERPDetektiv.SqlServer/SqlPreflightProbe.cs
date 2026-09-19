using ERPDetektiv.Contracts;
using Microsoft.Data.SqlClient;

namespace ERPDetektiv.SqlServer;

/// <summary>Prueft Erreichbarkeit und Mindestberechtigungen der SQL-Instanz.</summary>
/// <remarks>
/// Jede Pruefung entspricht einem Bereich, den der Collector spaeter braucht. Faellt hier
/// etwas aus, weiss die Anwenderin vorher, welcher Teil des Pakets duenn bleiben wird –
/// statt es hinterher am Erfassungsstatus abzulesen.
/// </remarks>
public sealed class SqlPreflightProbe(string? connectionString, string? database) : IPreflightProbe
{
    public string Id => "sqlserver";

    public async Task<IReadOnlyList<PreflightResult>> RunAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return
            [
                new PreflightResult(Id, "SQL-Verbindung", PreflightState.Skipped,
                    "Kein Connection String angegeben – das Paket entsteht ohne SQL-Daten.")
            ];

        var results = new List<PreflightResult>();
        SqlConnection connection;
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master", ApplicationName = "ERPDetektiv Vorabcheck", ConnectTimeout = 10
            };
            connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            results.Add(new PreflightResult(Id, "SQL-Verbindung", PreflightState.Ok,
                $"Verbunden mit {connection.DataSource} (Version {connection.ServerVersion})."));
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            return
            [
                new PreflightResult(Id, "SQL-Verbindung", PreflightState.Failed,
                    $"Keine Verbindung möglich: {FirstLine(exception.Message)}")
            ];
        }

        await using (connection)
        {
            results.Add(await ProbeAsync(connection, "VIEW SERVER STATE",
                "SELECT TOP 1 cpu_count FROM sys.dm_os_sys_info", cancellationToken,
                "Serverkennzahlen und Speicherinformationen bleiben leer.", PreflightState.Failed));
            results.Add(await ProbeAsync(connection, "Datenbankliste",
                "SELECT TOP 1 name FROM sys.databases", cancellationToken,
                "Ohne Datenbankliste entfällt der gesamte Datenbankteil.", PreflightState.Failed));
            results.Add(await ProbeAsync(connection, "Backup-Historie (msdb)",
                "SELECT TOP 1 database_name FROM msdb.dbo.backupset", cancellationToken,
                "Der Backup-Check kann nicht ausgeführt werden.", PreflightState.Warning));

            if (!string.IsNullOrWhiteSpace(database))
            {
                var safeName = database.Replace("]", "]]", StringComparison.Ordinal);
                results.Add(await ProbeAsync(connection, $"Datenbank {database}",
                    $"SELECT TOP 1 name FROM [{safeName}].sys.database_files", cancellationToken,
                    "Datenbankspezifische Details wie Query Store und Log-Space entfallen.",
                    PreflightState.Warning));
            }
        }

        return results;
    }

    private async Task<PreflightResult> ProbeAsync(SqlConnection connection, string name, string sql,
        CancellationToken cancellationToken, string consequence, PreflightState failureState)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 10 };
            await command.ExecuteScalarAsync(cancellationToken);
            return new PreflightResult(Id, name, PreflightState.Ok, "Lesbar.");
        }
        catch (SqlException exception)
        {
            var reason = exception.Number switch
            {
                229 or 230 or 300 => "Berechtigung fehlt",
                208 => "Objekt nicht verfügbar",
                _ => FirstLine(exception.Message)
            };
            return new PreflightResult(Id, name, failureState, $"{reason}. {consequence}");
        }
    }

    private static string FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Unbekannter Fehler";
}
