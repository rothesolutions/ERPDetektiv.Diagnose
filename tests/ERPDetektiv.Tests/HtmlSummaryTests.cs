using System.Text.RegularExpressions;
using ERPDetektiv.Contracts;
using ERPDetektiv.Core;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class HtmlSummaryTests
{
    [Fact]
    public void Untrusted_text_is_encoded()
    {
        var html = HtmlSummary.Create(TestSnapshots.Snapshot() with
        {
            Findings = [new Finding("x", "x", "<script>", "<b>", Severity.Warning, "x", [])]
        });
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Überblick", html, StringComparison.Ordinal);
        Assert.Contains("Erfassungsstatus", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tables_are_balanced_with_and_without_sage_data(bool withSage)
    {
        // Regression: Die Tabellen wurden abschnittsuebergreifend geschlossen und waren nur
        // zufaellig ausbalanciert; ein zusaetzlicher Abschnitt haette den Bericht zerlegt.
        var applicationServer = new SageApplicationServerSnapshot(
            [new SageApplicationServerSettingSnapshot("serviceDomain", "poolMaxSize", "20")],
            new SageIsolationProcessSummary(1, 0, 1, 0, 3, 4, 1024, null, 12,
                [new SageIsolationProcessStateSnapshot("Opened/Waiting", 1)]),
            [new SageServiceEndpointSnapshot("HttpsWindows", "https://server:5494/")], []);
        var snapshot = withSage ? TestSnapshots.WithSage(applicationServer) : TestSnapshots.Snapshot();

        var html = HtmlSummary.Create(snapshot);

        Assert.Equal(Regex.Matches(html, "<table").Count, Regex.Matches(html, "</table>").Count);
        if (withSage)
        {
            Assert.Contains("Sage Application Server", html, StringComparison.Ordinal);
            Assert.Contains("poolMaxSize", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Registry_section_names_the_role_of_every_application_server()
    {
        // Die Rolle entscheidet darueber, wie die Lastwerte eines Servers zu lesen sind.
        // Ein Server im Einzelbetrieb bedient Schnittstellen und traegt deshalb ein
        // anderes Profil als die Server der Lastverteilung.
        var registry = TestSnapshots.Registry(applicationServers:
        [
            new SageRegistryApplicationServerSnapshot("SAGE-A", "Started", 3, []),
            new SageRegistryApplicationServerSnapshot("SAGE-B", "Started", 0, [])
        ]);

        var html = HtmlSummary.Create(TestSnapshots.WithRegistry(registry));

        Assert.Equal(Regex.Matches(html, "<table").Count, Regex.Matches(html, "</table>").Count);
        Assert.Contains("Sage-Umgebung", html, StringComparison.Ordinal);
        Assert.Contains("Master, Lastverteilung", html, StringComparison.Ordinal);
        Assert.Contains("Einzelbetrieb", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_features_are_explained()
    {
        var database = TestSnapshots.UserDatabase() with
        {
            QueryStoreCollectionState = FeatureCollectionState.NotSupported,
            LogSpaceCollectionState = FeatureCollectionState.AccessDenied
        };
        var html = HtmlSummary.Create(TestSnapshots.WithDatabases(database));
        Assert.Contains("Nicht unterst&#252;tzt", html, StringComparison.Ordinal);
        Assert.Contains("Berechtigung fehlt", html, StringComparison.Ordinal);
        Assert.Contains("Log-Auslastung", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Drives_and_hardware_appear_in_the_report()
    {
        var snapshot = TestSnapshots.Snapshot();
        snapshot = snapshot with
        {
            System = snapshot.System! with
            {
                ProcessorName = "Intel Xeon Gold 6338", TotalMemoryBytes = 68719476736
            }
        };
        var html = HtmlSummary.Create(snapshot);
        Assert.Contains("Laufwerke", html, StringComparison.Ordinal);
        Assert.Contains("Intel Xeon Gold 6338", html, StringComparison.Ordinal);
        Assert.Contains("64 GiB", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_are_ordered_by_severity()
    {
        var snapshot = TestSnapshots.Snapshot() with
        {
            Findings =
            [
                new Finding("a", "c", "Infomeldung", "d", Severity.Info, "r", []),
                new Finding("b", "c", "Kritisch", "d", Severity.Critical, "r", [])
            ]
        };
        var html = HtmlSummary.Create(snapshot);
        Assert.True(html.IndexOf("Kritisch", StringComparison.Ordinal) <
                    html.IndexOf("Infomeldung", StringComparison.Ordinal));
    }
}
