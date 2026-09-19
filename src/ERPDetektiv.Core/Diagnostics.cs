using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ERPDetektiv.Contracts;

namespace ERPDetektiv.Core;

public sealed record ExportOptions(bool PseudonymizeIdentifiers = false);

public sealed record DiagnosticManifest(
    string FormatVersion,
    string ToolVersion,
    DateTimeOffset CreatedAt,
    string Profile,
    bool Pseudonymized,
    IReadOnlyDictionary<string, string> Files);

public sealed class DiagnosticRunner(
    ICollector<SystemSnapshot>? systemCollector,
    ICollector<SqlServerSnapshot>? sqlCollector,
    ICollector<Sage100Snapshot>? sageCollector,
    IEnumerable<ICheck> checks)
{
    public Task<DiagnosticSnapshot> RunAsync(CancellationToken cancellationToken) =>
        RunAsync(null, cancellationToken);

    public async Task<DiagnosticSnapshot> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var statuses = new List<CollectorStatus>();
        SystemSnapshot? system = null;
        SqlServerSnapshot? sql = null;
        Sage100Snapshot? sage = null;
        if (systemCollector is not null)
        {
            progress?.Report("Windows-Systemdaten werden erfasst …");
            system = await Collect(systemCollector, statuses, cancellationToken);
        }

        if (sqlCollector is not null)
        {
            progress?.Report("SQL-Server-Daten werden erfasst …");
            sql = await Collect(sqlCollector, statuses, cancellationToken);
        }

        if (sageCollector is not null)
        {
            progress?.Report("Sage-100-Umgebung wird erfasst …");
            sage = await Collect(sageCollector, statuses, cancellationToken);
        }

        progress?.Report("Checks werden ausgewertet …");
        var raw = new DiagnosticSnapshot(system, sql, sage, statuses, []);
        return raw with { Findings = checks.SelectMany(check => check.Evaluate(raw)).ToList() };
    }

    private static async Task<T?> Collect<T>(ICollector<T> collector, ICollection<CollectorStatus> statuses,
        CancellationToken token)
    {
        try
        {
            var result = await collector.CollectAsync(token);
            statuses.Add(result.Status);
            // Teilbereiche eines Collectors erscheinen als eigener Status, damit
            // collection-status.json nachvollziehbar bleibt.
            foreach (var additional in result.AdditionalStatus) statuses.Add(additional);
            return result.Value;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var now = DateTimeOffset.UtcNow;
            statuses.Add(new CollectorStatus(collector.Id, CollectorState.Failed, now, now, exception.Message));
            return default;
        }
    }
}

