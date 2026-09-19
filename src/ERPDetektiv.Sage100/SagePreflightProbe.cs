using ERPDetektiv.Contracts;

namespace ERPDetektiv.Sage100;

/// <summary>Prueft, ob Sage 100 und der Application Server erfassbar sind.</summary>
public sealed class SagePreflightProbe(IEnumerable<string>? searchRoots = null) : IPreflightProbe
{
    public string Id => "sage100";

    public async Task<IReadOnlyList<PreflightResult>> RunAsync(CancellationToken cancellationToken)
    {
        var collector = new Sage100Collector(searchRoots, new SkippingApplicationServerReader(),
            new SkippingRegistryReader());
        var result = await collector.CollectAsync(cancellationToken);
        var snapshot = result.Value;
        var results = new List<PreflightResult>();

        if (snapshot?.InstallationPath is null)
        {
            results.Add(new PreflightResult(Id, "Sage-100-Installation", PreflightState.Warning,
                $"Keine Installation in den durchsuchten Pfaden erkannt. Bei abweichendem Installationsort {Sage100Collector.SearchRootVariable} setzen."));
            return results;
        }

        results.Add(new PreflightResult(Id, "Sage-100-Installation", PreflightState.Ok,
            $"{Sage100Collector.DescribeVersion(snapshot.Version)} erkannt, {snapshot.Components.Count} Komponenten."));

        if (!snapshot.ApplicationServerDetected)
        {
            results.Add(new PreflightResult(Id, "Application Server", PreflightState.Skipped,
                "Kein lokaler Application Server – Laufzeitdaten der Service-Domains entfallen."));
            return results;
        }

        // Die Administrationsbibliotheken sind 32-Bit und brauchen die passende PowerShell.
        var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64",
            "WindowsPowerShell", "v1.0", "powershell.exe");
        results.Add(File.Exists(powerShell)
            ? new PreflightResult(Id, "32-Bit-PowerShell", PreflightState.Ok, "Vorhanden.")
            : new PreflightResult(Id, "32-Bit-PowerShell", PreflightState.Warning,
                "Nicht gefunden. Die Application-Server-Diagnose entfällt; der übrige Snapshot bleibt vollständig."));
        results.Add(new PreflightResult(Id, "Application Server", PreflightState.Ok,
            "Erkannt. Die Laufzeitabfrage läuft erst bei der Diagnose."));
        return results;
    }

    /// <summary>Ueberspringt die Registry-Abfrage; der Vorabcheck soll schnell bleiben.</summary>
    /// <remarks>
    /// Gleiche Festlegung wie bei der Laufzeitabfrage: Der Vorabcheck beantwortet, ob eine
    /// Erfassung moeglich ist, und erfasst selbst nichts.
    /// </remarks>
    private sealed class SkippingRegistryReader : ISageRegistryReader
    {
        public Task<CollectionResult<SageRegistrySnapshot>> CollectAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectionResult<SageRegistrySnapshot>(null,
                new CollectorStatus(SageRegistryReader.Id, CollectorState.Skipped, now, now,
                    "Im Vorabcheck nicht abgefragt.")));
        }
    }

    /// <summary>Ueberspringt die Laufzeitabfrage; der Vorabcheck soll schnell bleiben.</summary>
    private sealed class SkippingApplicationServerReader : ISageApplicationServerReader
    {
        public Task<CollectionResult<SageApplicationServerSnapshot>> CollectAsync(string installationPath,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectionResult<SageApplicationServerSnapshot>(null,
                new CollectorStatus(SageApplicationServerReader.Id, CollectorState.Skipped, now, now,
                    "Im Vorabcheck nicht abgefragt.")));
        }
    }
}
