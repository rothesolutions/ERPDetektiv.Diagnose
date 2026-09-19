using ERPDetektiv.Contracts;

namespace ERPDetektiv.Checks;

/// <summary>Prueft, ob genau ein Application Server als Master eingetragen ist.</summary>
/// <remarks>
/// Bewertet wird nur auf dem Sage-Server: Nur dort steht die Liste aller Application
/// Server. Auf einem Satelliten fehlt sie vollstaendig – eine Bewertung waere dort eine
/// Aussage ueber Daten, die gar nicht vorliegen.
/// </remarks>
public sealed class SageMasterCountCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var registry = snapshot?.Sage100?.Registry;
        if (registry is null || !registry.IsSageServer) yield break;
        var servers = registry.ApplicationServers.Where(server => server.Capability is not null).ToList();
        // Ohne gelesenes Capability-Flag gibt es nichts zu zaehlen; das ist kein Befund.
        if (servers.Count == 0) yield break;

        var masters = servers.Where(server => server.IsMaster).ToList();
        if (masters.Count == 1) yield break;

        var evidence = servers
            .Select(server => new Evidence(server.Host, DescribeCapability(server.Capability)))
            .ToList();
        yield return masters.Count == 0
            ? new Finding("sage.registry.master.missing", "Sage 100", "Kein Application Server als Master eingetragen",
                $"Von {servers.Count} eingetragenen Application Server(n) ist keiner mit Capability 3 als Master gekennzeichnet.",
                Severity.Warning,
                "Rollenverteilung im Sage-Server-Manager prüfen; ohne Master koordiniert niemand die Lastverteilung.",
                evidence)
            : new Finding("sage.registry.master.ambiguous", "Sage 100", "Mehrere Application Server als Master eingetragen",
                $"{masters.Count} Application Server sind mit Capability 3 als Master gekennzeichnet.",
                Severity.Warning,
                "Rollenverteilung im Sage-Server-Manager prüfen; vorgesehen ist genau ein Master.",
                evidence);
    }

    /// <summary>Rollenklartext des Capability-Flags.</summary>
    internal static string DescribeCapability(int? capability) => capability switch
    {
        3 => "3 (Master, nimmt an Lastverteilung teil)",
        1 => "1 (nimmt an Lastverteilung teil)",
        0 => "0 (Einzelbetrieb ohne Lastverteilung)",
        null => "nicht gesetzt",
        _ => $"{capability} (unbekannt)"
    };
}

