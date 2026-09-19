namespace ERPDetektiv.Contracts;

public enum Severity
{
    Info,
    Warning,
    Critical
}

public enum CollectorState
{
    Succeeded,
    Partial,
    Skipped,
    Failed
}

public enum FeatureCollectionState
{
    NotCollected,
    Available,
    NotAvailable,
    NotSupported,
    AccessDenied,
    Failed
}

public sealed record Evidence(string Key, string Value);

public sealed record Finding(
    string Id,
    string Category,
    string Name,
    string Description,
    Severity Severity,
    string Recommendation,
    IReadOnlyList<Evidence> Evidence);

public sealed record CollectorStatus(
    string CollectorId,
    CollectorState State,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string? Message = null);

public sealed record DriveInfoSnapshot(string Name, long TotalBytes, long FreeBytes);

public sealed record ServiceSnapshot(string Name, string Status, string StartType);

public sealed record SystemSnapshot(
    string OperatingSystem,
    string HostName,
    string Architecture,
    int ProcessorCount,
    long? TotalMemoryBytes,
    string TimeZone,
    IReadOnlyList<DriveInfoSnapshot> Drives,
    IReadOnlyList<ServiceSnapshot> Services,
    string? PowerPlan = null,
    IReadOnlyList<string>? DotnetRuntimes = null,
    string? ProcessorName = null,
    string? PowerPlanGuid = null);

public sealed record DatabaseSnapshot(
    string Name,
    string Status,
    long SizeBytes,
    string RecoveryModel,
    int CompatibilityLevel,
    bool? QueryStoreEnabled,
    DateTimeOffset? LastBackupAt,
    IReadOnlyList<DatabaseFileSnapshot>? Files = null,
    int? QueryStoreStorageUsedMb = null,
    int? QueryStoreStorageMaxMb = null,
    long? LogSpaceUsedBytes = null,
    double? LogSpaceUsedPercent = null,
    FeatureCollectionState QueryStoreCollectionState = FeatureCollectionState.NotCollected,
    FeatureCollectionState LogSpaceCollectionState = FeatureCollectionState.NotCollected)
{
    /// <summary>Mittlere Lesezeit je Zugriff seit Instanzstart, in Millisekunden.</summary>
    /// <remarks>
    /// Aus <c>sys.dm_io_virtual_file_stats</c>; die Zaehler sind kumulativ, ein einzelner
    /// Snapshot liefert also den Lebenszeit-Durchschnitt. Anders als die reine Summe der
    /// Wartezeit ist dieser Wert mit einer Bezugsgroesse ausserhalb des Systems
    /// vergleichbar: unter 10 ms gut, ueber 20 ms auffaellig.
    /// </remarks>
    public double? ReadLatencyMs { get; init; }

    /// <summary>Mittlere Schreibzeit je Zugriff seit Instanzstart, in Millisekunden.</summary>
    public double? WriteLatencyMs { get; init; }

    /// <summary>Anzahl Lesezugriffe seit Instanzstart.</summary>
    /// <remarks>Bezugsgroesse fuer die Latenz: Bei wenigen Zugriffen ist der Mittelwert
    /// nicht aussagekraeftig.</remarks>
    public long? ReadCount { get; init; }

    /// <summary>Anzahl Schreibzugriffe seit Instanzstart.</summary>
    public long? WriteCount { get; init; }
}

public sealed record DatabaseFileSnapshot(
    string LogicalName,
    string Type,
    long SizeBytes,
    string GrowthMode,
    long GrowthValue);

/// <summary>TempDB-Datei. <paramref name="GrowthMode"/> unterscheidet "Percent" von
/// "FixedBytes"; ohne diese Angabe waere prozentuales Wachstum nicht von "kein
/// Wachstum" zu unterscheiden.</summary>
public sealed record TempDbFileSnapshot(string Name, long SizeBytes, string GrowthMode, long GrowthValue);

