using ERPDetektiv.Checks;
using ERPDetektiv.Contracts;
using ERPDetektiv.Core;
using ERPDetektiv.Sage100;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class SageRegistryReaderTests
{
    /// <summary>Antwort der Registry-Abfrage, wie sie das Skript liefert.</summary>
    private sealed class StubRunner(string output, int exitCode = 0, bool timedOut = false) : ISageRegistryCommandRunner
    {
        public Task<SageRegistryCommandResult> ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SageRegistryCommandResult(exitCode, output, string.Empty) { TimedOut = timedOut });
    }

    /// <summary>Nachbildung des Sage-Servers einer Drei-Server-Farm.</summary>
    private const string FarmPayload = """
        {"Found":true,"ProductVersion":"9.0","SageServer":"\\\\SAGE-A","IsSageServer":true,
         "Components":[{"Name":"Office Line","InstallationPath":"C:\\Sage\\Sage 100\\9.0\\"}],
         "ApplicationServers":[
           {"Host":"SAGE-A","State":"Started","Capability":"3","SDataEndpoints":[
             {"Binding":"HttpsToken","Address":"https://sage-a:5486/sdata","Authentication":"Basic"}]},
           {"Host":"SAGE-B","State":"Started","Capability":"0","SDataEndpoints":[]},
           {"Host":"SAGE-C","State":"Started","Capability":"1","SDataEndpoints":[]}],
         "BlobStorageServers":[{"Host":"SAGE-A","Status":0,"Activated":1,"Endpoints":[
           {"Binding":"EndPoint1","Address":"https://sage-a:4000/BlobStorage","Authentication":"Basic"}]}],
         "Gateways":[{"Host":"SAGE-A","State":"Started","Endpoint":"https://sage-a:4337/api"}],
         "Stations":[{"Host":"CLIENT-1","Endpoint":"net.tcp://client-1:5499/S","State":1}],
         "Datasources":[{"Name":"FirmaD","ServerName":"SQL-A","DatabaseName":"FirmaC",
           "Applications":"ReweAbf","ServerVersion":160,"MandatorCount":1}],
         "DatasourceKeyNames":["FirmaD","FirmaF"],
         "NamedUsers":[{"Application":"Abf","Count":81},{"Application":"Rewe","Count":22}],
         "Values":[{"Path":"Options","Name":"LargeAddressAwareClient","Value":"1"}]}
        """;

    [Fact]
    public async Task Reader_maps_the_farm_view()
    {
        var result = await new SageRegistryReader(new StubRunner(FarmPayload)).CollectAsync(CancellationToken.None);

        Assert.NotNull(result.Value);
        var snapshot = result.Value;
        Assert.Equal(CollectorState.Succeeded, result.Status.State);
        Assert.Equal("9.0", snapshot.ProductVersion);
        Assert.Equal(3, snapshot.ApplicationServers.Count);
        Assert.Single(snapshot.ApplicationServers, server => server.IsMaster);
        Assert.Equal(2, snapshot.ApplicationServers.Count(server => server.ParticipatesInLoadBalancing));
        Assert.Equal("FirmaC", Assert.Single(snapshot.Datasources).DatabaseName);
        Assert.Equal(81, snapshot.NamedUsers.Single(entry => entry.Application == "Abf").Count);
    }

    [Fact]
    public async Task Reader_keeps_the_authentication_of_an_endpoint()
    {
        // Binding "HttpsToken" mit Authentifizierung "Basic" ist auf allen geprueften
        // Systemen die Sage-Voreinstellung. Ohne den Wert waere der Unterschied unsichtbar.
        var result = await new SageRegistryReader(new StubRunner(FarmPayload)).CollectAsync(CancellationToken.None);

        Assert.NotNull(result.Value);
        var endpoint = Assert.Single(result.Value.ApplicationServers[0].SDataEndpoints);
        Assert.Equal("HttpsToken", endpoint.Binding);
        Assert.Equal("Basic", endpoint.Authentication);
    }

    [Fact]
    public async Task Reader_reports_a_satellite_as_partial_and_names_the_sage_server()
    {
        // Auf einem zusaetzlichen Application Server fehlen Datenquellen und Server-Liste
        // planmaessig. Das ist eine Teilerfassung, kein leeres Ergebnis.
        const string payload = """
            {"Found":true,"ProductVersion":"9.0","SageServer":"\\\\SAGE-A","IsSageServer":false,
             "Components":[],"ApplicationServers":[],"BlobStorageServers":[],"Gateways":[],"Stations":[],
             "Datasources":[],"DatasourceKeyNames":[],"NamedUsers":[],"Values":[]}
            """;

        var result = await new SageRegistryReader(new StubRunner(payload)).CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorState.Partial, result.Status.State);
        Assert.Contains("SAGE-A", result.Status.Message);
    }

    [Fact]
    public async Task Reader_skips_when_no_version_is_registered()
    {
        var result = await new SageRegistryReader(new StubRunner("""{"Found":false}"""))
            .CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorState.Skipped, result.Status.State);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Reader_reports_a_timeout_as_failure()
    {
        var result = await new SageRegistryReader(new StubRunner(string.Empty, -1, timedOut: true))
            .CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorState.Failed, result.Status.State);
    }

    [Fact]
    public async Task Reader_survives_unreadable_output()
    {
        var result = await new SageRegistryReader(new StubRunner("kein JSON"))
            .CollectAsync(CancellationToken.None);

        Assert.Equal(CollectorState.Failed, result.Status.State);
        Assert.Null(result.Value);
    }
}