/// <summary>Gleicht registrierte Datenquellen gegen ihre Schluessel ab.</summary>
/// <remarks>
/// Beide Richtungen sind unterschiedlich schwer: Ein Schluessel ohne Datenquelle ist der
/// Rest einer entfernten Registrierung, eine Datenquelle ohne Schluessel dagegen nicht
/// benutzbar. <c>OLGlobal</c> ist ausgenommen – die Systemdatenquelle fuehrt auf keinem
/// der geprueften Systeme einen Schluessel.
/// </remarks>
public sealed class SageDatasourceKeyConsistencyCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var registry = snapshot?.Sage100?.Registry;
        if (registry is null || !registry.IsSageServer) yield break;
        if (registry.Datasources.Count == 0 || registry.DatasourceKeyNames.Count == 0) yield break;

        var datasources = registry.Datasources.Where(datasource => !IsSystemDatasource(datasource)).ToList();
        var names = datasources.Select(datasource => datasource.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = registry.DatasourceKeyNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphanedKeys = keys.Where(key => !names.Contains(key)).OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (orphanedKeys.Count > 0)
            yield return new Finding("sage.registry.datasource-key.orphaned", "Sage 100",
                "Schlüssel ohne zugehörige Datenquelle",
                $"{orphanedKeys.Count} Datenquellenschlüssel haben keine registrierte Datenquelle mehr: {string.Join(", ", orphanedKeys)}.",
                Severity.Info,
                "Rest entfernter Datenquellen. Prüfen, ob die zugehörigen Datenbanken noch vorhanden sind und weiter benötigt werden.",
                [.. orphanedKeys.Select(key => new Evidence("schlüssel", key))]);

        var withoutKey = datasources.Where(datasource => !keys.Contains(datasource.Name))
            .OrderBy(datasource => datasource.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (withoutKey.Count > 0)
            yield return new Finding("sage.registry.datasource.without-key", "Sage 100",
                "Datenquelle ohne Schlüssel",
                $"{withoutKey.Count} registrierte Datenquelle(n) haben keinen Schlüssel: {string.Join(", ", withoutKey.Select(datasource => datasource.Name))}.",
                Severity.Warning,
                "Datenquelle im Sage-Administrator erneut eintragen; ohne Schlüssel ist keine Anmeldung möglich.",
                [.. withoutKey.Select(datasource => new Evidence("datenquelle", datasource.Name))]);
    }

    /// <summary>OLGlobal fuehrt planmaessig keinen Datenquellenschluessel.</summary>
    private static bool IsSystemDatasource(SageRegistryDatasourceSnapshot datasource) =>
        string.Equals(datasource.Applications, "OLGlobal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Findet Blobstorage-Registrierungen ohne installiertes Produkt und ohne Dienst.</summary>
/// <remarks>
/// Bewertet wird ausschliesslich der eigene Host: Fuer fremde Server liegen weder
/// Installationsverzeichnis noch Dienstliste vor. Die Dienstliste muss gefuellt sein –
/// ist die Windows-Inventur fehlgeschlagen, waere jede Registrierung faelschlich
/// auffaellig.
/// </remarks>
public sealed class SageBlobStorageRegistrationCheck : ICheck
{
    private const string BlobStorage = "BlobStorage";

    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var registry = snapshot?.Sage100?.Registry;
        var services = snapshot?.System?.Services ?? [];
        var hostName = snapshot?.System?.HostName;
        if (registry is null || services.Count == 0 || string.IsNullOrWhiteSpace(hostName)) yield break;
        if (HasBlobStorage(registry.Components) || HasBlobStorage(services)) yield break;

        foreach (var registration in registry.BlobStorageServers.Where(candidate =>
                     candidate.Activated && string.Equals(candidate.Host, hostName, StringComparison.OrdinalIgnoreCase)))
            yield return new Finding("sage.registry.blobstorage.without-service", "Sage 100",
                "Blobstorage-Registrierung ohne installiertes Produkt",
                $"Für {registration.Host} ist ein Blobstorage-Server mit {registration.Endpoints.Count} Endpunkt(en) als aktiviert eingetragen, aber weder ein Installationsverzeichnis noch ein Dienst vorhanden.",
                Severity.Warning,
                "Prüfen, ob die Registrierung ein Rest einer entfernten Installation ist. Clients, die auf diese Endpunkte zeigen, erreichen niemanden.",
                [
                    new Evidence("host", registration.Host),
                    .. registration.Endpoints.Select(endpoint => new Evidence("endpunkt", endpoint.Address))
                ]);
    }

    private static bool HasBlobStorage(IEnumerable<SageRegistryComponentSnapshot> components) =>
        components.Any(component => component.Name.Contains(BlobStorage, StringComparison.OrdinalIgnoreCase));

    private static bool HasBlobStorage(IEnumerable<ServiceSnapshot> services) =>
        services.Any(service => service.Name.Contains(BlobStorage, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Findet Datenquellen, deren hinterlegte SQL-Version von ihren Geschwistern abweicht.</summary>
/// <remarks>
/// <c>ServerVersion</c> wird beim Anlegen oder Aktualisieren einer Datenquelle
/// geschrieben und veraltet danach. Die Bezugsgroesse liegt deshalb im Paket selbst:
/// Auf derselben Instanz sollten alle Datenquellen dieselbe Version tragen. Ein
/// Vergleich gegen die tatsaechliche SQL-Version waere kein Ersatz – er wuerde auf allen
/// Datenquellen gleichzeitig anschlagen, sobald der Server angehoben wurde.
/// </remarks>
public sealed class SageDatasourceServerVersionCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var registry = snapshot?.Sage100?.Registry;
        if (registry is null) yield break;

        foreach (var group in registry.Datasources
                     .Where(datasource => datasource.ServerName is not null && datasource.ServerVersion is not null)
                     .GroupBy(datasource => datasource.ServerName!, StringComparer.OrdinalIgnoreCase))
        {
            var newest = group.Max(datasource => datasource.ServerVersion!.Value);
            var outdated = group.Where(datasource => datasource.ServerVersion!.Value < newest)
                .OrderBy(datasource => datasource.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (outdated.Count == 0) continue;

            yield return new Finding("sage.registry.datasource.stale-server-version", "Sage 100",
                "Datenquelle mit abweichender SQL-Version hinterlegt",
                $"Auf {group.Key} tragen {outdated.Count} von {group.Count()} Datenquellen eine ältere SQL-Version als die übrigen ({DescribeVersion(newest)}).",
                Severity.Info,
                "Der Wert stammt vom letzten Anlegen oder Aktualisieren der Datenquelle. Prüfen, ob die betroffenen Datenquellen noch benötigt werden.",
                [
                    new Evidence("server", group.Key),
                    .. outdated.Select(datasource =>
                        new Evidence(datasource.Name, DescribeVersion(datasource.ServerVersion!.Value)))
                ]);
        }
    }

    /// <summary>Sage speichert die SQL-Hauptversion mal zehn.</summary>
    internal static string DescribeVersion(int serverVersion) =>
        serverVersion % 10 == 0
            ? $"{SnapshotFilters.Invariant(serverVersion)} (SQL {SnapshotFilters.Invariant(serverVersion / 10)}.x)"
            : SnapshotFilters.Invariant(serverVersion);
}

/// <summary>Meldet einen 32-Bit-Client ohne erweiterten Adressraum.</summary>
/// <remarks>
/// <c>LargeAddressAwareClient</c> hebt den nutzbaren Adressraum des 32-Bit-Clients von
/// 2 auf 4 GB. Gemeldet wird nur der auffaellige Zustand – nicht gesetzt oder 0 –, weil
/// der gesetzte Normalfall keine Nachricht wert ist. Der Wert selbst steht unabhaengig
/// davon im Paket.
/// </remarks>
public sealed class SageLargeAddressAwareClientCheck : ICheck
{
    internal const string ValueName = "LargeAddressAwareClient";

    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var registry = snapshot?.Sage100?.Registry;
        if (registry is null) yield break;
        var value = registry.Values.FirstOrDefault(entry =>
            string.Equals(entry.Name, ValueName, StringComparison.OrdinalIgnoreCase));
        if (value is not null && !IsDisabled(value.Value)) yield break;

        yield return new Finding("sage.registry.large-address-aware.inactive", "Sage 100",
            "Erweiterter Adressraum des Clients nicht aktiv",
            value is null
                ? $"{ValueName} ist nicht gesetzt; der 32-Bit-Client bleibt auf 2 GB Adressraum begrenzt."
                : $"{ValueName} steht auf {value.Value}; der 32-Bit-Client bleibt auf 2 GB Adressraum begrenzt.",
            Severity.Info,
            "Bei Speicherfehlern im Client als Erstes prüfen. Die Einstellung ist ein Marker, keine pauschale Empfehlung.",
            [new Evidence(ValueName, value?.Value ?? "nicht gesetzt")]);
    }

    private static bool IsDisabled(string value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "0", StringComparison.Ordinal);
}
