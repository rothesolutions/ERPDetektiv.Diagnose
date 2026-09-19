using ERPDetektiv.Contracts;
using ERPDetektiv.Sage100;

namespace ERPDetektiv.Tests;

/// <summary>Haelt die Registry-Abfrage aus Tests heraus, die sie nicht pruefen.</summary>
/// <remarks>
/// Ohne diesen Ersatz startet jeder Collector-Test einen PowerShell-Prozess und liest die
/// Registry des ausfuehrenden Rechners – das Ergebnis haenge dann an der Testumgebung.
/// </remarks>
internal sealed class SkippingRegistryReader : ISageRegistryReader
{
    public Task<CollectionResult<SageRegistrySnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CollectionResult<SageRegistrySnapshot>(null,
            new CollectorStatus(SageRegistryReader.Id, CollectorState.Skipped, now, now, "Im Test nicht abgefragt.")));
    }
}

/// <summary>Liefert eine vorgegebene Registry-Aufnahme.</summary>
internal sealed class StubRegistryReader(SageRegistrySnapshot snapshot) : ISageRegistryReader
{
    public Task<CollectionResult<SageRegistrySnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CollectionResult<SageRegistrySnapshot>(snapshot,
            new CollectorStatus(SageRegistryReader.Id, CollectorState.Succeeded, now, now, "Testdaten.")));
    }
}

/// <summary>Gemeinsame Testdaten.</summary>
internal static class TestSnapshots
{
    public static DiagnosticSnapshot Snapshot(long free = 100, bool? queryStore = null, int? memory = null) => new(
        System(free),
        new SqlServerSnapshot("SQL-A", "16", "Developer", null, memory, null, null, null,
            [new DatabaseSnapshot("ERP", "ONLINE", 1, "FULL", 160, queryStore, null)], []),
        null, [], []);

    public static SystemSnapshot System(long free = 100) => new("Windows", "HOST-A", "X64", 4, 1000, "UTC",
        [new DriveInfoSnapshot("C:", 1000, free)], []);

    public static DatabaseSnapshot UserDatabase(
        DateTimeOffset? lastBackupAt = null,
        bool? queryStoreEnabled = null,
        int? queryStoreUsedMb = null,
        int? queryStoreMaxMb = null,
        IReadOnlyList<DatabaseFileSnapshot>? files = null,
        string status = "ONLINE") =>
        new("ERP", status, 1, "FULL", 160, queryStoreEnabled, lastBackupAt, files, queryStoreUsedMb, queryStoreMaxMb);

    public static DiagnosticSnapshot WithDatabases(params DatabaseSnapshot[] databases)
    {
        var snapshot = Snapshot();
        return snapshot with { SqlServer = snapshot.SqlServer! with { Databases = databases } };
    }

    public static DiagnosticSnapshot WithSage(SageApplicationServerSnapshot applicationServer) =>
        Snapshot() with { Sage100 = new Sage100Snapshot(null, null, true, [], applicationServer) };

    /// <summary>Registry-Aufnahme mit leeren Vorgaben; Tests setzen nur den geprueften Teil.</summary>
    public static SageRegistrySnapshot Registry(
        bool isSageServer = true,
        IReadOnlyList<SageRegistryComponentSnapshot>? components = null,
        IReadOnlyList<SageRegistryApplicationServerSnapshot>? applicationServers = null,
        IReadOnlyList<SageRegistryBlobStorageSnapshot>? blobStorageServers = null,
        IReadOnlyList<SageRegistryDatasourceSnapshot>? datasources = null,
        IReadOnlyList<string>? datasourceKeyNames = null,
        IReadOnlyList<SageRegistryValueSnapshot>? values = null,
        string? sageServer = @"\\HOST-A") =>
        new("9.0", sageServer, isSageServer, components ?? [], applicationServers ?? [], blobStorageServers ?? [], [],
            [], datasources ?? [], datasourceKeyNames ?? [], [], values ?? []);

    public static DiagnosticSnapshot WithRegistry(SageRegistrySnapshot registry, params ServiceSnapshot[] services)
    {
        var snapshot = Snapshot();
        return snapshot with
        {
            System = snapshot.System! with { Services = services },
            Sage100 = new Sage100Snapshot(null, null, false, []) { Registry = registry }
        };
    }

    public static SageRegistryDatasourceSnapshot Datasource(string name, string server = "SQL-A",
        int? serverVersion = null, string applications = "ReweAbf") =>
        new(name, server, name, applications, serverVersion, 1);

    public static DiagnosticSnapshot WithServices(params ServiceSnapshot[] services)
    {
        var snapshot = Snapshot();
        return snapshot with { System = snapshot.System! with { Services = services } };
    }
}