public sealed class SageRegistryCheckTests
{
    private static SageRegistryApplicationServerSnapshot Server(string host, int? capability) =>
        new(host, "Started", capability, []);

    [Fact]
    public void Master_check_accepts_exactly_one_master() =>
        Assert.Empty(new SageMasterCountCheck().Evaluate(TestSnapshots.WithRegistry(TestSnapshots.Registry(
            applicationServers: [Server("SAGE-A", 3), Server("SAGE-B", 0), Server("SAGE-C", 1)]))));

    [Fact]
    public void Master_check_finds_a_farm_without_a_master()
    {
        var finding = Assert.Single(new SageMasterCountCheck().Evaluate(TestSnapshots.WithRegistry(
            TestSnapshots.Registry(applicationServers: [Server("SAGE-A", 1), Server("SAGE-B", 1)]))));
        Assert.Equal("sage.registry.master.missing", finding.Id);
    }

    [Fact]
    public void Master_check_finds_more_than_one_master()
    {
        var finding = Assert.Single(new SageMasterCountCheck().Evaluate(TestSnapshots.WithRegistry(
            TestSnapshots.Registry(applicationServers: [Server("SAGE-A", 3), Server("SAGE-B", 3)]))));
        Assert.Equal("sage.registry.master.ambiguous", finding.Id);
    }

    [Fact]
    public void Master_check_stays_silent_without_a_capability_flag() =>
        Assert.Empty(new SageMasterCountCheck().Evaluate(TestSnapshots.WithRegistry(
            TestSnapshots.Registry(applicationServers: [Server("SAGE-A", null)]))));

    [Fact]
    public void Master_check_stays_silent_on_a_satellite()
    {
        // Ein Satellit fuehrt keine Server-Liste. Eine Bewertung waere eine Aussage
        // ueber Daten, die dort gar nicht stehen.
        var registry = TestSnapshots.Registry(isSageServer: false, applicationServers: []);
        Assert.Empty(new SageMasterCountCheck().Evaluate(TestSnapshots.WithRegistry(registry)));
    }

