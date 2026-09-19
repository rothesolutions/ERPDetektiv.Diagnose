using ERPDetektiv.Checks;
using ERPDetektiv.Contracts;
using ERPDetektiv.Core;
using ERPDetektiv.Sage100;
using ERPDetektiv.SqlServer;
using ERPDetektiv.Windows;

namespace ERPDetektiv.Application;

/// <summary>Stellt die einheitliche Produktzusammenstellung für CLI und Oberfläche bereit.</summary>
/// <remarks>Collector, Checks und Vorabprüfungen werden bewusst nur hier verdrahtet. Damit
/// liefern beide Oberflächen bei identischen Eingaben dasselbe Diagnosepaket.</remarks>
public static class DiagnosticComposition
{
    public static DiagnosticRunner CreateRunner(string? sqlConnectionString, string? sqlServer, string? database) =>
        new(new WindowsCollector(), CreateSqlCollector(sqlConnectionString, sqlServer, database), new Sage100Collector(),
            CreateChecks());

    public static ICollector<SqlServerSnapshot>? CreateSqlCollector(string? connectionString, string? sqlServer,
        string? database, bool includeSelectedDatabaseDetails = true) =>
        string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new SqlServerCollector(sqlServer ?? "sql-server",
                new MicrosoftSqlServerSnapshotReader(connectionString, database, includeSelectedDatabaseDetails));

    /// <summary>Schlaegt den SQL-Server der Sage-Installation vor, sofern er lesbar ist.</summary>
    /// <remarks>
    /// Liegt hier und nicht in der Oberflaeche, damit CLI und GUI denselben Vorschlag
    /// bekaemen. Die Abfrage startet einen kurzlebigen Hilfsprozess; Aufrufer sollten sie
    /// nicht im Startpfad blockieren.
    /// </remarks>
    public static async Task<string?> SuggestSqlServerAsync(CancellationToken cancellationToken)
    {
        var result = await new SageRegistryReader().CollectAsync(cancellationToken);
        return SageSqlServerHint.FromRegistry(result.Value);
    }

    public static PreflightRunner CreatePreflightRunner(string outputPath, string? sqlConnectionString,
        string? database) => new([
            new ExportPathProbe(outputPath),
            new WindowsPreflightProbe(),
            new SqlPreflightProbe(sqlConnectionString, database),
            new SagePreflightProbe()
        ]);

    public static IReadOnlyList<ICheck> CreateChecks() =>
    [
        new DiskSpaceCheck(), new QueryStoreCheck(), new SqlMemoryCheck(), new BackupAgeCheck(),
        new QueryStoreCapacityCheck(), new DatabaseAutogrowthCheck(), new SqlMemoryRelativeToHostCheck(),
        new PowerPlanCheck(), new SageServiceStateCheck(), new SageSoapServiceCheck(),
        new SageAnonymousSDataEndpointCheck(), new SageIsolationProcessStateCheck(),
        new SqlIoLatencyCheck(), new SqlPageLifeExpectancyCheck(), new SqlMemoryGrantsPendingCheck(),
        new SageMasterCountCheck(), new SageDatasourceKeyConsistencyCheck(), new SageBlobStorageRegistrationCheck(),
        new SageDatasourceServerVersionCheck(), new SageLargeAddressAwareClientCheck()
    ];
}
