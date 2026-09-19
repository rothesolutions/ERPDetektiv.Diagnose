using System.Xml.Linq;
using ERPDetektiv.Contracts;

namespace ERPDetektiv.Sage100;

public sealed class Sage100Collector(
    IEnumerable<string>? searchRoots = null,
    ISageApplicationServerReader? applicationServerReader = null,
    ISageRegistryReader? registryReader = null) : ICollector<Sage100Snapshot>
{
    /// <summary>Umgebungsvariable fuer Installationen ausserhalb der Standardpfade.</summary>
    /// <remarks>
    /// Seit die Registry als Quelle abgesichert ist, ist dies kein Regelweg mehr, sondern
    /// eine ausdrueckliche Uebersteuerung: Wer sie setzt, meint sie auch. Sie geht der
    /// Registry deshalb vor, waehrend die Standardpfade ihr nachgeordnet sind.
    /// </remarks>
    public const string SearchRootVariable = "ERPDETEKTIV_SAGE_ROOT";

    private readonly ISageApplicationServerReader _applicationServerReader =
        applicationServerReader ?? new SageApplicationServerReader();

    private readonly ISageRegistryReader _registryReader = registryReader ?? new SageRegistryReader();

    public string Id => "sage100";

    public async Task<CollectionResult<Sage100Snapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();

        // Die Registry steht vor der Verzeichnissuche: Sie nennt den Installationsort,
        // waehrend die Suche ihn errät. Gelesen wird sie unabhaengig vom Verzeichnisfund,
        // weil sie auch auf einem Satelliten ohne lokale Installation traegt.
        var registryResult = await _registryReader.CollectAsync(cancellationToken);
        var registry = registryResult.Value;

        // Reihenfolge: ausdrueckliche Uebersteuerung, dann Registry, dann Standardpfade.
        // Uebergebene Suchpfade grenzen die Suche vollstaendig ein – sonst liefe ein Test
        // oder ein Vorabcheck mit eigenem Pfad am Ende doch ueber "Programme".
        var explicitRoot = SelectSageRoot(searchRoots?.ToList() ?? ConfiguredSearchRoots());
        var fallbackRoots = searchRoots is null ? DefaultSearchRoots() : [];
        var installationPath = SelectInstallation(explicitRoot)
                               ?? ExistingComponentPath(registry, OfficeLineComponent)
                               ?? SelectInstallation(SelectSageRoot(fallbackRoots));
        var sageRoot = ParentOfInstallation(installationPath) ?? explicitRoot;
        var version = installationPath is null
            ? null
            : ReadBuildVersion(Path.Combine(installationPath, "Buildinfo.xml"));
        var components = MergeComponents(sageRoot, registry);
        var applicationServerPath = ExistingComponentPath(registry, ApplicationServerComponent)
                                    ?? DirectoryApplicationServerPath(sageRoot, installationPath);
        var applicationServerDetected = applicationServerPath is not null || components.Any(component =>
            string.Equals(component, ApplicationServerComponent, StringComparison.OrdinalIgnoreCase));

        CollectionResult<SageApplicationServerSnapshot>? applicationServerResult = null;
        if (applicationServerPath is not null)
            applicationServerResult =
                await _applicationServerReader.CollectAsync(applicationServerPath, cancellationToken);

        var snapshot = new Sage100Snapshot(installationPath, version, applicationServerDetected, components,
            applicationServerResult?.Value) { Registry = registry };
        var found = installationPath is not null;
        var message = found
            ? $"{DescribeVersion(version)} erkannt; {components.Count} Installationskomponenten erfasst.{(applicationServerDetected ? " Application Server erkannt." : string.Empty)}"
            : registry is not null
                ? $"Keine lokale Sage-100-Installation erkannt; die Registry meldet Version {registry.ProductVersion}{DescribeSageServer(registry)}."
                : $"Keine Sage-100-Installation erkannt – weder in der Registry noch in den durchsuchten Pfaden. Abweichende Installationen über {SearchRootVariable} angeben.";
        var state = !found
            ? registry is null ? CollectorState.Skipped : CollectorState.Partial
            : applicationServerResult?.Status.State is CollectorState.Failed ||
              registryResult.Status.State is CollectorState.Failed
                ? CollectorState.Partial
                : CollectorState.Succeeded;
        return new CollectionResult<Sage100Snapshot>(snapshot,
            new CollectorStatus(Id, state, started, DateTimeOffset.UtcNow, message))
        {
            // Application Server und Registry erhalten eigene Statuseintraege, statt nur als
            // Text in der Sammelmeldung zu erscheinen.
            AdditionalStatus = applicationServerResult is null
                ? [registryResult.Status]
                : [applicationServerResult.Status, registryResult.Status]
        };
    }

    /// <summary>Beschreibt die erkannte Version fuer Statusmeldungen.</summary>
    /// <remarks>
    /// Die FixVersion aus der Buildinfo.xml enthaelt den Produktnamen bereits
    /// ("Sage 100 (9.0.11.4)"); ein fester Praefix ergaebe "Sage 100 Sage 100 (…)".
    /// </remarks>
    public static string DescribeVersion(string? version) => version switch
    {
        null or "" => "Sage 100 ohne Buildinfo",
        _ when version.StartsWith("Sage", StringComparison.OrdinalIgnoreCase) => version,
        _ => $"Sage 100 {version}"
    };

    /// <summary>Name der Sage-100-Komponente in Registry und Verzeichnisstruktur.</summary>
    private const string OfficeLineComponent = "Office Line";

    private const string ApplicationServerComponent = "Application Server";

    /// <summary>Nur die ausdrueckliche Uebersteuerung; die Standardpfade folgen der Registry.</summary>
    private static List<string> ConfiguredSearchRoots()
    {
        var configured = Environment.GetEnvironmentVariable(SearchRootVariable);
        return string.IsNullOrWhiteSpace(configured) ? [] : [configured];
    }

    private static List<string> DefaultSearchRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
    ];

    private static string? SelectSageRoot(IEnumerable<string> roots) => roots
        .Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => Path.Combine(root, "Sage"))
        .FirstOrDefault(Directory.Exists);

    /// <summary>Verzeichnis eines in der Registry eingetragenen Produkts, sofern es existiert.</summary>
    /// <remarks>
    /// Die Existenzpruefung ist der Punkt: Ein Eintrag ueberlebt eine Deinstallation. Ohne
    /// sie meldete der Collector einen Pfad, den es nicht mehr gibt – schlechter als ein
    /// ehrliches "nicht erkannt".
    /// </remarks>
    private static string? ExistingComponentPath(SageRegistrySnapshot? registry, string component)
    {
        var path = registry?.Components
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, component, StringComparison.OrdinalIgnoreCase))
            ?.InstallationPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
    }

    /// <summary>Verzeichnis oberhalb der Installation, wenn sie aus der Registry stammt.</summary>
    private static string? ParentOfInstallation(string? installationPath) =>
        installationPath is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(installationPath));

    /// <summary>Application Server neben der Installation, wie ihn die Verzeichnissuche findet.</summary>
    private static string? DirectoryApplicationServerPath(string? sageRoot, string? installationPath)
    {
        if (sageRoot is null || installationPath is null) return null;
        var path = Path.Combine(sageRoot, ApplicationServerComponent, Path.GetFileName(installationPath));
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Vereinigt die Komponenten aus Registry und Verzeichnisstruktur.</summary>
    /// <remarks>
    /// Beide Quellen sind unvollstaendig, und zwar auf unterschiedliche Weise: Die
    /// Registry fuehrt nur Produkte mit eigenem Versionsknoten und dortigem <c>Root</c>,
    /// die Verzeichnisliste nur, was unterhalb desselben Sage-Ordners liegt. Die Liste ist
    /// eine Bestandsaufnahme des Umfelds, keine Bewertung – Zusatzprodukte von
    /// Business-Partnern erscheinen darin, werden aber nicht ausgewertet.
    /// </remarks>
    private static List<string> MergeComponents(string? sageRoot, SageRegistrySnapshot? registry)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in registry?.Components ?? []) names.Add(component.Name);
        if (sageRoot is not null && Directory.Exists(sageRoot))
            foreach (var directory in Directory.EnumerateDirectories(sageRoot))
            {
                var name = Path.GetFileName(directory);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }

        return [.. names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string DescribeSageServer(SageRegistrySnapshot registry) =>
        string.IsNullOrWhiteSpace(registry.SageServer)
            ? string.Empty
            : $"; Sage-Server ist {registry.SageServer.TrimStart('\\')}";

    /// <summary>Waehlt die hoechste installierte Version.</summary>
    /// <remarks>
    /// Zuvor entschied das Aenderungsdatum des Ordners. Das kippt, sobald eine aeltere
    /// Installation nachtraeglich angefasst wird – etwa durch ein Backup.
    /// </remarks>
    private static string? SelectInstallation(string? sageRoot)
    {
        if (sageRoot is null) return null;
        var sage100Root = Path.Combine(sageRoot, "Sage 100");
        if (!Directory.Exists(sage100Root)) return null;
        return Directory.EnumerateDirectories(sage100Root)
            .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
            .OrderByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => Directory.GetLastWriteTimeUtc(candidate.Path))
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static Version ParseVersion(string? folderName) =>
        Version.TryParse(folderName, out var version) ? version : new Version(0, 0);

    private static string? ReadBuildVersion(string buildInfoPath)
    {
        try
        {
            if (!File.Exists(buildInfoPath)) return null;
            var document = XDocument.Load(buildInfoPath);
            return document.Root?.Attribute("FixVersion")?.Value;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.Xml.XmlException) { return null; }
    }
}
