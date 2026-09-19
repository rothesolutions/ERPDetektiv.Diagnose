using ERPDetektiv.Checks;
using ERPDetektiv.Contracts;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class CheckTests
{
    [Fact]
    public void Disk_check_returns_warning_below_threshold() =>
        Assert.Single(new DiskSpaceCheck(101).Evaluate(TestSnapshots.Snapshot()));

    [Fact]
    public void Disk_check_ignores_sufficient_capacity() =>
        Assert.Empty(new DiskSpaceCheck(100).Evaluate(TestSnapshots.Snapshot()));

    [Fact]
    public void Query_store_check_finds_disabled_database()
    {
        Assert.Single(new QueryStoreCheck().Evaluate(TestSnapshots.Snapshot(queryStore: false)));
        Assert.Empty(new QueryStoreCheck().Evaluate(TestSnapshots.Snapshot(queryStore: null)));
    }

    [Fact]
    public void Sql_memory_check_handles_absent_value() =>
        Assert.Empty(new SqlMemoryCheck().Evaluate(TestSnapshots.Snapshot()));

    [Fact]
    public void Sql_memory_check_finds_low_value() =>
        Assert.Single(new SqlMemoryCheck(512).Evaluate(TestSnapshots.Snapshot(memory: 511)));

    [Fact]
    public void Backup_age_check_finds_old_full_backup() =>
        Assert.Single(new BackupAgeCheck(TimeSpan.FromDays(7)).Evaluate(
            TestSnapshots.WithDatabases(TestSnapshots.UserDatabase(DateTimeOffset.UtcNow.AddDays(-8)))));

    [Fact]
    public void Backup_age_check_ignores_databases_that_are_not_online()
    {
        // Eine Datenbank im Status RESTORING hat planmaessig kein aktuelles Backup;
        // eine Warnung waere ein Fehlalarm.
        var snapshot = TestSnapshots.WithDatabases(TestSnapshots.UserDatabase(status: "RESTORING"));
        Assert.Empty(new BackupAgeCheck(TimeSpan.FromDays(7)).Evaluate(snapshot));
    }

    [Fact]
    public void Query_store_capacity_check_finds_high_usage() =>
        Assert.Single(new QueryStoreCapacityCheck(90).Evaluate(TestSnapshots.WithDatabases(
            TestSnapshots.UserDatabase(queryStoreEnabled: true, queryStoreUsedMb: 91, queryStoreMaxMb: 100))));

    [Fact]
    public void Autogrowth_check_finds_percent_growth() =>
        Assert.Single(new DatabaseAutogrowthCheck().Evaluate(TestSnapshots.WithDatabases(
            TestSnapshots.UserDatabase(files: [new DatabaseFileSnapshot("ERP_Data", "ROWS", 1, "Percent", 10)]))));

    [Fact]
    public void Relative_memory_check_finds_high_limit()
    {
        var snapshot = TestSnapshots.Snapshot(memory: 4096);
        snapshot = snapshot with
        {
            SqlServer = snapshot.SqlServer! with { HostPhysicalMemoryBytes = 4L * 1024 * 1024 * 1024 }
        };
        Assert.Single(new SqlMemoryRelativeToHostCheck(90).Evaluate(snapshot));
    }

    [Fact]
    public void Power_plan_check_finds_balanced_plan_by_name_when_no_guid_is_known()
    {
        var snapshot = TestSnapshots.Snapshot();
        snapshot = snapshot with { System = snapshot.System! with { PowerPlan = "Balanced" } };
        Assert.Single(new PowerPlanCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Power_plan_check_uses_the_guid_regardless_of_display_language()
    {
        // Auf einem franzoesischen Server heisst der Plan "Utilisation normale"; die GUID
        // ist ueberall dieselbe.
        var snapshot = TestSnapshots.Snapshot();
        snapshot = snapshot with
        {
            System = snapshot.System! with
            {
                PowerPlan = "Utilisation normale", PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e"
            }
        };
        Assert.Single(new PowerPlanCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Power_plan_check_trusts_the_guid_over_a_misleading_name()
    {
        var snapshot = TestSnapshots.Snapshot();
        snapshot = snapshot with
        {
            System = snapshot.System! with
            {
                // Herstellerplan, dessen Name das Wort enthaelt, der aber nicht "Ausbalanciert" ist.
                PowerPlan = "HP Balanced Performance", PowerPlanGuid = "fb5220ff-7e1a-47aa-9a42-50ffbf45c673"
            }
        };
        Assert.Empty(new PowerPlanCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Sage_service_check_finds_stopped_automatic_service() =>
        Assert.Single(new SageServiceStateCheck().Evaluate(TestSnapshots.WithServices(
            new ServiceSnapshot("SagedeApplicationServerService90", "Stopped", "Auto"))));

    [Theory]
    [InlineData("Manual")]
    [InlineData("Disabled")]
    public void Sage_service_check_ignores_services_that_do_not_start_automatically(string startType)
    {
        // Sage installiert Dienste, die planmaessig nicht laufen. Regression gegen
        // Fehlalarme, die die Glaubwuerdigkeit der uebrigen Findings kosten.
        var snapshot = TestSnapshots.WithServices(new ServiceSnapshot("SagedeLoggingService", "Stopped", startType));
        Assert.Empty(new SageServiceStateCheck().Evaluate(snapshot));
    }

    [Fact]
    public void Sage_soap_service_check_is_informational()
    {
        var applicationServer = new SageApplicationServerSnapshot([], null, [],
            [new SageSoapServiceSnapshot("Custom", null, null, null, ["Run"])]);
        var finding = Assert.Single(new SageSoapServiceCheck().Evaluate(TestSnapshots.WithSage(applicationServer)));
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void Sage_anonymous_sdata_check_is_informational()
    {
        var applicationServer = new SageApplicationServerSnapshot([], null,
            [new SageServiceEndpointSnapshot("HttpsNone", "https://server:5501/")], []);
        var finding = Assert.Single(
            new SageAnonymousSDataEndpointCheck().Evaluate(TestSnapshots.WithSage(applicationServer)));
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void Sage_isolation_process_check_finds_error_state()
    {
        var isolation = new SageIsolationProcessSummary(1, 0, 0, 0, 0, 0, 0, null, null,
            [new SageIsolationProcessStateSnapshot("Faulted", 1)]);
        Assert.Single(new SageIsolationProcessStateCheck()
            .Evaluate(TestSnapshots.WithSage(new SageApplicationServerSnapshot([], isolation, [], []))));
    }

    [Fact]
    public void Checks_stay_silent_on_an_empty_snapshot()
    {
        // Unvollstaendige Daten duerfen keine unbegruendete Warnung erzeugen.
        var empty = new DiagnosticSnapshot(null, null, null, [], []);
        ICheck[] checks =
        [
            new DiskSpaceCheck(), new QueryStoreCheck(), new SqlMemoryCheck(), new BackupAgeCheck(),
            new QueryStoreCapacityCheck(), new DatabaseAutogrowthCheck(), new SqlMemoryRelativeToHostCheck(),
            new PowerPlanCheck(), new SageServiceStateCheck(), new SageSoapServiceCheck(),
            new SageAnonymousSDataEndpointCheck(), new SageIsolationProcessStateCheck(),
            new SageMasterCountCheck(), new SageDatasourceKeyConsistencyCheck(),
            new SageBlobStorageRegistrationCheck(), new SageDatasourceServerVersionCheck(),
            new SageLargeAddressAwareClientCheck()
        ];
        Assert.Empty(checks.SelectMany(check => check.Evaluate(empty)));
    }
}