    [Fact]
    public void Datasource_key_check_finds_orphaned_keys()
    {
        var registry = TestSnapshots.Registry(
            datasources: [TestSnapshots.Datasource("FirmaA")],
            datasourceKeyNames: ["FirmaA", "FirmaE", "FirmaF"]);

        var finding = Assert.Single(new SageDatasourceKeyConsistencyCheck().Evaluate(
            TestSnapshots.WithRegistry(registry)));

        Assert.Equal("sage.registry.datasource-key.orphaned", finding.Id);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("FirmaE", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Datasource_key_check_warns_about_a_datasource_without_a_key()
    {
        var registry = TestSnapshots.Registry(
            datasources: [TestSnapshots.Datasource("FirmaA"), TestSnapshots.Datasource("FirmaB")],
            datasourceKeyNames: ["FirmaA"]);

        var finding = Assert.Single(new SageDatasourceKeyConsistencyCheck().Evaluate(
            TestSnapshots.WithRegistry(registry)));

        Assert.Equal("sage.registry.datasource.without-key", finding.Id);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void Datasource_key_check_exempts_the_system_datasource()
    {
        // OLGlobal fuehrt auf keinem der geprueften Systeme einen Schluessel.
        var registry = TestSnapshots.Registry(
            datasources: [TestSnapshots.Datasource("FirmaA"), TestSnapshots.Datasource("OLGlobal", applications: "OLGlobal")],
            datasourceKeyNames: ["FirmaA"]);

        Assert.Empty(new SageDatasourceKeyConsistencyCheck().Evaluate(TestSnapshots.WithRegistry(registry)));
    }

    [Fact]
    public void Blobstorage_check_finds_a_registration_without_product_and_service()
    {
        var registry = TestSnapshots.Registry(blobStorageServers:
            [new SageRegistryBlobStorageSnapshot("HOST-A", 0, true, [new SageServiceEndpointSnapshot("EndPoint1", "https://host-a:4000/BlobStorage")])]);

        var finding = Assert.Single(new SageBlobStorageRegistrationCheck().Evaluate(TestSnapshots.WithRegistry(
            registry, new ServiceSnapshot("SagedeApplicationServerService90", "Running", "Auto"))));

        Assert.Equal("sage.registry.blobstorage.without-service", finding.Id);
    }

    [Fact]
    public void Blobstorage_check_accepts_an_installed_product()
    {
        var registry = TestSnapshots.Registry(
            components: [new SageRegistryComponentSnapshot("BlobStorage Server", @"C:\Sage\Blobstorage Server\9.0\")],
            blobStorageServers: [new SageRegistryBlobStorageSnapshot("HOST-A", 0, true, [])]);

        Assert.Empty(new SageBlobStorageRegistrationCheck().Evaluate(TestSnapshots.WithRegistry(
            registry, new ServiceSnapshot("SagedeApplicationServerService90", "Running", "Auto"))));
    }

    [Fact]
    public void Blobstorage_check_accepts_a_running_service_without_a_product_entry()
    {
        var registry = TestSnapshots.Registry(blobStorageServers:
            [new SageRegistryBlobStorageSnapshot("HOST-A", 0, true, [])]);

        Assert.Empty(new SageBlobStorageRegistrationCheck().Evaluate(TestSnapshots.WithRegistry(
            registry, new ServiceSnapshot("SagedeBlobStorageServer90", "Running", "Auto"))));
    }

    [Fact]
    public void Blobstorage_check_stays_silent_without_a_service_list()
    {
        // Ist die Windows-Inventur fehlgeschlagen, waere jede Registrierung faelschlich
        // auffaellig.
        var registry = TestSnapshots.Registry(blobStorageServers:
            [new SageRegistryBlobStorageSnapshot("HOST-A", 0, true, [])]);

        Assert.Empty(new SageBlobStorageRegistrationCheck().Evaluate(TestSnapshots.WithRegistry(registry)));
    }

    [Fact]
    public void Blobstorage_check_ignores_other_hosts()
    {
        // Fuer einen fremden Server liegen weder Verzeichnis noch Dienstliste vor.
        var registry = TestSnapshots.Registry(blobStorageServers:
            [new SageRegistryBlobStorageSnapshot("SAGE-B", 0, true, [])]);

        Assert.Empty(new SageBlobStorageRegistrationCheck().Evaluate(TestSnapshots.WithRegistry(
            registry, new ServiceSnapshot("SagedeApplicationServerService90", "Running", "Auto"))));
    }

    [Fact]
    public void Server_version_check_finds_a_datasource_left_behind()
    {
        var registry = TestSnapshots.Registry(datasources:
        [
            TestSnapshots.Datasource("OLDemo", serverVersion: 120),
            TestSnapshots.Datasource("Produktiv", serverVersion: 150),
            TestSnapshots.Datasource("Kopie", serverVersion: 150)
        ]);

        var finding = Assert.Single(new SageDatasourceServerVersionCheck().Evaluate(
            TestSnapshots.WithRegistry(registry)));

        Assert.Equal("sage.registry.datasource.stale-server-version", finding.Id);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("SQL 15.x", finding.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_version_check_accepts_a_consistent_instance() =>
        Assert.Empty(new SageDatasourceServerVersionCheck().Evaluate(TestSnapshots.WithRegistry(
            TestSnapshots.Registry(datasources:
            [
                TestSnapshots.Datasource("A", serverVersion: 160), TestSnapshots.Datasource("B", serverVersion: 160)
            ]))));

    [Fact]
    public void Server_version_check_compares_only_within_one_instance()
    {
        // Zwei Instanzen duerfen unterschiedliche Versionen tragen; verglichen wird je Server.
        var registry = TestSnapshots.Registry(datasources:
        [
            TestSnapshots.Datasource("A", "SQL-A", 160), TestSnapshots.Datasource("B", "SQL-B", 150)
        ]);

        Assert.Empty(new SageDatasourceServerVersionCheck().Evaluate(TestSnapshots.WithRegistry(registry)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public void Large_address_aware_check_reports_the_inactive_state(string value)
    {
        var registry = TestSnapshots.Registry(values:
            [new SageRegistryValueSnapshot("Options", "LargeAddressAwareClient", value)]);

        var finding = Assert.Single(new SageLargeAddressAwareClientCheck().Evaluate(
            TestSnapshots.WithRegistry(registry)));

        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void Large_address_aware_check_reports_an_absent_value() =>
        Assert.Single(new SageLargeAddressAwareClientCheck().Evaluate(
            TestSnapshots.WithRegistry(TestSnapshots.Registry())));

    [Fact]
    public void Large_address_aware_check_stays_silent_when_active() =>
        Assert.Empty(new SageLargeAddressAwareClientCheck().Evaluate(TestSnapshots.WithRegistry(
            TestSnapshots.Registry(values:
                [new SageRegistryValueSnapshot("Options", "LargeAddressAwareClient", "1")]))));

    [Fact]
    public void Registry_checks_stay_silent_without_registry_data()
    {
        var empty = new DiagnosticSnapshot(null, null, null, [], []);
        ICheck[] checks =
        [
            new SageMasterCountCheck(), new SageDatasourceKeyConsistencyCheck(),
            new SageBlobStorageRegistrationCheck(), new SageDatasourceServerVersionCheck(),
            new SageLargeAddressAwareClientCheck()
        ];
        Assert.Empty(checks.SelectMany(check => check.Evaluate(empty)));
    }
}

public sealed class SageRegistryRedactionTests
{
    [Fact]
    public void Registry_host_names_are_pseudonymized()
    {
        var registry = TestSnapshots.Registry(
            applicationServers:
            [
                new SageRegistryApplicationServerSnapshot("SAGE-HOST", "Started", 3,
                    [new SageServiceEndpointSnapshot("HttpsBasic", "https://sage-host:5493/sdata")])
            ],
            datasources: [TestSnapshots.Datasource("FirmaA", "SQL-HOST\\SAGE2019")],
            sageServer: @"\\SAGE-HOST");

        var redacted = Redactor.Redact(TestSnapshots.WithRegistry(registry), true).Sage100!.Registry!;

        var server = Assert.Single(redacted.ApplicationServers);
        Assert.DoesNotContain("SAGE-HOST", server.Host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SAGE-HOST", server.SDataEndpoints[0].Address, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":5493/sdata", server.SDataEndpoints[0].Address, StringComparison.Ordinal);
        Assert.DoesNotContain("SAGE-HOST", redacted.SageServer!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQL-HOST", redacted.Datasources[0].ServerName!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Datasource_and_database_names_stay_readable_by_design()
    {
        // Gleiche Festlegung wie bei SQL-Datenbanknamen: Aus ihnen laesst sich das
        // technische Umfeld ableiten, und genau das ist fuer eine Diagnose wertvoll.
        var registry = TestSnapshots.Registry(datasources: [TestSnapshots.Datasource("FirmaD", "SQL-HOST")]);

        var redacted = Redactor.Redact(TestSnapshots.WithRegistry(registry), true).Sage100!.Registry!;

        Assert.Equal("FirmaD", redacted.Datasources[0].Name);
        Assert.Equal("FirmaD", redacted.Datasources[0].DatabaseName);
    }
}

public sealed class Sage100DetectionTests
{
    private static string CreateInstallation(string root, string version, string fixVersion)
    {
        var installation = Path.Combine(root, "Sage", "Sage 100", version);
        Directory.CreateDirectory(installation);
        File.WriteAllText(Path.Combine(installation, "Buildinfo.xml"), $"<Main FixVersion=\"{fixVersion}\" />");
        return installation;
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"erpdetektiv-reg-{Guid.NewGuid():N}");

    private static string Missing() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public async Task Registry_path_is_used_when_the_directory_search_finds_nothing()
    {
        // Der eigentliche Gewinn: Eine Installation ausserhalb der Standardpfade wird
        // erkannt, ohne dass jemand ERPDETEKTIV_SAGE_ROOT setzen muss.
        var root = TempRoot();
        try
        {
            var installation = CreateInstallation(root, "9.0", "Sage 100 (9.0.11.4)");
            var registry = TestSnapshots.Registry(components:
                [new SageRegistryComponentSnapshot("Office Line", installation + Path.DirectorySeparatorChar)]);

            var result = await new Sage100Collector([Missing()], registryReader: new StubRegistryReader(registry))
                .CollectAsync(CancellationToken.None);

            Assert.Equal(installation, result.Value!.InstallationPath);
            Assert.Equal("Sage 100 (9.0.11.4)", result.Value.Version);
            Assert.Equal(CollectorState.Succeeded, result.Status.State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_registry_entry_without_directory_is_ignored()
    {
        // Ein Eintrag ueberlebt die Deinstallation. Ein gemeldeter Pfad, den es nicht mehr
        // gibt, waere schlechter als ein ehrliches "nicht erkannt".
        var registry = TestSnapshots.Registry(components:
            [new SageRegistryComponentSnapshot("Office Line", Missing())]);

        var result = await new Sage100Collector([Missing()], registryReader: new StubRegistryReader(registry))
            .CollectAsync(CancellationToken.None);

        Assert.Null(result.Value!.InstallationPath);
        // Die Registry hat geliefert, die Installation fehlt: Teilerfassung, nicht uebersprungen.
        Assert.Equal(CollectorState.Partial, result.Status.State);
    }

    [Fact]
    public async Task An_explicit_search_root_takes_precedence_over_the_registry()
    {
        var explicitRoot = TempRoot();
        var registryRoot = TempRoot();
        try
        {
            var expected = CreateInstallation(explicitRoot, "9.0", "ausdrücklich");
            var other = CreateInstallation(registryRoot, "9.0", "aus der Registry");
            var registry = TestSnapshots.Registry(components:
                [new SageRegistryComponentSnapshot("Office Line", other)]);

            var result = await new Sage100Collector([explicitRoot], registryReader: new StubRegistryReader(registry))
                .CollectAsync(CancellationToken.None);

            Assert.Equal(expected, result.Value!.InstallationPath);
        }
        finally
        {
            if (Directory.Exists(explicitRoot)) Directory.Delete(explicitRoot, true);
            if (Directory.Exists(registryRoot)) Directory.Delete(registryRoot, true);
        }
    }

    [Fact]
    public async Task Components_combine_both_sources()
    {
        // Beide Quellen sind unvollstaendig, und zwar unterschiedlich: Ein Produkt ohne
        // eigenen Versionsknoten fehlt in der Registry, eines ausserhalb des Sage-Ordners
        // in der Verzeichnisliste.
        var root = TempRoot();
        try
        {
            CreateInstallation(root, "9.0", "Sage 100 (9.0.11.4)");
            Directory.CreateDirectory(Path.Combine(root, "Sage", "Zusatzmodul"));
            var registry = TestSnapshots.Registry(components:
            [
                new SageRegistryComponentSnapshot("Office Line", Path.Combine(root, "Sage", "Sage 100", "9.0")),
                new SageRegistryComponentSnapshot("BlobStorage Server", Missing())
            ]);

            var result = await new Sage100Collector([root], registryReader: new StubRegistryReader(registry))
                .CollectAsync(CancellationToken.None);

            Assert.Contains("Zusatzmodul", result.Value!.Components);
            Assert.Contains("BlobStorage Server", result.Value.Components);
            Assert.Contains("Sage 100", result.Value.Components);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

public sealed class SageSqlServerHintTests
{
    [Fact]
    public void System_datasource_decides()
    {
        var registry = TestSnapshots.Registry(datasources:
        [
            TestSnapshots.Datasource("FirmaA", "SQL-ALT"),
            TestSnapshots.Datasource("OLGlobal", "SQL-NEU", applications: "OLGlobal")
        ]);

        Assert.Equal("SQL-NEU", SageSqlServerHint.FromRegistry(registry));
    }

    [Fact]
    public void Without_the_system_datasource_the_most_common_server_wins()
    {
        var registry = TestSnapshots.Registry(datasources:
        [
            TestSnapshots.Datasource("A", "SQL-A"), TestSnapshots.Datasource("B", "SQL-B"),
            TestSnapshots.Datasource("C", "SQL-B")
        ]);

        Assert.Equal("SQL-B", SageSqlServerHint.FromRegistry(registry));
    }

    [Fact]
    public void An_instance_name_is_kept()
    {
        var registry = TestSnapshots.Registry(datasources:
            [TestSnapshots.Datasource("A", @"SAGEHOST03\SAGE2019", applications: "OLGlobal")]);

        Assert.Equal(@"SAGEHOST03\SAGE2019", SageSqlServerHint.FromRegistry(registry));
    }

    [Fact]
    public void Nothing_is_suggested_on_a_satellite() =>
        Assert.Null(SageSqlServerHint.FromRegistry(TestSnapshots.Registry(isSageServer: false)));

    [Fact]
    public void Nothing_is_suggested_without_registry_data() => Assert.Null(SageSqlServerHint.FromRegistry(null));
}
