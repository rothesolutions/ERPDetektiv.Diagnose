using System.Globalization;
using ERPDetektiv.Contracts;
using Microsoft.Data.SqlClient;

namespace ERPDetektiv.SqlServer;

/// <summary>Ergebnis einer SQL-Erfassung samt der Bereiche, die nicht gelesen werden konnten.</summary>
public sealed record SqlServerReadResult(SqlServerSnapshot Snapshot, IReadOnlyList<string> Warnings);

public interface ISqlServerSnapshotReader
{
    Task<SqlServerReadResult> ReadAsync(string server, CancellationToken cancellationToken);
}

public sealed class SqlServerCollector(string server, ISqlServerSnapshotReader reader) : ICollector<SqlServerSnapshot>
{
    public string Id => "sqlserver";

    public async Task<CollectionResult<SqlServerSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var result = await reader.ReadAsync(server, cancellationToken);
        // Ein Teilbereich ohne Berechtigung darf den Snapshot nicht als vollstaendig ausweisen.
        var state = result.Warnings.Count == 0 ? CollectorState.Succeeded : CollectorState.Partial;
        var message = result.Warnings.Count == 0
            ? "SQL-Server-Snapshot vollständig erfasst."
            : $"Teilweise erfasst. Nicht gelesen: {string.Join(" | ", result.Warnings)}";
        return new CollectionResult<SqlServerSnapshot>(result.Snapshot,
            new CollectorStatus(Id, state, started, DateTimeOffset.UtcNow, message));
    }
}

public sealed class UnavailableSqlServerSnapshotReader : ISqlServerSnapshotReader
{
    public Task<SqlServerReadResult> ReadAsync(string server, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "SQL-Server-Zugriff ist in diesem Build noch nicht konfiguriert. Verwende einen freigegebenen Snapshot-Reader.");
}