public sealed record SqlServerSnapshot(
    string Server,
    string? Version,
    string? Edition,
    DateTimeOffset? StartedAt,
    int? MaxServerMemoryMb,
    int? MinServerMemoryMb,
    int? MaxDop,
    int? CostThresholdForParallelism,
    IReadOnlyList<DatabaseSnapshot> Databases,
    IReadOnlyList<TempDbFileSnapshot> TempDbFiles,
    int? CpuCount = null,
    int? SocketCount = null,
    int? CoresPerSocket = null,
    string? VirtualMachineType = null,
    long? HostPhysicalMemoryBytes = null,
    long? HostAvailableMemoryBytes = null,
    bool? InstantFileInitializationEnabled = null,
    bool? AlwaysOnEnabled = null)
{
    /// <summary>Page Life Expectancy in Sekunden.</summary>
    /// <remarks>
    /// Nur zusammen mit <see cref="BufferPoolBytes"/> aussagekraeftig: Die alte
    /// Pauschalregel "unter 300" ist bei heutigen Speichergroessen wertlos, die
    /// skalierte Fassung 300 × (Bufferpool-GB / 4) nicht.
    /// </remarks>
    public long? PageLifeExpectancySeconds { get; init; }

    /// <summary>Groesse des Bufferpools in Byte; Bezugsgroesse fuer die Page Life Expectancy.</summary>
    public long? BufferPoolBytes { get; init; }

    /// <summary>Abfragen, die zum Erfassungszeitpunkt auf Arbeitsspeicher warten.</summary>
    /// <remarks>
    /// Nahezu binaer: Dauerhaft groesser als null heisst, Abfragen bekommen den
    /// angeforderten Speicher nicht. Belegter Arbeitsspeicher allein ist dagegen kein
    /// Marker - SQL Server nimmt sich, was max server memory erlaubt.
    /// </remarks>
    public int? MemoryGrantsPending { get; init; }
}

public sealed record Sage100Snapshot(
    string? InstallationPath,
    string? Version,
    bool ApplicationServerDetected,
    IReadOnlyList<string> Components,
    SageApplicationServerSnapshot? ApplicationServer = null)
{
    /// <summary>Konfiguration aus der Sage-Registry.</summary>
    /// <remarks>
    /// Getrennt vom Application-Server-Adapter, weil beide Quellen unabhaengig
    /// voneinander ausfallen: Der Adapter braucht die 32-Bit-Bibliotheken und einen
    /// antwortenden Dienst, die Registry nur Lesezugriff. Auf einem Satelliten liefert
    /// die Registry fast nichts, auf einem Server ohne Application Server der Adapter
    /// nichts – jeweils ohne den anderen Teil zu verhindern.
    /// </remarks>
    public SageRegistrySnapshot? Registry { get; init; }
}

public sealed record SageApplicationServerSnapshot(
    IReadOnlyList<SageApplicationServerSettingSnapshot> Settings,
    SageIsolationProcessSummary? IsolationProcesses,
    IReadOnlyList<SageServiceEndpointSnapshot> SDataEndpoints,
    IReadOnlyList<SageSoapServiceSnapshot> SoapServices)
{
    /// <summary>Service-Domains einzeln, mit Identitaet.</summary>
    /// <remarks>
    /// Die aggregierten Zaehler in <see cref="SageIsolationProcessSummary"/> sind nicht
    /// monoton: Der Pool erzeugt und verwirft Domains, wodurch die Summe faellt. Deltas
    /// lassen sich nur je Domain bilden.
    /// </remarks>
    public IReadOnlyList<SageServiceDomainSnapshot> ServiceDomains { get; init; } = [];
}

/// <summary>Eine Service-Domain des Sage Application Servers.</summary>
/// <remarks>
/// Bewusst nicht erfasst werden die ebenfalls verfuegbaren Felder <c>UserName</c>,
/// <c>MandantName</c>, <c>DatabaseName</c> und <c>RequestContext</c>. Letzteres enthaelt
/// die vollstaendige SData-Anfrage samt Mandant, Datenbank und Hostname – also genau die
/// Geschaeftsdaten, die nicht in ein Diagnosepaket gehoeren.
/// </remarks>
public sealed record SageServiceDomainSnapshot(
    string? Id,
    string State,
    bool IsActive,
    bool ForAsyncService,
    long ContextSwitches,
    long TotalCalls,
    long WorkingSetBytes,
    DateTimeOffset? CreatedAt,
    long? AgeSeconds)
{
    /// <summary>Vom Application Server gemeldete CPU-Last dieser Domain in Prozent.</summary>
    public int? ProcessorTimePercent { get; init; }

    /// <summary>Windows-Prozesskennung des Isolationsprozesses.</summary>
    public int? WindowsProcessId { get; init; }

    /// <summary>Technischer Dienstname, etwa <c>SDataService</c>.</summary>
    public string? ServiceName { get; init; }
}

