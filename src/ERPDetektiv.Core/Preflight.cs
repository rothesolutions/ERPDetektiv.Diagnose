using ERPDetektiv.Contracts;

namespace ERPDetektiv.Core;

/// <summary>Fuehrt die Vorabpruefungen aus und fasst sie zusammen.</summary>
/// <remarks>
/// Der Vorabcheck beantwortet in wenigen Sekunden, ob ein vollstaendiger Lauf ueberhaupt
/// gelingen kann. Auf einem fremden Kundensystem ist das der Unterschied zwischen
/// "gleich klar" und "nach fuenf Minuten unvollstaendiges Paket".
/// </remarks>
public sealed class PreflightRunner(IEnumerable<IPreflightProbe> probes)
{
    public async Task<PreflightReport> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var results = new List<PreflightResult>();
        foreach (var probe in probes)
        {
            progress?.Report($"Vorabcheck: {probe.Id} …");
            try
            {
                results.AddRange(await probe.RunAsync(cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Eine fehlgeschlagene Pruefung ist ein Ergebnis, kein Programmabbruch.
                results.Add(new PreflightResult(probe.Id, probe.Id, PreflightState.Failed, exception.Message));
            }
        }

        return new PreflightReport(results);
    }
}

public sealed record PreflightReport(IReadOnlyList<PreflightResult> Results)
{
    public bool HasFailures => Results.Any(result => result.State == PreflightState.Failed);
    public bool HasWarnings => Results.Any(result => result.State == PreflightState.Warning);

    /// <summary>Kurzfassung fuer Statuszeile und Konsole.</summary>
    public string Summary()
    {
        var ok = Results.Count(result => result.State == PreflightState.Ok);
        var text = $"{ok} von {Results.Count} Prüfungen in Ordnung";
        if (HasFailures) return $"{text}; {Results.Count(r => r.State == PreflightState.Failed)} blockierend.";
        if (HasWarnings) return $"{text}; {Results.Count(r => r.State == PreflightState.Warning)} eingeschränkt.";
        return $"{text}. Ein vollständiger Lauf ist zu erwarten.";
    }
}

/// <summary>Prueft, ob das Diagnosepaket am gewaehlten Ort geschrieben werden kann.</summary>
/// <remarks>
/// Bewusst mit einer echten Testdatei statt einer Rechtepruefung: Freigaben,
/// Datentraegerkontingente und Virenscanner scheitern erst beim Schreiben.
/// </remarks>
public sealed class ExportPathProbe(string outputPath) : IPreflightProbe
{
    public string Id => "export-path";

    public Task<IReadOnlyList<PreflightResult>> RunAsync(CancellationToken cancellationToken)
    {
        const string name = "Exportpfad beschreibbar";
        try
        {
            var full = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory))
                return Result(new PreflightResult(Id, name, PreflightState.Failed,
                    $"Der Zielpfad {outputPath} enthält kein Verzeichnis."));
            Directory.CreateDirectory(directory);
            var probeFile = Path.Combine(directory, $".erpdetektiv-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probeFile, [0]);
            File.Delete(probeFile);
            var exists = File.Exists(full);
            return Result(new PreflightResult(Id, name,
                exists ? PreflightState.Warning : PreflightState.Ok,
                exists
                    ? $"{full} ist beschreibbar, die Datei existiert aber bereits und würde überschrieben."
                    : $"{full} ist beschreibbar."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return Result(new PreflightResult(Id, name, PreflightState.Failed,
                $"Der Zielpfad ist nicht beschreibbar: {exception.Message}"));
        }
    }

    private static Task<IReadOnlyList<PreflightResult>> Result(PreflightResult result) =>
        Task.FromResult<IReadOnlyList<PreflightResult>>([result]);
}
