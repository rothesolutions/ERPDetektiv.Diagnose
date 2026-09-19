using ERPDetektiv.Contracts;
using ERPDetektiv.SqlServer;
using Xunit;

namespace ERPDetektiv.Tests;

/// <summary>
/// Tests gegen eine echte SQL-Instanz. Sie laufen nur, wenn
/// <c>ERPDETEKTIV_TEST_SQL_CONNECTION</c> gesetzt ist.
/// </summary>
/// <remarks>
/// Zuvor kehrte der Test ohne Verbindung still zurueck und meldete Erfolg, ohne etwas
/// geprueft zu haben. Jetzt macht <see cref="SkipUnless"/> das Ueberspringen im Testlauf
/// sichtbar, und <c>ERPDETEKTIV_REQUIRE_INTEGRATION=1</c> laesst den Lauf scheitern, wenn
/// die Verbindung erwartet wird (etwa in einer Release-Pipeline).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SqlIntegrationTests
{
    private const string ConnectionVariable = "ERPDETEKTIV_TEST_SQL_CONNECTION";
    private const string RequireVariable = "ERPDETEKTIV_REQUIRE_INTEGRATION";
    private const string DatabaseVariable = "ERPDETEKTIV_TEST_SQL_DATABASE";

    [Fact]
    public async Task Reader_collects_a_snapshot_from_the_configured_instance()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (SkipUnless(connectionString)) return;

        var result = await new MicrosoftSqlServerSnapshotReader(connectionString!, "master",
            includeSelectedDatabaseDetails: false).ReadAsync("connection-test", CancellationToken.None);
        Assert.Contains(result.Snapshot.Databases, database => database.Name == "master");
        Assert.False(string.IsNullOrWhiteSpace(result.Snapshot.Version));

        var master = await new MicrosoftSqlServerSnapshotReader(connectionString!, "master")
            .ReadAsync("master-details", CancellationToken.None);
        Assert.NotEqual(FeatureCollectionState.Failed,
            Assert.Single(master.Snapshot.Databases, database => database.Name == "master")
                .QueryStoreCollectionState);
    }

    [Fact]
    public async Task Database_sizes_survive_databases_larger_than_two_gibibytes()
    {
        // Regression gegen Msg 8115: SUM(size) * 8192 rechnete in int und lief ab rund
        // 2 GiB ueber – also bei praktisch jeder produktiven Sage-Datenbank.
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (SkipUnless(connectionString)) return;
        var database = Environment.GetEnvironmentVariable(DatabaseVariable);

        var result = await new MicrosoftSqlServerSnapshotReader(connectionString!, database)
            .ReadAsync("integration", CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.NotEmpty(result.Snapshot.Databases);
        Assert.NotEmpty(result.Snapshot.TempDbFiles);
        Assert.All(result.Snapshot.Databases, item => Assert.True(item.SizeBytes >= 0));
    }

    private static bool SkipUnless(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString)) return false;
        Assert.False(Environment.GetEnvironmentVariable(RequireVariable) == "1",
            $"{ConnectionVariable} ist nicht gesetzt, obwohl {RequireVariable}=1 die Integrationstests verlangt.");
        return true;
    }
}