public sealed record SageApplicationServerSettingSnapshot(string Section, string Name, string Value);

public sealed record SageIsolationProcessSummary(
    int ServiceDomainCount,
    int ActiveCount,
    int AsyncCount,
    int AppDomainIsolatedCount,
    long ContextSwitchesTotal,
    long TotalCalls,
    long WorkingSetBytes,
    DateTimeOffset? OldestProcessStartedAt,
    long? OldestProcessAgeSeconds,
    IReadOnlyList<SageIsolationProcessStateSnapshot> States);

public sealed record SageIsolationProcessStateSnapshot(string State, int Count);

public sealed record SageServiceEndpointSnapshot(string Binding, string Address)
{
    /// <summary>Authentifizierungsart laut Konfiguration, etwa <c>Basic</c> oder <c>Windows</c>.</summary>
    /// <remarks>
    /// Weicht auf allen bisher geprueften Systemen beim Binding <c>HttpsToken</c> ab: Dort
    /// steht <c>Basic</c>. Das ist eine Sage-Voreinstellung und keine Fehlkonfiguration –
    /// ohne diesen Wert waere der Unterschied zwischen Binding-Name und tatsaechlicher
    /// Anmeldung nicht sichtbar.
    /// </remarks>
    public string? Authentication { get; init; }
}

public sealed record SageSoapServiceSnapshot(
    string Name,
    string? Namespace,
    string? ModuleName,
    string? ContractName,
    IReadOnlyList<string> Operations);

/// <summary>Sage-Konfiguration aus <c>HKLM\SOFTWARE\WOW6432Node\Sage</c>.</summary>
/// <remarks>
/// Die Inventardaten stehen nur auf dem Sage-Server. Auf einem zusaetzlichen
/// Application Server fehlen Datenquellen, Server-Liste und Security-Zweig
/// vollstaendig; dort bleiben die Listen leer und <see cref="IsSageServer"/> ist
/// <c>false</c>. Ohne diese Unterscheidung waere ein Satellit von einem Server mit
/// leerer Konfiguration nicht zu trennen.
/// </remarks>
public sealed record SageRegistrySnapshot(
    string ProductVersion,
    string? SageServer,
    bool IsSageServer,
    IReadOnlyList<SageRegistryComponentSnapshot> Components,
    IReadOnlyList<SageRegistryApplicationServerSnapshot> ApplicationServers,
    IReadOnlyList<SageRegistryBlobStorageSnapshot> BlobStorageServers,
    IReadOnlyList<SageRegistryGatewaySnapshot> Gateways,
    IReadOnlyList<SageRegistryStationSnapshot> Stations,
    IReadOnlyList<SageRegistryDatasourceSnapshot> Datasources,
    IReadOnlyList<string> DatasourceKeyNames,
    IReadOnlyList<SageRegistryNamedUserSnapshot> NamedUsers,
    IReadOnlyList<SageRegistryValueSnapshot> Values);

/// <summary>Installiertes Sage-Produkt mit seinem Wurzelverzeichnis.</summary>
public sealed record SageRegistryComponentSnapshot(string Name, string? InstallationPath);

public sealed record SageRegistryApplicationServerSnapshot(
    string Host,
    string? State,
    int? Capability,
    IReadOnlyList<SageServiceEndpointSnapshot> SDataEndpoints)
{
    /// <summary>Master der Farm.</summary>
    /// <remarks>
    /// <c>Capability</c> 3 bedeutet Master und Teilnahme an der Lastverteilung, 1 nur
    /// Teilnahme, 0 Einzelbetrieb. Ein Server mit 0 ist kein Fehler: Diese Rolle wird
    /// fuer eigene SData-Schnittstellen genutzt, die bewusst nicht verteilt werden.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMaster => Capability == 3;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ParticipatesInLoadBalancing => Capability is 1 or 3;
}

public sealed record SageRegistryBlobStorageSnapshot(
    string Host,
    int? Status,
    bool Activated,
    IReadOnlyList<SageServiceEndpointSnapshot> Endpoints);

/// <summary>Application Gateway je Host. Der <c>ApiKey</c> ist bewusst nicht enthalten.</summary>
public sealed record SageRegistryGatewaySnapshot(string Host, string? State, string? Endpoint);

/// <summary>Station aus <c>Connectivity Server</c>; die einzige Quelle fuer Clients
/// und Terminalserver einer Installation.</summary>
public sealed record SageRegistryStationSnapshot(string Host, string? Endpoint, int? State);