public static class DiagnosticExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() },
        // Deutsche Texte bleiben lesbar. Das Paket soll geprueft werden koennen, bevor es
        // weitergegeben wird, und "ä" arbeitet dagegen. Die JSON-Dateien werden
        // nirgends in HTML eingebettet; der Bericht kodiert separat.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Version des laufenden Werkzeugs, aus dem Assembly gelesen.</summary>
    public static string ToolVersion { get; } =
        typeof(DiagnosticExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(DiagnosticExporter).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static void Export(DiagnosticSnapshot snapshot, string zipPath, ExportOptions options,
        string? toolVersion = null, CaseNotes? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var safe = Redactor.Redact(snapshot, options.PseudonymizeIdentifiers);
        var safeNotes = notes is null || notes.IsEmpty
            ? null
            : Redactor.RedactNotes(notes, snapshot, options.PseudonymizeIdentifiers);
        var entries = new Dictionary<string, byte[]>
        {
            ["system.json"] = Serialize(safe.System),
            ["sqlserver.json"] = Serialize(safe.SqlServer),
            ["sage100.json"] = Serialize(safe.Sage100),
            ["findings.json"] = Serialize(safe.Findings),
            ["collection-status.json"] = Serialize(safe.CollectionStatus),
            ["summary.html"] = Encoding.UTF8.GetBytes(HtmlSummary.Create(safe, safeNotes))
        };
        // notes.json entsteht nur, wenn wirklich etwas eingetragen wurde. Eine leere Datei
        // im Paket suggeriert, es sei nach Kontext gefragt und nichts geliefert worden.
        if (safeNotes is not null) entries["notes.json"] = Serialize(safeNotes);
        var checksums = entries.ToDictionary(pair => pair.Key,
            pair => Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant());
        // Exportprofile (Basis/Erweitert) sind noch nicht umgesetzt. Das Manifest weist
        // deshalb bewusst nur "base" aus, statt ein Profil zu behaupten, das den Inhalt
        // nicht beschreibt.
        var manifest = new DiagnosticManifest("1.0", toolVersion ?? ToolVersion, DateTimeOffset.UtcNow, "base",
            options.PseudonymizeIdentifiers, checksums);
        entries["manifest.json"] = Serialize(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            stream.Write(content);
        }
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
}

public static class Redactor
{
    private static readonly Regex SecretPattern = new(
        @"(?i)\b(password|pwd|client[_-]?secret|access[_-]?token|api[_-]?key)\s*[=:]\s*[^;,\s<""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Entfernt Secrets und ersetzt bei Bedarf geschuetzte Bezeichner im gesamten Snapshot.</summary>
    /// <remarks>
    /// Die Ersetzung laeuft ueber <see cref="IdentifierRegistry"/> und damit auch durch
    /// Finding-Freitext und Evidence. Eine Feldliste war hier zuvor unvollstaendig: Ein
    /// Check, der eine Endpunktadresse als Evidence weiterreichte, veroeffentlichte den
    /// Hostnamen im Klartext, obwohl derselbe Wert im Snapshot ersetzt wurde.
    /// </remarks>
    public static DiagnosticSnapshot Redact(DiagnosticSnapshot snapshot, bool pseudonymize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var registry = pseudonymize ? IdentifierRegistry.FromSnapshot(snapshot) : null;
        string T(string value) => registry is null ? Scrub(value) : registry.Apply(Scrub(value));
        string? TN(string? value) => value is null ? null : T(value);

        var system = snapshot.System is null
            ? null
            : snapshot.System with
            {
                HostName = T(snapshot.System.HostName),
                OperatingSystem = T(snapshot.System.OperatingSystem),
                Services = snapshot.System.Services.Select(service => service with { Name = T(service.Name) }).ToList(),
                PowerPlan = TN(snapshot.System.PowerPlan)
            };

        var sql = snapshot.SqlServer is null
            ? null
            : snapshot.SqlServer with
            {
                Server = T(snapshot.SqlServer.Server),
                Databases = snapshot.SqlServer.Databases.Select(database => database with { Name = T(database.Name) })
                    .ToList()
            };

        var sage = snapshot.Sage100 is null
            ? null
            : snapshot.Sage100 with
            {
                InstallationPath = TN(snapshot.Sage100.InstallationPath),
                ApplicationServer = RedactApplicationServer(snapshot.Sage100.ApplicationServer, T),
                Registry = RedactSageRegistry(snapshot.Sage100.Registry, T, TN)
            };

        return snapshot with
        {
            System = system,
            SqlServer = sql,
            Sage100 = sage,
            Findings = snapshot.Findings.Select(finding => finding with
            {
                Name = T(finding.Name),
                Description = T(finding.Description),
                Recommendation = T(finding.Recommendation),
                Evidence = finding.Evidence.Select(evidence =>
                    evidence with { Key = T(evidence.Key), Value = T(evidence.Value) }).ToList()
            }).ToList(),
            CollectionStatus = snapshot.CollectionStatus
                .Select(status => status with { Message = TN(status.Message) }).ToList()
        };
    }

    /// <summary>Wendet Secret-Filter und Pseudonymisierung auf freie Fallnotizen an.</summary>
    /// <remarks>
    /// Die Bezeichner stammen aus dem Snapshot, nicht aus den Notizen: Schreibt jemand den
    /// Servernamen in das Symptomfeld, wird er mit derselben Kennung ersetzt wie im
    /// uebrigen Paket.
    /// </remarks>
    public static CaseNotes RedactNotes(CaseNotes notes, DiagnosticSnapshot snapshot, bool pseudonymize)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(snapshot);
        var registry = pseudonymize ? IdentifierRegistry.FromSnapshot(snapshot) : null;
        string? T(string? value) => value is null
            ? null
            : registry is null
                ? Scrub(value)
                : registry.Apply(Scrub(value));
        return new CaseNotes(T(notes.Symptom), T(notes.ObservedSince), T(notes.AffectedArea),
            T(notes.RecentChanges), T(notes.AdditionalNotes));
    }

    private static SageApplicationServerSnapshot? RedactApplicationServer(SageApplicationServerSnapshot? snapshot,
        Func<string, string> transform) => snapshot is null
        ? null
        : snapshot with
        {
            Settings = snapshot.Settings.Select(setting => setting with { Value = transform(setting.Value) }).ToList(),
            SDataEndpoints = snapshot.SDataEndpoints
                .Select(endpoint => endpoint with { Address = transform(endpoint.Address) }).ToList(),
            SoapServices = snapshot.SoapServices.Select(service => service with
            {
                Name = transform(service.Name),
                Namespace = service.Namespace is null ? null : transform(service.Namespace)
            }).ToList()
        };

    /// <summary>Ersetzt Servernamen in der Registry-Aufnahme.</summary>
    /// <remarks>
    /// Datenquellen- und Datenbanknamen bleiben lesbar – sie gehoeren zur bewusst nicht
    /// pseudonymisierten Kategorie. Ersetzt werden Hostnamen, Endpunktadressen und der
    /// Serveranteil einer Datenquelle; ausserdem die Werte der Whitelist, weil dort mit
    /// <c>CentralAddInDirectory</c> ein UNC-Pfad samt Servername stehen kann.
    /// </remarks>
    private static SageRegistrySnapshot? RedactSageRegistry(SageRegistrySnapshot? snapshot,
        Func<string, string> transform, Func<string?, string?> transformOptional) => snapshot is null
        ? null
        : snapshot with
        {
            SageServer = transformOptional(snapshot.SageServer),
            Components = [.. snapshot.Components.Select(component => component with
            {
                InstallationPath = transformOptional(component.InstallationPath)
            })],
            ApplicationServers = [.. snapshot.ApplicationServers.Select(server => server with
            {
                Host = transform(server.Host),
                SDataEndpoints = RedactEndpoints(server.SDataEndpoints, transform)
            })],
            BlobStorageServers = [.. snapshot.BlobStorageServers.Select(server => server with
            {
                Host = transform(server.Host), Endpoints = RedactEndpoints(server.Endpoints, transform)
            })],
            Gateways = [.. snapshot.Gateways.Select(gateway => gateway with
            {
                Host = transform(gateway.Host), Endpoint = transformOptional(gateway.Endpoint)
            })],
            Stations = [.. snapshot.Stations.Select(station => station with
            {
                Host = transform(station.Host), Endpoint = transformOptional(station.Endpoint)
            })],
            Datasources = [.. snapshot.Datasources.Select(datasource => datasource with
            {
                ServerName = transformOptional(datasource.ServerName)
            })],
            Values = [.. snapshot.Values.Select(value => value with { Value = transform(value.Value) })]
        };

    private static IReadOnlyList<SageServiceEndpointSnapshot> RedactEndpoints(
        IReadOnlyList<SageServiceEndpointSnapshot> endpoints, Func<string, string> transform) =>
        [.. endpoints.Select(endpoint => endpoint with { Address = transform(endpoint.Address) })];

    private static string Scrub(string value) => SecretPattern.Replace(value, "$1=[REDACTED]");
}
