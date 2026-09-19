using System.Globalization;
using ERPDetektiv.Contracts;

namespace ERPDetektiv.Checks;

public sealed class BackupAgeCheck(TimeSpan? maximumAge = null) : ICheck
{
    private readonly TimeSpan _maximumAge = maximumAge ?? TimeSpan.FromDays(7);

    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        return snapshot.SqlServer?.Databases.Where(SnapshotFilters.IsAssessableUserDatabase).Where(database =>
                database.LastBackupAt is null || now - database.LastBackupAt > _maximumAge)
            .Select(database => new Finding("sql.backup.full.stale", "SQL Server", "Full Backup fehlt oder ist zu alt",
                database.LastBackupAt is null
                    ? $"Für {database.Name} wurde kein Full Backup erfasst."
                    : $"Das letzte Full Backup von {database.Name} ist älter als {_maximumAge.TotalDays:0} Tage.",
                Severity.Warning, "Backup-Job, Aufbewahrung und Wiederherstellbarkeit prüfen.",
                [
                    new Evidence("database", database.Name),
                    new Evidence("lastBackupAt", database.LastBackupAt?.ToString("O") ?? "not-recorded"),
                    new Evidence("maximumAgeDays", SnapshotFilters.Invariant(_maximumAge.TotalDays, "0"))
                ])) ?? [];
    }

}

public sealed class QueryStoreCapacityCheck(double warningThresholdPercent = 90) : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.SqlServer?.Databases
        .Where(database => database.QueryStoreEnabled is true && database.QueryStoreStorageUsedMb is not null &&
                           database.QueryStoreStorageMaxMb is > 0)
        .Where(database => database.QueryStoreStorageUsedMb!.Value * 100d / database.QueryStoreStorageMaxMb!.Value >=
                           warningThresholdPercent)
        .Select(database => new Finding("sql.query-store.capacity", "SQL Server", "Query Store nahezu voll",
            $"Query Store von {database.Name} nutzt {database.QueryStoreStorageUsedMb!.Value * 100d / database.QueryStoreStorageMaxMb!.Value:0.#}% seines Limits.",
            Severity.Warning, "Query-Store-Aufbewahrung und Speicherlimit prüfen.",
            [
                new Evidence("database", database.Name),
                new Evidence("usedMb", SnapshotFilters.Invariant(database.QueryStoreStorageUsedMb!.Value)),
                new Evidence("maxMb", SnapshotFilters.Invariant(database.QueryStoreStorageMaxMb!.Value))
            ])) ?? [];
}

public sealed class DatabaseAutogrowthCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.SqlServer?.Databases
        .Where(SnapshotFilters.IsAssessableUserDatabase).SelectMany(database => database.Files ?? [], (database, file) => (database, file))
        .Where(item => string.Equals(item.file.GrowthMode, "Percent", StringComparison.OrdinalIgnoreCase))
        .Select(item => new Finding("sql.database-file.percent-growth", "SQL Server", "Prozentuales Dateiwachstum",
            $"{item.database.Name}/{item.file.LogicalName} wächst um {item.file.GrowthValue}%.", Severity.Warning,
            "Feste, zur Arbeitslast passende Wachstumsschritte bevorzugen.",
            [
                new Evidence("database", item.database.Name), new Evidence("file", item.file.LogicalName),
                new Evidence("growthPercent", SnapshotFilters.Invariant(item.file.GrowthValue))
            ])) ?? [];

}

public sealed class SqlMemoryRelativeToHostCheck(double warningThresholdPercent = 90) : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var sql = snapshot.SqlServer;
        if (sql?.MaxServerMemoryMb is null || sql.HostPhysicalMemoryBytes is null ||
            sql.HostPhysicalMemoryBytes <= 0) return [];
        var hostMemoryMb = sql.HostPhysicalMemoryBytes.Value / 1024d / 1024d;
        if (sql.MaxServerMemoryMb.Value > hostMemoryMb * 2)
            return
            [
                new Finding("sql.memory.max-server-memory.unbounded", "SQL Server",
                    "Max Server Memory praktisch unbegrenzt",
                    "max server memory liegt weit über dem erkannten Host-RAM und begrenzt SQL Server damit faktisch nicht.",
                    Severity.Warning,
                    "Speicherreserve für Windows, Sage Application Server und weitere Dienste einplanen.",
                    [
                        new Evidence("maxServerMemoryMb",
                            sql.MaxServerMemoryMb.Value.ToString(CultureInfo.InvariantCulture)),
                        new Evidence("hostMemoryMb",
                            hostMemoryMb.ToString("0.#", CultureInfo.InvariantCulture))
                    ])
            ];
        var percent = sql.MaxServerMemoryMb.Value * 100d / hostMemoryMb;
        return percent < warningThresholdPercent
            ? []
            :
            [
                new Finding("sql.memory.max-server-memory.relative", "SQL Server",
                    "Max Server Memory relativ zum Host hoch",
                    $"max server memory entspricht {percent:0.#}% des erkannten Host-RAM.", Severity.Warning,
                    "Speicherreserve für Windows, Sage Application Server und weitere Dienste einplanen.",
                    [
                        new Evidence("maxServerMemoryMb",
                            sql.MaxServerMemoryMb.Value.ToString(CultureInfo.InvariantCulture)),
                        new Evidence("hostPhysicalMemoryBytes",
                            sql.HostPhysicalMemoryBytes.Value.ToString(
                                CultureInfo.InvariantCulture)),
                        new Evidence("percent",
                            percent.ToString("0.#", CultureInfo.InvariantCulture))
                    ])
            ];
    }
}

public sealed class PowerPlanCheck : ICheck
{
    /// <summary>GUID des Windows-Schemas "Ausbalanciert".</summary>
    /// <remarks>
    /// Die GUID ist auf jedem Windows dieselbe. Der Anzeigename ist uebersetzt, sodass
    /// eine Namenspruefung auf franzoesischen oder polnischen Servern stillschweigend
    /// nichts findet.
    /// </remarks>
    private const string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var system = snapshot.System;
        if (system is null || !IsBalanced(system)) return [];
        var powerPlan = system.PowerPlan ?? system.PowerPlanGuid ?? "unbekannt";
        return
        [
            new Finding("windows.power-plan.balanced", "Windows", "Ausbalancierter Energieplan",
                $"Aktiver Energieplan: {powerPlan}.", Severity.Info,
                "Für einen dedizierten SQL-/Sage-Server den Hersteller- und Performance-Leitfaden prüfen.",
                [
                    new Evidence("powerPlan", powerPlan),
                    new Evidence("powerPlanGuid", system.PowerPlanGuid ?? "nicht erfasst")
                ])
        ];
    }

    private static bool IsBalanced(SystemSnapshot system)
    {
        if (!string.IsNullOrWhiteSpace(system.PowerPlanGuid))
            return string.Equals(system.PowerPlanGuid, BalancedPlanGuid, StringComparison.OrdinalIgnoreCase);
        // Ohne GUID bleibt nur der Anzeigename; bewusst auf Deutsch und Englisch begrenzt.
        return system.PowerPlan is { } name &&
               (name.Contains("balanced", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ausbalanciert", StringComparison.OrdinalIgnoreCase));
    }
}