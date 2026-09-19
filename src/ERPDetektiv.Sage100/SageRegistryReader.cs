using System.Globalization;
using System.Text.Json;
using ERPDetektiv.Contracts;
using ERPDetektiv.Platform;

namespace ERPDetektiv.Sage100;

public interface ISageRegistryReader
{
    Task<CollectionResult<SageRegistrySnapshot>> CollectAsync(CancellationToken cancellationToken);
}

public sealed record SageRegistryCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool TimedOut { get; init; }
}

public interface ISageRegistryCommandRunner
{
    Task<SageRegistryCommandResult> ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>Liest die Sage-Konfiguration aus der Registry.</summary>
/// <remarks>
/// Zweite, unabhaengige Quelle neben <see cref="SageApplicationServerReader"/>: Sie
/// braucht weder die 32-Bit-Administrationsbibliotheken noch einen antwortenden Dienst
/// und liefert dafuer die Farm-Sicht, die der lokale Adapter nicht hat.
/// </remarks>
public sealed class SageRegistryReader(ISageRegistryCommandRunner? commandRunner = null) : ISageRegistryReader
{
    private readonly ISageRegistryCommandRunner _commandRunner =
        commandRunner ?? new PowerShellSageRegistryCommandRunner();

    public const string Id = "sage100.registry";

    public async Task<CollectionResult<SageRegistrySnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows())
            return Result(null, CollectorState.Skipped, started, "Die Sage-Registry ist nur unter Windows lesbar.");