/// <summary>Registrierte Datenquelle.</summary>
/// <remarks>
/// Der Name der Datenquelle ist nicht der Datenbankname – diese Abbildung existiert nur
/// hier. <paramref name="ServerVersion"/> ist die beim Anlegen oder Aktualisieren
/// hinterlegte SQL-Hauptversion mal zehn (150 = 15.x) und veraltet danach; sie ist
/// ausdruecklich kein Istwert. Von den Mandanten geht nur die Anzahl in den Export.
/// </remarks>
public sealed record SageRegistryDatasourceSnapshot(
    string Name,
    string? ServerName,
    string? DatabaseName,
    string? Applications,
    int? ServerVersion,
    int MandatorCount);

/// <summary>Belegte benannte Benutzer je Applikation.</summary>
/// <remarks>
/// Nur die Anzahl, nicht die Namen. Das lizenzierte Maximum steht nicht in der
/// Registry, sondern nur im Lizenz-Blob – der Wert ist damit eine Groessenangabe und
/// keine Auslastung.
/// </remarks>
public sealed record SageRegistryNamedUserSnapshot(string Application, int Count);

/// <summary>Einzelwert aus der Whitelist, mit seinem Pfad unterhalb des Versionsknotens.</summary>
public sealed record SageRegistryValueSnapshot(string Path, string Name, string Value);

/// <summary>Freiwillige Angaben der Anwenderin oder des Anwenders zum Fall.</summary>
/// <remarks>
/// Bewusst getrennt vom automatischen Collector: Diese Angaben sind Beobachtungen,
/// keine gemessenen Fakten. Sie sind fuer eine spaetere Expertenanalyse oft der
/// entscheidende Kontext ("seit dem Update am Montag", "nur in der Auftragserfassung"),
/// der sonst per Rueckfrage nachgereicht werden muss.
///
/// Der Text wird frei eingegeben. Er durchlaeuft dieselbe Secret-Filterung und
/// Pseudonymisierung wie der uebrige Export, ersetzt aber keine Sorgfalt beim
/// Formulieren – wer Kundennamen hineinschreibt, exportiert Kundennamen.
/// </remarks>
public sealed record CaseNotes(
    string? Symptom = null,
    string? ObservedSince = null,
    string? AffectedArea = null,
    string? RecentChanges = null,
    string? AdditionalNotes = null)
{
    /// <summary>Abgeleiteter Zustand; gehoert nicht in das Diagnoseformat.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Symptom) && string.IsNullOrWhiteSpace(ObservedSince) &&
                           string.IsNullOrWhiteSpace(AffectedArea) && string.IsNullOrWhiteSpace(RecentChanges) &&
                           string.IsNullOrWhiteSpace(AdditionalNotes);
}

public enum PreflightState
{
    Ok,
    Warning,
    Failed,
    Skipped
}

/// <summary>Ergebnis einer einzelnen Vorabpruefung.</summary>
public sealed record PreflightResult(string Id, string Name, PreflightState State, string Message);

/// <summary>Prueft vor der eigentlichen Diagnose, ob eine Datenquelle nutzbar ist.</summary>
public interface IPreflightProbe
{
    string Id { get; }
    Task<IReadOnlyList<PreflightResult>> RunAsync(CancellationToken cancellationToken);
}

public sealed record DiagnosticSnapshot(
    SystemSnapshot? System,
    SqlServerSnapshot? SqlServer,
    Sage100Snapshot? Sage100,
    IReadOnlyList<CollectorStatus> CollectionStatus,
    IReadOnlyList<Finding> Findings);

public sealed record CollectionResult<T>(T? Value, CollectorStatus Status)
{
    /// <summary>Status optionaler Teilbereiche eines Collectors.</summary>
    /// <remarks>
    /// Diese Eintraege landen unveraendert in <c>collection-status.json</c>. Ein
    /// Teilbereich, der nur als Text in der Sammelmeldung auftaucht, ist fuer eine
    /// spaetere Auswertung nicht nachvollziehbar.
    /// </remarks>
    public IReadOnlyList<CollectorStatus> AdditionalStatus { get; init; } = [];
}

public interface ICollector<T>
{
    string Id { get; }
    Task<CollectionResult<T>> CollectAsync(CancellationToken cancellationToken);
}

public interface ICheck
{
    IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot);
}
