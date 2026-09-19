using ERPDetektiv.Contracts;

namespace ERPDetektiv.Checks;

public sealed class DiskSpaceCheck(long minimumFreeBytes = 10L * 1024 * 1024 * 1024) : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.System?.Drives
        .Where(drive => drive.FreeBytes < minimumFreeBytes)
        .Select(drive => new Finding("windows.disk.free-space", "Windows", "Wenig freier Speicher",
            $"Auf {drive.Name} sind noch {SnapshotFilters.FormatBytes(drive.FreeBytes)} von {SnapshotFilters.FormatBytes(drive.TotalBytes)} frei.",
            Severity.Warning,
            "Freien Speicher prüfen und ausreichend Reserve schaffen.",
            [
                new Evidence("drive", drive.Name),
                new Evidence("freeBytes", SnapshotFilters.Invariant(drive.FreeBytes)),
                new Evidence("totalBytes", SnapshotFilters.Invariant(drive.TotalBytes)),
                new Evidence("thresholdBytes", SnapshotFilters.Invariant(minimumFreeBytes))
            ])) ?? [];
}

public sealed class QueryStoreCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.SqlServer?.Databases
        .Where(database => database.QueryStoreEnabled is false)
        .Select(database => new Finding("sql.query-store.disabled", "SQL Server", "Query Store deaktiviert",
            $"Query Store ist für Datenbank {database.Name} deaktiviert.", Severity.Warning,
            "Query Store nach Prüfung der Sage-/SQL-Anforderungen aktivieren.",
            [new Evidence("database", database.Name)])) ?? [];
}

public sealed class SqlMemoryCheck(int minimumMb = 512) : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var value = snapshot.SqlServer?.MaxServerMemoryMb;
        return value is null || value >= minimumMb
            ? []
            :
            [
                new Finding("sql.memory.max-server-memory", "SQL Server", "Max Server Memory auffällig niedrig",
                    $"max server memory ist auf {value} MB gesetzt.", Severity.Warning,
                    "Speicheraufteilung für SQL Server und Sage prüfen.",
                    [
                        new Evidence("maxServerMemoryMb", SnapshotFilters.Invariant(value.Value)),
                        new Evidence("thresholdMb", SnapshotFilters.Invariant(minimumMb))
                    ])
            ];
    }
}