public sealed class MicrosoftSqlServerSnapshotReader(
    string connectionString,
    string? database,
    bool includeSelectedDatabaseDetails = true)
    : ISqlServerSnapshotReader
{
    private const int CommandTimeoutSeconds = 30;

    public async Task<SqlServerReadResult> ReadAsync(string server, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
            { InitialCatalog = "master", ApplicationName = "ERPDetektiv Diagnose" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Jeder Bereich wird einzeln gekapselt: Eine fehlende Berechtigung kostet genau
        // diesen Bereich, nicht den gesamten Snapshot.
        var warnings = new List<string>();
        var serverInfo = await TryReadAsync(() => ServerInfoAsync(connection, cancellationToken),
            "Serverinformationen (VIEW SERVER STATE)", warnings) ?? ServerInfo.Empty;
        var settings = await TryReadAsync(() => SettingsAsync(connection, cancellationToken),
            "Instanzkonfiguration", warnings) ?? [];
        var databases = await TryReadAsync(() => DatabasesAsync(connection, cancellationToken),
            "Datenbankliste", warnings) ?? [];

        var files = await TryReadAsync(() => DatabaseFilesAsync(connection, cancellationToken),
            "Datenbankdateien", warnings);
        if (files is not null)
            databases = databases.Select(item =>
                item with { Files = files.TryGetValue(item.Name, out var databaseFiles) ? databaseFiles : [] }).ToList();

        var backups = await TryReadAsync(() => BackupTimesAsync(connection, serverInfo.Offset, cancellationToken),
            "Backup-Zeitpunkte (msdb)", warnings);
        if (backups is not null)
            databases = databases.Select(item =>
                item with { LastBackupAt = backups.TryGetValue(item.Name, out var backup) ? backup : null }).ToList();

        if (includeSelectedDatabaseDetails && !string.IsNullOrWhiteSpace(database))
        {
            var selectedIndex = databases.FindIndex(item =>
                string.Equals(item.Name, database, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex >= 0)
                databases[selectedIndex] =
                    await AddSelectedDatabaseDetailsAsync(connection, databases[selectedIndex], cancellationToken);
        }

        // Latenz je Datenbank: Die Zaehler in dm_io_virtual_file_stats sind kumulativ
        // seit Instanzstart, ein einzelner Snapshot genuegt also fuer den
        // Lebenszeit-Durchschnitt - es braucht keine Zeitreihe.
        var latency = await TryReadAsync(() => FileLatencyAsync(connection, cancellationToken),
            "I/O-Latenz je Datenbank", warnings);
        if (latency is not null)
            databases = databases.Select(item => latency.TryGetValue(item.Name, out var values)
                ? item with
                {
                    ReadLatencyMs = values.ReadLatencyMs, WriteLatencyMs = values.WriteLatencyMs,
                    ReadCount = values.ReadCount, WriteCount = values.WriteCount
                }
                : item).ToList();

        var counters = await TryReadAsync(() => MemoryCountersAsync(connection, cancellationToken),
            "Speicherzähler (Page Life Expectancy, Memory Grants)", warnings);

        var tempDb = await TryReadAsync(() => TempDbAsync(connection, cancellationToken), "TempDB-Dateien", warnings)
                     ?? [];
        var instantFileInitialization = await InstantFileInitializationAsync(connection, cancellationToken);

        var snapshot = new SqlServerSnapshot(server, serverInfo.Version, serverInfo.Edition, serverInfo.StartedAt,
            GetSetting(settings, "max server memory (MB)"), GetSetting(settings, "min server memory (MB)"),
            GetSetting(settings, "max degree of parallelism"), GetSetting(settings, "cost threshold for parallelism"),
            databases, tempDb, serverInfo.CpuCount, serverInfo.SocketCount, serverInfo.CoresPerSocket,
            serverInfo.VirtualMachineType, serverInfo.HostPhysicalMemoryBytes, serverInfo.HostAvailableMemoryBytes,
            instantFileInitialization, serverInfo.AlwaysOnEnabled)
        {
            PageLifeExpectancySeconds = counters?.PageLifeExpectancySeconds,
            BufferPoolBytes = counters?.BufferPoolBytes,
            MemoryGrantsPending = counters?.MemoryGrantsPending
        };
        return new SqlServerReadResult(snapshot, warnings);
    }

    /// <summary>Fuehrt eine Teilabfrage aus und vermerkt einen Fehlschlag als Warnung.</summary>
    private static async Task<T?> TryReadAsync<T>(Func<Task<T>> read, string area, ICollection<string> warnings)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (SqlException exception)
        {
            warnings.Add($"{area}: {Describe(exception)}");
            return null;
        }
        catch (InvalidOperationException exception)
        {
            warnings.Add($"{area}: {exception.Message}");
            return null;
        }
    }

    private static string Describe(SqlException exception) => exception.Number switch
    {
        229 or 230 or 300 => "Berechtigung fehlt",
        208 => "Objekt nicht verfügbar",
        _ => exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
             ?? "Abfrage fehlgeschlagen"
    };

    private sealed record ServerInfo(
        string? Version,
        string? Edition,
        DateTimeOffset? StartedAt,
        int? CpuCount,
        int? SocketCount,
        int? CoresPerSocket,
        string? VirtualMachineType,
        long? HostPhysicalMemoryBytes,
        long? HostAvailableMemoryBytes,
        bool? AlwaysOnEnabled,
        TimeSpan Offset)
    {
        public static readonly ServerInfo Empty =
            new(null, null, null, null, null, null, null, null, null, null, TimeSpan.Zero);
    }

    private static async Task<ServerInfo> ServerInfoAsync(SqlConnection connection, CancellationToken token)
    {
        // SYSDATETIMEOFFSET() liefert die Zeitzone der Instanz. Ohne sie muesste ein
        // Serverzeitstempel mit der Zeitzone des auswertenden Rechners interpretiert
        // werden - auf verteilten Installationen ist das schlicht falsch.
        const string sql = """
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS Version,
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS Edition,
                   info.sqlserver_start_time AS StartTime,
                   info.cpu_count AS CpuCount,
                   info.socket_count AS SocketCount,
                   info.cores_per_socket AS CoresPerSocket,
                   info.virtual_machine_type_desc AS VirtualMachineType,
                   CAST(memory.total_physical_memory_kb AS bigint) * 1024 AS TotalMemory,
                   CAST(memory.available_physical_memory_kb AS bigint) * 1024 AS AvailableMemory,
                   CAST(SERVERPROPERTY('IsHadrEnabled') AS int) AS Hadr,
                   SYSDATETIMEOFFSET() AS ServerNow
            FROM sys.dm_os_sys_info info CROSS JOIN sys.dm_os_sys_memory memory
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Keine Serverinformationen erhalten.");
        var offset = reader.GetDateTimeOffset(reader.GetOrdinal("ServerNow")).Offset;
        return new ServerInfo(
            GetNullableString(reader, "Version"),
            GetNullableString(reader, "Edition"),
            GetNullableDateTime(reader, "StartTime") is { } start
                ? new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), offset)
                : null,
            GetNullableInt(reader, "CpuCount"),
            GetNullableInt(reader, "SocketCount"),
            GetNullableInt(reader, "CoresPerSocket"),
            GetNullableString(reader, "VirtualMachineType"),
            GetNullableLong(reader, "TotalMemory"),
            GetNullableLong(reader, "AvailableMemory"),
            GetNullableInt(reader, "Hadr") == 1,
            offset);
    }

    private static async Task<Dictionary<string, int>> SettingsAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql =
            "SELECT name, value_in_use FROM sys.configurations WHERE name IN ('max server memory (MB)', 'min server memory (MB)', 'max degree of parallelism', 'cost threshold for parallelism')";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
            result[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        return result;
    }

    private static async Task<List<DatabaseSnapshot>> DatabasesAsync(SqlConnection connection, CancellationToken token)
    {
        // mf.size ist int und zaehlt 8-KB-Seiten. Ohne CAST auf bigint rechnet SQL Server
        // die Multiplikation in int und wirft ab rund 2 GiB Msg 8115 (arithmetic overflow) -
        // also bei praktisch jeder produktiven Sage-Datenbank.
        const string sql = """
            SELECT d.name, d.state_desc, SUM(CAST(mf.size AS bigint)) * 8192, d.recovery_model_desc, d.compatibility_level
            FROM sys.databases d
            LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id
            GROUP BY d.name, d.state_desc, d.recovery_model_desc, d.compatibility_level
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<DatabaseSnapshot>();
        while (await reader.ReadAsync(token))
            result.Add(new DatabaseSnapshot(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.GetString(3), Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture), null, null));
        return result;
    }

    private static async Task<List<TempDbFileSnapshot>> TempDbAsync(SqlConnection connection, CancellationToken token)
    {
        const string sql =
            "SELECT name, CAST(size AS bigint) * 8192, is_percent_growth, CAST(growth AS bigint) FROM sys.master_files WHERE database_id = 2";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<TempDbFileSnapshot>();
        while (await reader.ReadAsync(token))
        {
            var isPercent = reader.GetBoolean(2);
            var rawGrowth = reader.GetInt64(3);
            result.Add(new TempDbFileSnapshot(reader.GetString(0), reader.GetInt64(1),
                isPercent ? "Percent" : "FixedBytes", isPercent ? rawGrowth : rawGrowth * 8192));
        }

        return result;
    }

    private static async Task<Dictionary<string, DateTimeOffset>> BackupTimesAsync(SqlConnection connection,
        TimeSpan serverOffset, CancellationToken token)
    {
        const string sql =
            "SELECT database_name, MAX(backup_finish_date) FROM msdb.dbo.backupset WHERE type = 'D' GROUP BY database_name";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            // backup_finish_date ist Serverzeit ohne Zeitzone; der Offset der Instanz macht
            // den Wert erst vergleichbar.
            result[reader.GetString(0)] =
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Unspecified), serverOffset);
        }

        return result;
    }

    private static async Task<DatabaseSnapshot> AddSelectedDatabaseDetailsAsync(SqlConnection connection,
        DatabaseSnapshot database, CancellationToken token)
    {
        var safeName = database.Name.Replace("]", "]]", StringComparison.Ordinal);
        var withQueryStore = await AddQueryStoreDetailsAsync(connection, database, safeName, token);
        return await AddLogSpaceDetailsAsync(connection, withQueryStore, safeName, token);
    }

    private static async Task<DatabaseSnapshot> AddQueryStoreDetailsAsync(SqlConnection connection,
        DatabaseSnapshot database, string safeName, CancellationToken token)
    {
        try
        {
            await using var command = new SqlCommand(
                $"SELECT actual_state_desc, current_storage_size_mb, max_storage_size_mb FROM [{safeName}].sys.database_query_store_options",
                connection) { CommandTimeout = CommandTimeoutSeconds };
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return database with { QueryStoreCollectionState = FeatureCollectionState.NotAvailable };
            var state = GetNullableString(reader, "actual_state_desc");
            return database with
            {
                QueryStoreEnabled = string.Equals(state, "READ_WRITE", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(state, "READ_ONLY", StringComparison.OrdinalIgnoreCase),
                QueryStoreStorageUsedMb = GetNullableInt(reader, "current_storage_size_mb"),
                QueryStoreStorageMaxMb = GetNullableInt(reader, "max_storage_size_mb"),
                QueryStoreCollectionState = FeatureCollectionState.Available
            };
        }
        catch (SqlException exception) { return database with { QueryStoreCollectionState = FeatureState(exception) }; }
        catch (InvalidOperationException)
        {
            return database with { QueryStoreCollectionState = FeatureCollectionState.NotAvailable };
        }
    }

    private static async Task<DatabaseSnapshot> AddLogSpaceDetailsAsync(SqlConnection connection,
        DatabaseSnapshot database, string safeName, CancellationToken token)
    {
        try
        {
            await using var command = new SqlCommand(
                $"SELECT used_log_space_in_bytes, used_log_space_in_percent FROM [{safeName}].sys.dm_db_log_space_usage",
                connection) { CommandTimeout = CommandTimeoutSeconds };
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return database with { LogSpaceCollectionState = FeatureCollectionState.NotAvailable };
            return database with
            {
                LogSpaceUsedBytes = GetNullableLong(reader, "used_log_space_in_bytes"),
                LogSpaceUsedPercent = GetNullableDouble(reader, "used_log_space_in_percent"),
                LogSpaceCollectionState = FeatureCollectionState.Available
            };
        }
        catch (SqlException exception) { return database with { LogSpaceCollectionState = FeatureState(exception) }; }
        catch (InvalidOperationException)
        {
            return database with { LogSpaceCollectionState = FeatureCollectionState.NotAvailable };
        }
    }

    private static async Task<bool?> InstantFileInitializationAsync(SqlConnection connection, CancellationToken token)
    {
        try
        {
            await using var command =
                new SqlCommand("SELECT MAX(CAST(instant_file_initialization_enabled AS int)) FROM sys.dm_server_services",
                    connection) { CommandTimeout = CommandTimeoutSeconds };
            var value = await command.ExecuteScalarAsync(token);
            return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }
        catch (SqlException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<Dictionary<string, IReadOnlyList<DatabaseFileSnapshot>>> DatabaseFilesAsync(
        SqlConnection connection, CancellationToken token)
    {
        const string sql =
            "SELECT DB_NAME(database_id), name, type_desc, CAST(size AS bigint) * 8192, is_percent_growth, CAST(growth AS bigint) FROM sys.master_files";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        var grouped = new Dictionary<string, List<DatabaseFileSnapshot>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            // DB_NAME() liefert NULL fuer Datenbanken ohne Leseberechtigung.
            if (reader.IsDBNull(0)) continue;
            var databaseName = reader.GetString(0);
            var isPercent = reader.GetBoolean(4);
            var rawGrowth = reader.GetInt64(5);
            var file = new DatabaseFileSnapshot(reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                isPercent ? "Percent" : "FixedBytes", isPercent ? rawGrowth : rawGrowth * 8192);
            if (!grouped.TryGetValue(databaseName, out var list))
            {
                list = [];
                grouped.Add(databaseName, list);
            }

            list.Add(file);
        }

        return grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<DatabaseFileSnapshot>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record FileLatency(double? ReadLatencyMs, double? WriteLatencyMs, long ReadCount, long WriteCount);

    /// <summary>Mittlere Zugriffszeit je Datenbank seit Instanzstart.</summary>
    /// <remarks>
    /// Die reine Summe der Wartezeit taeuscht: Sechstausend Sekunden klingen dramatisch,
    /// verteilt auf Millionen Zugriffe sind es zwei Millisekunden. Erst der Quotient hat
    /// eine Bezugsgroesse ausserhalb des Systems.
    /// </remarks>
    private static async Task<Dictionary<string, FileLatency>> FileLatencyAsync(SqlConnection connection,
        CancellationToken token)
    {
        const string sql = """
            SELECT DB_NAME(vfs.database_id)      AS name,
                   SUM(vfs.num_of_reads)         AS reads,
                   SUM(vfs.io_stall_read_ms)     AS readStallMs,
                   SUM(vfs.num_of_writes)        AS writes,
                   SUM(vfs.io_stall_write_ms)    AS writeStallMs
            FROM sys.dm_io_virtual_file_stats(NULL, NULL) AS vfs
            WHERE DB_NAME(vfs.database_id) IS NOT NULL
            GROUP BY DB_NAME(vfs.database_id)
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<string, FileLatency>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token))
        {
            var reads = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            var readStall = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            var writes = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            var writeStall = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
            result[reader.GetString(0)] = new FileLatency(
                reads > 0 ? Math.Round((double)readStall / reads, 2) : null,
                writes > 0 ? Math.Round((double)writeStall / writes, 2) : null,
                reads, writes);
        }

        return result;
    }

    private sealed record MemoryCounters(long? PageLifeExpectancySeconds, long? BufferPoolBytes, int? MemoryGrantsPending);

    private static async Task<MemoryCounters> MemoryCountersAsync(SqlConnection connection, CancellationToken token)
    {
        // counter_name ist mit Leerzeichen aufgefuellt, deshalb LIKE statt Gleichheit.
        // object_name traegt bei benannten Instanzen einen Praefix.
        const string sql = """
            SELECT
                MAX(CASE WHEN counter_name LIKE 'Page life expectancy%'
                          AND object_name LIKE '%Buffer Manager%' THEN cntr_value END)        AS ple,
                MAX(CASE WHEN counter_name LIKE 'Database pages%'
                          AND object_name LIKE '%Buffer Manager%' THEN cntr_value END) * 8192 AS bufferPoolBytes,
                MAX(CASE WHEN counter_name LIKE 'Memory Grants Pending%' THEN cntr_value END) AS grantsPending
            FROM sys.dm_os_performance_counters
            WHERE counter_name LIKE 'Page life expectancy%'
               OR counter_name LIKE 'Memory Grants Pending%'
               OR (counter_name LIKE 'Database pages%' AND object_name LIKE '%Buffer Manager%')
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new MemoryCounters(null, null, null);
        return new MemoryCounters(GetNullableLong(reader, "ple"), GetNullableLong(reader, "bufferPoolBytes"),
            GetNullableInt(reader, "grantsPending"));
    }

    private static int? GetSetting(IReadOnlyDictionary<string, int> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : null;

    private static string? GetNullableString(SqlDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
    }

    private static int? GetNullableInt(SqlDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : Convert.ToInt32(reader.GetValue(index), CultureInfo.InvariantCulture);
    }

    private static long? GetNullableLong(SqlDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : Convert.ToInt64(reader.GetValue(index), CultureInfo.InvariantCulture);
    }

    private static double? GetNullableDouble(SqlDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : Convert.ToDouble(reader.GetValue(index), CultureInfo.InvariantCulture);
    }

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetDateTime(index);
    }

    private static FeatureCollectionState FeatureState(SqlException exception) => exception.Number switch
    {
        208 => FeatureCollectionState.NotSupported,
        229 or 230 or 300 => FeatureCollectionState.AccessDenied,
        _ => FeatureCollectionState.Failed
    };
}
