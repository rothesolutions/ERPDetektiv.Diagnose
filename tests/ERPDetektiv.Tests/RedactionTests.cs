using System.IO.Compression;
using System.Text;
using ERPDetektiv.Contracts;
using ERPDetektiv.Core;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void Pseudonymization_is_deterministic_and_optional()
    {
        var redacted = Redactor.Redact(TestSnapshots.Snapshot(), true);
        var repeated = Redactor.Redact(TestSnapshots.Snapshot(), true);
        Assert.NotEqual("HOST-A", redacted.System!.HostName);
        Assert.Equal(redacted.System.HostName, repeated.System!.HostName);
        Assert.Equal("HOST-A", Redactor.Redact(TestSnapshots.Snapshot(), false).System!.HostName);
    }

    [Fact]
    public void Database_names_stay_readable_by_design()
    {
        // Bewusste Produktentscheidung: Aus Datenbanknamen laesst sich das technische
        // Umfeld ableiten (etwa DWData -> DocuWare). Sie werden deshalb nicht ersetzt.
        var redacted = Redactor.Redact(TestSnapshots.Snapshot(), true);
        Assert.Equal("ERP", redacted.SqlServer!.Databases[0].Name);
    }

    [Fact]
    public void Protected_identifiers_are_replaced_inside_finding_evidence()
    {
        // Regression: Zuvor ersetzte der Redactor nur einzelne Snapshot-Felder. Ein Check,
        // der dieselbe Adresse als Evidence weiterreichte, veroeffentlichte den Hostnamen
        // trotz aktivierter Pseudonymisierung im Klartext.
        var applicationServer = new SageApplicationServerSnapshot([], null,
            [new SageServiceEndpointSnapshot("HttpsNone", "https://SAGE-HOST:5494/sdata")], []);
        var finding = new Finding("sage.sdata.anonymous-endpoint", "Sage 100", "SData-Endpunkt ohne Anmeldung",
            "Der Endpunkt https://SAGE-HOST:5494/sdata ist konfiguriert.", Severity.Info, "prüfen",
            [new Evidence("address", "https://SAGE-HOST:5494/sdata")]);
        var snapshot = TestSnapshots.WithSage(applicationServer) with { Findings = [finding] };

        var redacted = Redactor.Redact(snapshot, true);

        var evidence = Assert.Single(Assert.Single(redacted.Findings).Evidence);
        Assert.DoesNotContain("SAGE-HOST", evidence.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SAGE-HOST", Assert.Single(redacted.Findings).Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(":5494/sdata", evidence.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_server_host_is_replaced_even_with_instance_and_protocol()
    {
        var snapshot = TestSnapshots.Snapshot();
        snapshot = snapshot with
        {
            SqlServer = snapshot.SqlServer! with { Server = "tcp:SQLSRV01\\SAGE100,1433" },
            Findings =
            [
                new Finding("x", "SQL Server", "n", "Verbindung zu SQLSRV01 geprüft.", Severity.Info, "r",
                    [new Evidence("server", "tcp:SQLSRV01\\SAGE100,1433")])
            ]
        };

        var redacted = Redactor.Redact(snapshot, true);

        Assert.DoesNotContain("SQLSRV01", redacted.SqlServer!.Server, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLSRV01", Assert.Single(redacted.Findings).Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLSRV01", Assert.Single(Assert.Single(redacted.Findings).Evidence).Value,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secrets_are_removed_from_free_text()
    {
        var finding = new Finding("x", "x", "Password=do-not-export", "pwd=supersecret; clientSecret=abc",
            Severity.Info, "none", [new Evidence("k", "Password=another")]);
        var redacted = Redactor.Redact(TestSnapshots.Snapshot() with { Findings = [finding] }, false);
        var result = Assert.Single(redacted.Findings);
        Assert.DoesNotContain("supersecret", result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("another", Assert.Single(result.Evidence).Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_contains_expected_files_and_no_secrets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"erpdetektiv-{Guid.NewGuid():N}.zip");
        try
        {
            var finding = new Finding("test", "test", "Password=do-not-export", "Password=supersecret", Severity.Info,
                "none", []);
            DiagnosticExporter.Export(TestSnapshots.Snapshot() with { Findings = [finding] }, path,
                new ExportOptions(PseudonymizeIdentifiers: true));
            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(7, archive.Entries.Count);
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("summary.html"));
            var all = string.Join("", archive.Entries.Select(entry =>
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            }));
            Assert.DoesNotContain("HOST-A", all, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"ERP\"", all, StringComparison.Ordinal);
            Assert.DoesNotContain("supersecret", all, StringComparison.Ordinal);
            Assert.Contains("formatVersion", all, StringComparison.Ordinal);
            Assert.Contains("\"server\": \"id-", all, StringComparison.Ordinal);
            Assert.Contains("\"pseudonymized\": true", all, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Manifest_reports_the_real_tool_version()
    {
        // Regression: Die Version war fest verdrahtet, das Manifest behauptete unabhaengig
        // vom Build immer 0.1.0.
        Assert.NotEqual("0.0.0", DiagnosticExporter.ToolVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+", DiagnosticExporter.ToolVersion);
    }
}