        try
        {
            var execution = await _commandRunner.ExecuteAsync(cancellationToken);
            if (execution.TimedOut)
                return Result(null, CollectorState.Failed, started,
                    "Die Registry-Abfrage wurde nach Ablauf der Zeitgrenze abgebrochen.");
            if (execution.ExitCode != 0)
                return Result(null, CollectorState.Failed, started,
                    $"Registry-Abfrage fehlgeschlagen: {FirstLine(execution)}");

            var payload = JsonSerializer.Deserialize<Payload>(execution.StandardOutput, JsonOptions);
            if (payload is null)
                return Result(null, CollectorState.Failed, started,
                    "Die Registry-Abfrage lieferte keine lesbaren Daten.");
            if (!payload.Found)
                return Result(null, CollectorState.Skipped, started,
                    "Unter HKLM\\SOFTWARE\\WOW6432Node\\Sage ist keine Sage-100-Version eingetragen.");

            var snapshot = Map(payload);
            return Result(snapshot, snapshot.IsSageServer ? CollectorState.Succeeded : CollectorState.Partial, started,
                Describe(snapshot));
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException exception)
        {
            return Result(null, CollectorState.Failed, started,
                $"Die Registry-Abfrage lieferte keine lesbaren Daten: {exception.Message}");
        }
        catch (Exception exception)
        {
            return Result(null, CollectorState.Failed, started,
                $"Registry-Abfrage fehlgeschlagen: {exception.Message}");
        }
    }

    /// <summary>Beschreibt den Umfang der Erfassung.</summary>
    /// <remarks>
    /// Auf einem zusaetzlichen Application Server fehlen Datenquellen und Server-Liste
    /// vollstaendig. Ohne den Hinweis auf den Sage-Server waere das von einer leeren
    /// Installation nicht zu unterscheiden – der Status wuerde eine Luecke melden, die
    /// keine ist.
    /// </remarks>
    internal static string Describe(SageRegistrySnapshot snapshot) => snapshot.IsSageServer
        ? $"Sage {snapshot.ProductVersion}: {snapshot.ApplicationServers.Count} Application Server, {snapshot.Datasources.Count} Datenquellen, {snapshot.Stations.Count} Stationen erfasst."
        : $"Sage {snapshot.ProductVersion}: Dieses System ist nicht der Sage-Server{DescribeServer(snapshot.SageServer)}. Datenquellen, Server-Liste und Sicherheitseinstellungen stehen nur dort.";

    private static string DescribeServer(string? server) =>
        string.IsNullOrWhiteSpace(server) ? string.Empty : $" ({server.TrimStart('\\')})";

    private static string FirstLine(SageRegistryCommandResult execution) =>
        new ProcessResult(execution.ExitCode, execution.StandardOutput, execution.StandardError, execution.TimedOut)
            .FirstErrorLine("Unbekannter Fehler");

    private static CollectionResult<SageRegistrySnapshot> Result(SageRegistrySnapshot? value, CollectorState state,
        DateTimeOffset started, string message) =>
        new(value, new CollectorStatus(Id, state, started, DateTimeOffset.UtcNow, message));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static SageRegistrySnapshot Map(Payload payload) => new(
        payload.ProductVersion ?? "unbekannt",
        Empty(payload.SageServer),
        payload.IsSageServer,
        [
            .. (payload.Components ?? []).Select(component =>
                new SageRegistryComponentSnapshot(component.Name ?? "unbekannt", Empty(component.InstallationPath)))
        ],
        [
            .. (payload.ApplicationServers ?? []).Select(server => new SageRegistryApplicationServerSnapshot(
                server.Host ?? "unbekannt", Empty(server.State), ParseCapability(server.Capability),
                Endpoints(server.SDataEndpoints)))
        ],
        [
            .. (payload.BlobStorageServers ?? []).Select(server => new SageRegistryBlobStorageSnapshot(
                server.Host ?? "unbekannt", server.Status, server.Activated is not (null or 0),
                Endpoints(server.Endpoints)))
        ],
        [
            .. (payload.Gateways ?? []).Select(gateway =>
                new SageRegistryGatewaySnapshot(gateway.Host ?? "unbekannt", Empty(gateway.State),
                    Empty(gateway.Endpoint)))
        ],
        [
            .. (payload.Stations ?? []).Select(station =>
                new SageRegistryStationSnapshot(station.Host ?? "unbekannt", Empty(station.Endpoint), station.State))
        ],
        [
            .. (payload.Datasources ?? []).Select(datasource => new SageRegistryDatasourceSnapshot(
                datasource.Name ?? "unbekannt", Empty(datasource.ServerName), Empty(datasource.DatabaseName),
                Empty(datasource.Applications), datasource.ServerVersion, datasource.MandatorCount))
        ],
        [.. payload.DatasourceKeyNames ?? []],
        [
            .. (payload.NamedUsers ?? []).Select(entry =>
                new SageRegistryNamedUserSnapshot(entry.Application ?? "unbekannt", entry.Count))
        ],
        [
            .. (payload.Values ?? []).Select(value => new SageRegistryValueSnapshot(value.Path ?? string.Empty,
                value.Name ?? "unbekannt", value.Value ?? string.Empty))
        ]);

    private static IReadOnlyList<SageServiceEndpointSnapshot> Endpoints(IReadOnlyList<EndpointWire>? endpoints) =>
    [
        .. (endpoints ?? []).Select(endpoint =>
            new SageServiceEndpointSnapshot(Empty(endpoint.Binding) ?? "unbekannt", endpoint.Address ?? string.Empty)
            {
                Authentication = Empty(endpoint.Authentication)
            })
    ];

    /// <summary>Das Capability-Flag steht als Zeichenkette in der Registry, nicht als Zahl.</summary>
    private static int? ParseCapability(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal sealed record Payload(
        bool Found,
        string? ProductVersion,
        string? SageServer,
        bool IsSageServer,
        IReadOnlyList<ComponentWire>? Components,
        IReadOnlyList<ApplicationServerWire>? ApplicationServers,
        IReadOnlyList<BlobStorageWire>? BlobStorageServers,
        IReadOnlyList<GatewayWire>? Gateways,
        IReadOnlyList<StationWire>? Stations,
        IReadOnlyList<DatasourceWire>? Datasources,
        IReadOnlyList<string>? DatasourceKeyNames,
        IReadOnlyList<NamedUserWire>? NamedUsers,
        IReadOnlyList<ValueWire>? Values);

    internal sealed record ComponentWire(string? Name, string? InstallationPath);

    internal sealed record ApplicationServerWire(
        string? Host,
        string? State,
        string? Capability,
        IReadOnlyList<EndpointWire>? SDataEndpoints);

    internal sealed record BlobStorageWire(
        string? Host,
        int? Status,
        int? Activated,
        IReadOnlyList<EndpointWire>? Endpoints);

    internal sealed record EndpointWire(string? Binding, string? Address, string? Authentication);

    internal sealed record GatewayWire(string? Host, string? State, string? Endpoint);

    internal sealed record StationWire(string? Host, string? Endpoint, int? State);

    internal sealed record DatasourceWire(
        string? Name,
        string? ServerName,
        string? DatabaseName,
        string? Applications,
        int? ServerVersion,
        int MandatorCount);

    internal sealed record NamedUserWire(string? Application, int Count);

    internal sealed record ValueWire(string? Path, string? Name, string? Value);
}
