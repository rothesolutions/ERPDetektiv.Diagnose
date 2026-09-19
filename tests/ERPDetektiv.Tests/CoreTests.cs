using System.IO.Compression;
using ERPDetektiv.Cli;
using ERPDetektiv.Contracts;
using ERPDetektiv.Core;
using ERPDetektiv.Sage100;
using ERPDetektiv.SqlServer;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class CoreTests
{
    [Fact]
    public async Task Runner_isolates_collector_failure()
    {
        var runner = new DiagnosticRunner(new ThrowingCollector(), null, null, []);
        var snapshot = await runner.RunAsync(CancellationToken.None);
        Assert.Equal(CollectorState.Failed, Assert.Single(snapshot.CollectionStatus).State);
    }

    [Fact]
    public async Task Runner_reports_sub_area_status_separately()
    {
        // Regression: Der Application-Server-Status stand nur als Text in der
        // Sammelmeldung und fehlte damit in collection-status.json.
        var runner = new DiagnosticRunner(new SubStatusCollector(), null, null, []);
        var snapshot = await runner.RunAsync(CancellationToken.None);
        Assert.Equal(2, snapshot.CollectionStatus.Count);
        Assert.Contains(snapshot.CollectionStatus, status => status.CollectorId == "windows.sub-area");
    }

    [Fact]
    public async Task Sql_collector_exposes_reader_result()
    {
        var collector = new SqlServerCollector("test", new FixedReader());
        var result = await collector.CollectAsync(CancellationToken.None);
        Assert.Equal("test", result.Value!.Server);
        Assert.Equal(CollectorState.Succeeded, result.Status.State);
    }

    [Fact]
    public async Task Sql_collector_reports_partial_state_for_unreadable_areas()
    {
        // Eine fehlende Berechtigung kostet den betroffenen Bereich, nicht den Snapshot.
        var collector = new SqlServerCollector("test", new FixedReader(["Backup-Zeitpunkte (msdb): Berechtigung fehlt"]));
        var result = await collector.CollectAsync(CancellationToken.None);
        Assert.Equal(CollectorState.Partial, result.Status.State);
        Assert.Contains("msdb", result.Status.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task Sage_collector_skips_unknown_install()
    {
        var collector = new Sage100Collector([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())],
            registryReader: new SkippingRegistryReader());
        var result = await collector.CollectAsync(CancellationToken.None);
        Assert.Equal(CollectorState.Skipped, result.Status.State);
    }

    [Fact]
    public async Task Sage_collector_prefers_buildinfo_version_and_detects_application_server()
    {
        var root = Path.Combine(Path.GetTempPath(), $"erpdetektiv-sage-{Guid.NewGuid():N}");
        var installation = Path.Combine(root, "Sage", "Sage 100", "9.0");
        try
        {
            Directory.CreateDirectory(installation);
            Directory.CreateDirectory(Path.Combine(root, "Sage", "Application Server"));
            await File.WriteAllTextAsync(Path.Combine(installation, "Buildinfo.xml"),
                "<Main FixVersion=\"Sage 100 (9.0.11.4)\" />");
            var result = await new Sage100Collector([root], registryReader: new SkippingRegistryReader())
                .CollectAsync(CancellationToken.None);
            Assert.Equal(CollectorState.Succeeded, result.Status.State);
            Assert.Equal("Sage 100 (9.0.11.4)", result.Value!.Version);
            Assert.True(result.Value.ApplicationServerDetected);
            Assert.Contains("Application Server", result.Value.Components);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sage_collector_picks_the_highest_installed_version()
    {
        // Regression: Zuvor entschied das Aenderungsdatum. Wird eine aeltere Installation
        // nachtraeglich angefasst, meldete der Collector die falsche Version.
        var root = Path.Combine(Path.GetTempPath(), $"erpdetektiv-sage-{Guid.NewGuid():N}");
        try
        {
            var older = Path.Combine(root, "Sage", "Sage 100", "9.0");
            var newer = Path.Combine(root, "Sage", "Sage 100", "10.1");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            await File.WriteAllTextAsync(Path.Combine(older, "Buildinfo.xml"), "<Main FixVersion=\"alt\" />");
            await File.WriteAllTextAsync(Path.Combine(newer, "Buildinfo.xml"), "<Main FixVersion=\"neu\" />");
            Directory.SetLastWriteTimeUtc(older, DateTime.UtcNow);
            Directory.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-30));

            var result = await new Sage100Collector([root], registryReader: new SkippingRegistryReader())
                .CollectAsync(CancellationToken.None);

            Assert.Equal("neu", result.Value!.Version);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sage_application_server_reader_maps_whitelisted_helper_data()
    {
        var root = await CreateSageApplicationServerDirectoryAsync();
        try
        {
            const string json = """
                {"settings":[{"section":"serviceDomain","name":"poolMaxSize","value":"20"}],"isolationProcesses":{"serviceDomainCount":2,"activeCount":1,"asyncCount":1,"appDomainIsolatedCount":0,"contextSwitchesTotal":7,"totalCalls":9,"workingSetBytes":1048576,"oldestProcessStartedAt":"2026-08-23T17:19:00+00:00","oldestProcessAgeSeconds":60,"states":[{"state":"Opened/Waiting","count":2}]},"sDataEndpoints":[{"binding":"HttpsWindows","address":"https://server:5494/"}],"soapServices":[{"name":"CustomService","namespace":"urn:test","moduleName":"Test","contractName":"ITest","operations":["Run"]}],"serviceDomains":[{"id":"7","state":"Opened","isActive":true,"forAsyncService":false,"contextSwitches":7,"totalCalls":9,"workingSetBytes":1048576,"createdAt":"2026-08-23T17:19:00+00:00","ageSeconds":60}]}
                """;
            var reader = new SageApplicationServerReader(new FixedSageCommandRunner(new(0, json, "")));
            var result = await reader.CollectAsync(root, CancellationToken.None);
            Assert.Equal(CollectorState.Succeeded, result.Status.State);
            Assert.Equal("20", Assert.Single(result.Value!.Settings).Value);
            Assert.Equal(2, result.Value.IsolationProcesses!.ServiceDomainCount);
            Assert.Equal("HttpsWindows", Assert.Single(result.Value.SDataEndpoints).Binding);
            Assert.Equal("Run", Assert.Single(Assert.Single(result.Value.SoapServices).Operations));
            Assert.Equal("7", Assert.Single(result.Value.ServiceDomains).Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sage_application_server_reader_reports_command_failures_without_snapshot()
    {
        var root = await CreateSageApplicationServerDirectoryAsync();
        try
        {
            var result = await new SageApplicationServerReader(new FixedSageCommandRunner(new(1, "", "access denied")))
                .CollectAsync(root, CancellationToken.None);
            Assert.Equal(CollectorState.Failed, result.Status.State);
            Assert.Null(result.Value);
            Assert.Contains("access denied", result.Status.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sage_application_server_reader_ignores_powershell_progress_noise()
    {
        // Regression: PowerShell serialisiert bei umgeleitetem Fehlerstrom auch den
        // Fortschrittsstrom als CLIXML. Ohne Filter stand "#< CLIXML" als Fehlermeldung
        // im Status und verdeckte die eigentliche Ursache.
        var root = await CreateSageApplicationServerDirectoryAsync();
        try
        {
            const string noisy = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\"><Obj S=\"progress\" /></Objs>\r\nGet-ServerConfiguration: Zugriff verweigert";
            var result = await new SageApplicationServerReader(new FixedSageCommandRunner(new(1, "", noisy)))
                .CollectAsync(root, CancellationToken.None);
            Assert.Equal(CollectorState.Failed, result.Status.State);
            Assert.DoesNotContain("CLIXML", result.Status.Message, StringComparison.Ordinal);
            Assert.Contains("Zugriff verweigert", result.Status.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sage_application_server_reader_reports_a_timeout_distinctly()
    {
        var root = await CreateSageApplicationServerDirectoryAsync();
        try
        {
            var runner = new FixedSageCommandRunner(new SageApplicationServerCommandResult(-1, "", "")
                { TimedOut = true });
            var result = await new SageApplicationServerReader(runner).CollectAsync(root, CancellationToken.None);
            Assert.Equal(CollectorState.Failed, result.Status.State);
            Assert.Contains("Zeitgrenze", result.Status.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sage_application_server_reader_reports_invalid_json_without_snapshot()
    {
        var root = await CreateSageApplicationServerDirectoryAsync();
        try
        {
            var result = await new SageApplicationServerReader(new FixedSageCommandRunner(new(0, "not-json", "")))
                .CollectAsync(root, CancellationToken.None);
            Assert.Equal(CollectorState.Failed, result.Status.State);
            Assert.Null(result.Value);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sage_application_server_reader_skips_when_administration_libraries_are_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"erpdetektiv-sage-as-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = await new SageApplicationServerReader(new FixedSageCommandRunner(new(0, "{}", "")))
                .CollectAsync(root, CancellationToken.None);
            Assert.Equal(CollectorState.Skipped, result.Status.State);
            Assert.Null(result.Value);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Export_serializes_database_file_growth_settings()
    {
        var database = new DatabaseSnapshot("ERP", "ONLINE", 1, "FULL", 160, true, null,
            [new DatabaseFileSnapshot("ERP_Data", "ROWS", 1048576, "FixedBytes", 67108864)]);
        var path = Path.Combine(Path.GetTempPath(), $"erpdetektiv-{Guid.NewGuid():N}.zip");
        try
        {
            DiagnosticExporter.Export(TestSnapshots.WithDatabases(database), path, new ExportOptions());
            using var archive = ZipFile.OpenRead(path);
            using var reader = new StreamReader(archive.GetEntry("sqlserver.json")!.Open());
            var json = reader.ReadToEnd();
            Assert.Contains("growthMode", json, StringComparison.Ordinal);
            Assert.Contains("FixedBytes", json, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Cli_rejects_unknown_options_and_missing_values()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--unbekannt", "x"]));
        // Regression: "--output --pseudonymize" hat zuvor das Flag als Dateinamen verschluckt.
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--output", "--pseudonymize"]));
        var options = CliOptions.Parse(["--output", "paket.zip", "--database", "ERP", "--pseudonymize"]);
        Assert.Equal("paket.zip", options.Output);
        Assert.Equal("ERP", options.Database);
        Assert.True(options.Pseudonymize);
    }

    [Fact]
    public void Cli_uses_no_environment_specific_defaults()
    {
        // Der Auslieferstand darf keine Entwicklungsdatenbank vorbelegen.
        var options = CliOptions.Parse([]);
        Assert.Null(options.Database);
        Assert.Null(options.SqlServer);
        Assert.False(options.Pseudonymize);
    }

    private static async Task<string> CreateSageApplicationServerDirectoryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"erpdetektiv-sage-as-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Sagede.ApplicationServer.Administration.PowerShell.dll"),
            "test");
        await File.WriteAllTextAsync(Path.Combine(root, "Sagede.ApplicationServer.Administration.Client.dll"), "test");
        return root;
    }

    private sealed class ThrowingCollector : ICollector<SystemSnapshot>
    {
        public string Id => "throwing";

        public Task<CollectionResult<SystemSnapshot>> CollectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("expected");
    }

    private sealed class SubStatusCollector : ICollector<SystemSnapshot>
    {
        public string Id => "windows";

        public Task<CollectionResult<SystemSnapshot>> CollectAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectionResult<SystemSnapshot>(TestSnapshots.System(),
                new CollectorStatus(Id, CollectorState.Succeeded, now, now))
            {
                AdditionalStatus =
                    [new CollectorStatus("windows.sub-area", CollectorState.Skipped, now, now, "nicht verfügbar")]
            });
        }
    }

    private sealed class FixedReader(IReadOnlyList<string>? warnings = null) : ISqlServerSnapshotReader
    {
        public Task<SqlServerReadResult> ReadAsync(string server, CancellationToken cancellationToken) =>
            Task.FromResult(new SqlServerReadResult(
                new SqlServerSnapshot(server, "16", "Developer", null, null, null, null, null, [], []),
                warnings ?? []));
    }

    private sealed class FixedSageCommandRunner(SageApplicationServerCommandResult result)
        : ISageApplicationServerCommandRunner
    {
        public Task<SageApplicationServerCommandResult> ExecuteAsync(string installationPath,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
