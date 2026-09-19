using ERPDetektiv.Contracts;
using ERPDetektiv.Core;
using ERPDetektiv.Sage100;
using ERPDetektiv.SqlServer;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class PreflightTests
{
    [Fact]
    public async Task Export_path_probe_accepts_a_writable_directory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"erpdetektiv-{Guid.NewGuid():N}", "paket.zip");
        var result = Assert.Single(await new ExportPathProbe(path).RunAsync(CancellationToken.None));
        Assert.Equal(PreflightState.Ok, result.State);
    }

    [Fact]
    public async Task Export_path_probe_warns_before_overwriting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"erpdetektiv-{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(path, "vorhanden");
        try
        {
            var result = Assert.Single(await new ExportPathProbe(path).RunAsync(CancellationToken.None));
            Assert.Equal(PreflightState.Warning, result.State);
            Assert.Contains("überschrieben", result.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Export_path_probe_reports_an_invalid_target()
    {
        var result = Assert.Single(await new ExportPathProbe("\0ungültig\\paket.zip").RunAsync(CancellationToken.None));
        Assert.Equal(PreflightState.Failed, result.State);
    }

    [Fact]
    public async Task Sql_probe_skips_without_a_connection_string()
    {
        var result = Assert.Single(await new SqlPreflightProbe(null, null).RunAsync(CancellationToken.None));
        Assert.Equal(PreflightState.Skipped, result.State);
    }

    [Fact]
    public async Task Sql_probe_reports_an_unreachable_server_as_failure()
    {
        // Port 1 nimmt keine Verbindungen an; kurzer Timeout genuegt.
        var probe = new SqlPreflightProbe("Server=127.0.0.1,1;Connect Timeout=2;Encrypt=False", null);
        var result = Assert.Single(await probe.RunAsync(CancellationToken.None));
        Assert.Equal(PreflightState.Failed, result.State);
        Assert.Contains("Keine Verbindung möglich", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sage_probe_warns_when_no_installation_is_found()
    {
        var probe = new SagePreflightProbe([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())]);
        var result = Assert.Single(await probe.RunAsync(CancellationToken.None));
        Assert.Equal(PreflightState.Warning, result.State);
        Assert.Contains(Sage100Collector.SearchRootVariable, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sage_probe_reports_a_detected_installation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"erpdetektiv-sage-{Guid.NewGuid():N}");
        var installation = Path.Combine(root, "Sage", "Sage 100", "9.0");
        try
        {
            Directory.CreateDirectory(installation);
            await File.WriteAllTextAsync(Path.Combine(installation, "Buildinfo.xml"),
                "<Main FixVersion=\"Sage 100 (9.0.11.4)\" />");

            var results = await new SagePreflightProbe([root]).RunAsync(CancellationToken.None);

            var installationResult = results.First(result => result.Name == "Sage-100-Installation");
            Assert.Equal(PreflightState.Ok, installationResult.State);
            Assert.Contains("9.0.11.4", installationResult.Message, StringComparison.Ordinal);
            // Ohne Application-Server-Komponente wird der Laufzeitteil uebersprungen,
            // nicht als Fehler gemeldet.
            Assert.Equal(PreflightState.Skipped,
                results.First(result => result.Name == "Application Server").State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Sage 100 (9.0.11.4)", "Sage 100 (9.0.11.4)")]
    [InlineData("9.0.11.4", "Sage 100 9.0.11.4")]
    [InlineData(null, "Sage 100 ohne Buildinfo")]
    public void Version_description_does_not_repeat_the_product_name(string? version, string expected) =>
        // Die FixVersion enthaelt den Produktnamen bereits; ein fester Praefix ergab
        // "Sage 100 Sage 100 (9.0.11.4)".
        Assert.Equal(expected, Sage100Collector.DescribeVersion(version));

    [Fact]
    public async Task Runner_aggregates_results_and_survives_a_throwing_probe()
    {
        var report = await new PreflightRunner([
            new FixedProbe("a", PreflightState.Ok),
            new ThrowingProbe(),
            new FixedProbe("c", PreflightState.Warning)
        ]).RunAsync(null, CancellationToken.None);

        Assert.Equal(3, report.Results.Count);
        Assert.True(report.HasFailures);
        Assert.True(report.HasWarnings);
        Assert.Contains("1 von 3", report.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_reports_a_clean_result_as_ready()
    {
        var report = await new PreflightRunner([new FixedProbe("a", PreflightState.Ok)])
            .RunAsync(null, CancellationToken.None);
        Assert.False(report.HasFailures);
        Assert.False(report.HasWarnings);
        Assert.Contains("vollständiger Lauf", report.Summary(), StringComparison.Ordinal);
    }

    private sealed class FixedProbe(string id, PreflightState state) : IPreflightProbe
    {
        public string Id => id;

        public Task<IReadOnlyList<PreflightResult>> RunAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PreflightResult>>([new PreflightResult(id, id, state, "test")]);
    }

    private sealed class ThrowingProbe : IPreflightProbe
    {
        public string Id => "throwing";

        public Task<IReadOnlyList<PreflightResult>> RunAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("erwartet");
    }
}
