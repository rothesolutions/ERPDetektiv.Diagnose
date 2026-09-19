using System.Security.Cryptography;
using System.Text;
using ERPDetektiv.Contracts;

namespace ERPDetektiv.Core;

/// <summary>
/// Kennt die Bezeichner, die bei aktivierter Pseudonymisierung ersetzt werden.
/// </summary>
/// <remarks>
/// Bewusst eng gefasst: Geschuetzt sind Host- und Servernamen. Datenbank-, Dienst-,
/// Komponenten- und Laufwerksnamen bleiben lesbar, weil sie das technische Umfeld
/// erkennbar machen – ein Datenbankname wie <c>DWData</c> ist ein Diagnosehinweis,
/// kein Datenleck. Die Festlegung steht in docs/collected-data-and-permissions.md.
///
/// Die Registry ersetzt einen registrierten Bezeichner ueberall, auch in Freitext
/// und in Evidence-Werten. Eine Feldliste im Redactor waere sonst genau dort
/// unvollstaendig, wo ein neuer Check einen Wert weiterreicht.
/// </remarks>
public sealed class IdentifierRegistry
{
    private readonly List<string> _protectedValues = [];

    /// <summary>Sammelt die geschuetzten Bezeichner eines Snapshots.</summary>
    public static IdentifierRegistry FromSnapshot(DiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var registry = new IdentifierRegistry();
        registry.Register(snapshot.System?.HostName);
        registry.RegisterServer(snapshot.SqlServer?.Server);
        foreach (var endpoint in snapshot.Sage100?.ApplicationServer?.SDataEndpoints ?? [])
            registry.RegisterEndpointHost(endpoint.Address);
        RegisterSageRegistry(registry, snapshot.Sage100?.Registry);
        return registry;
    }

    /// <summary>Sammelt die Servernamen aus der Sage-Registry.</summary>
    /// <remarks>
    /// Die Registry des Sage-Servers nennt die gesamte Farm samt Stationen. Ohne diese
    /// Namen bliebe die Pseudonymisierung genau dort unvollstaendig, wo sie am meisten
    /// preisgibt: bei einer Liste aller Rechner der Installation.
    /// </remarks>
    private static void RegisterSageRegistry(IdentifierRegistry registry, SageRegistrySnapshot? sage)
    {
        if (sage is null) return;
        registry.Register(sage.SageServer?.TrimStart('\\'));
        foreach (var server in sage.ApplicationServers)
        {
            registry.Register(server.Host);
            foreach (var endpoint in server.SDataEndpoints) registry.RegisterEndpointHost(endpoint.Address);
        }

        foreach (var server in sage.BlobStorageServers)
        {
            registry.Register(server.Host);
            foreach (var endpoint in server.Endpoints) registry.RegisterEndpointHost(endpoint.Address);
        }

        foreach (var gateway in sage.Gateways)
        {
            registry.Register(gateway.Host);
            registry.RegisterEndpointHost(gateway.Endpoint);
        }

        foreach (var station in sage.Stations)
        {
            registry.Register(station.Host);
            registry.RegisterEndpointHost(station.Endpoint);
        }

        // Der Server einer Datenquelle ist ein SQL-Server und kann eine Instanz tragen.
        foreach (var datasource in sage.Datasources) registry.RegisterServer(datasource.ServerName);
    }

    /// <summary>Registriert einen Wert als geschuetzten Bezeichner.</summary>
    public void Register(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        // Zu kurze Werte wuerden als Teilzeichenfolge in unbeteiligtem Text treffen.
        if (trimmed.Length < 3) return;
        if (!_protectedValues.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) _protectedValues.Add(trimmed);
    }

    /// <summary>Registriert einen SQL-Endpunkt samt Hostanteil vor Instanz, Port und Protokoll.</summary>
    public void RegisterServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return;
        Register(server);
        var value = server.Trim();
        // "tcp:HOST\INSTANZ,1433" -> auch "HOST" schuetzen, sonst bleibt der Name
        // ueber jede andere Fundstelle sichtbar.
        var withoutProtocol = value.Contains(':', StringComparison.Ordinal)
            ? value[(value.IndexOf(':', StringComparison.Ordinal) + 1)..]
            : value;
        Register(withoutProtocol.Split(['\\', ','], StringSplitOptions.TrimEntries).FirstOrDefault());
    }

    /// <summary>Registriert den Hostanteil einer Endpunktadresse; Schema, Port und Pfad bleiben erhalten.</summary>
    public void RegisterEndpointHost(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri)) Register(uri.Host);
        else Register(address);
    }

    /// <summary>Ersetzt jeden registrierten Bezeichner im Text durch seine stabile Kennung.</summary>
    public string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        // Laengste zuerst, damit "HOST\INSTANZ" vor "HOST" ersetzt wird.
        return _protectedValues.OrderByDescending(value => value.Length)
            .Aggregate(text, (current, value) => current.Replace(value, Pseudonymize(value), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Stabile, ueber Diagnosepakete hinweg vergleichbare Kennung eines Bezeichners.</summary>
    /// <remarks>
    /// Ohne Salt, damit derselbe Server in zwei Paketen dieselbe Kennung erhaelt und ein
    /// Vorher-/Nachher-Vergleich moeglich bleibt. Das ist kein Schutz gegen gezieltes
    /// Durchprobieren bekannter Namen; siehe docs/collected-data-and-permissions.md.
    /// </remarks>
    public static string Pseudonymize(string value) =>
        "id-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant())))[..12]
            .ToLowerInvariant();
}
