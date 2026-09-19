using ERPDetektiv.Checks;
using ERPDetektiv.Contracts;
using Xunit;

namespace ERPDetektiv.Tests;

/// <summary>
/// Die drei Checks, die aus der Vier-Server-Messreihe hervorgegangen sind.
/// </summary>
/// <remarks>
/// Jeder von ihnen wird zusätzlich gegen die gemessenen Werte des als gut eingestellt
/// betrachteten Referenzsystems geprüft. Ein Check, der dort anschlägt, ist widerlegt –
/// so sind CXPACKET-Anteil, Signal-Wait-Anteil und die bloße Anwesenheit von
/// Blockierungen als Regelkandidaten ausgeschieden.
/// </remarks>
public sealed class StorageAndMemoryCheckTests
{
    private static DiagnosticSnapshot WithSql(Func<SqlServerSnapshot, SqlServerSnapshot> configure)
    {
        var snapshot = TestSnapshots.Snapshot();
        return snapshot with { SqlServer = configure(snapshot.SqlServer!) };
    }

    private static DatabaseSnapshot Database(string name, double? read, double? write, long reads = 100_000,
        long writes = 100_000, string status = "ONLINE") =>
        new(name, status, 1, "FULL", 160, null, null)
        {
            ReadLatencyMs = read, WriteLatencyMs = write, ReadCount = reads, WriteCount = writes
        };

    // ---------------------------------------------------------------- I/O-Latenz

    [Fact]
    public void Io_latency_is_silent_on_the_reference_system()
    {
        // Gemessene Werte des Referenzsystems: Sage-Datenbanken 1,8 ms lesend und
        // 0,4 ms schreibend, schlechtester Wert der Instanz 5,6 ms auf msdb.
        var snapshot = WithSql(sql => sql with
        {
            Databases =
            [
                Database("huppsql01", 1.8, 0.4, 2_888_186, 1_975_515),
                Database("OLGlobal", 1.8, 0.4, 500_000, 300_000),
                Database("msdb", 5.6, 0.4, 40_792, 60_650)
            ]
        });
        Assert.Empty(new SqlIoLatencyCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Io_latency_warns_above_the_threshold()
    {
        var snapshot = WithSql(sql => sql with { Databases = [Database("ERP", 34.5, 0.4)] });
        var finding = Assert.Single(new SqlIoLatencyCheck().Evaluate(snapshot));
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("34,5", finding.Description.Replace('.', ','), StringComparison.Ordinal);
    }

    [Fact]
    public void Io_latency_also_covers_slow_writes()
    {
        var snapshot = WithSql(sql => sql with { Databases = [Database("ERP", 2.0, 45.0)] });
        var finding = Assert.Single(new SqlIoLatencyCheck().Evaluate(snapshot));
        Assert.Contains("Schreibzeit", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Io_latency_ignores_databases_with_too_few_accesses()
    {
        // Ohne Mindestanzahl loeste eine Datenbank mit fuenf Zugriffen, davon einer
        // langsam, eine Warnung aus. Der Mittelwert ist dort nicht aussagekraeftig.
        var snapshot = WithSql(sql => sql with { Databases = [Database("Selten", 200, 200, 12, 3)] });
        Assert.Empty(new SqlIoLatencyCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Io_latency_ignores_databases_that_are_not_online()
    {
        var snapshot = WithSql(sql => sql with { Databases = [Database("ERP", 99, 99, status: "RESTORING")] });
        Assert.Empty(new SqlIoLatencyCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Io_latency_stays_silent_without_data()
    {
        var snapshot = WithSql(sql => sql with { Databases = [Database("ERP", null, null)] });
        Assert.Empty(new SqlIoLatencyCheck().Evaluate(snapshot));
    }

    // --------------------------------------------------- Page Life Expectancy

    [Fact]
    public void Page_life_expectancy_is_silent_on_the_reference_system()
    {
        // Referenzinstanz: 86.041 Sekunden bei rund 0,4 GB Bufferpool.
        var snapshot = WithSql(sql => sql with
        {
            PageLifeExpectancySeconds = 86_041, BufferPoolBytes = 430_587_904
        });
        Assert.Empty(new SqlPageLifeExpectancyCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Page_life_expectancy_scales_with_the_buffer_pool()
    {
        // 128 GB Bufferpool: Referenz 300 x (128 / 4) = 9600 Sekunden. Die alte
        // Pauschalregel "unter 300" haette hier geschwiegen.
        var snapshot = WithSql(sql => sql with
        {
            PageLifeExpectancySeconds = 1_200, BufferPoolBytes = 128L * 1024 * 1024 * 1024
        });
        var finding = Assert.Single(new SqlPageLifeExpectancyCheck().Evaluate(snapshot));
        Assert.Contains("9600", finding.Evidence.Single(e => e.Key == "referenceSeconds").Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Page_life_expectancy_keeps_three_hundred_seconds_as_floor()
    {
        // Kleiner Bufferpool: Die skalierte Referenz laege unter 300, das bleibt die
        // Untergrenze.
        var snapshot = WithSql(sql => sql with
        {
            PageLifeExpectancySeconds = 120, BufferPoolBytes = 512L * 1024 * 1024
        });
        var finding = Assert.Single(new SqlPageLifeExpectancyCheck().Evaluate(snapshot));
        Assert.Equal("300", finding.Evidence.Single(e => e.Key == "referenceSeconds").Value);
    }

    [Fact]
    public void Page_life_expectancy_stays_silent_without_the_buffer_pool_size()
    {
        var snapshot = WithSql(sql => sql with { PageLifeExpectancySeconds = 10, BufferPoolBytes = null });
        Assert.Empty(new SqlPageLifeExpectancyCheck().Evaluate(snapshot));
    }

    // ------------------------------------------------------------ Memory Grants

    [Fact]
    public void Memory_grants_are_silent_when_nothing_waits()
    {
        Assert.Empty(new SqlMemoryGrantsPendingCheck().Evaluate(WithSql(sql => sql with { MemoryGrantsPending = 0 })));
        Assert.Empty(new SqlMemoryGrantsPendingCheck().Evaluate(WithSql(sql => sql with { MemoryGrantsPending = null })));
    }

    [Fact]
    public void Memory_grants_warn_when_queries_wait()
    {
        var finding = Assert.Single(
            new SqlMemoryGrantsPendingCheck().Evaluate(WithSql(sql => sql with { MemoryGrantsPending = 3 })));
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("3", Assert.Single(finding.Evidence).Value);
    }

    [Fact]
    public void All_three_stay_silent_on_an_empty_snapshot()
    {
        var empty = new DiagnosticSnapshot(null, null, null, [], []);
        ICheck[] checks = [new SqlIoLatencyCheck(), new SqlPageLifeExpectancyCheck(), new SqlMemoryGrantsPendingCheck()];
        Assert.Empty(checks.SelectMany(check => check.Evaluate(empty)));
    }
}
