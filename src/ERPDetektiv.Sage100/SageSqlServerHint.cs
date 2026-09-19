using ERPDetektiv.Contracts;

namespace ERPDetektiv.Sage100;

/// <summary>Leitet den SQL-Server der Sage-Installation aus der Registry ab.</summary>
/// <remarks>
/// Die Datenquellen nennen den Server, auf dem Sage seine Datenbanken fuehrt. Damit muss
/// ihn niemand aus dem Gedaechtnis eintippen – und ein Tippfehler im Servernamen kann
/// nicht mehr als Zertifikats- oder Verbindungsproblem missverstanden werden.
///
/// Der Vorschlag ersetzt keine Eingabe: Er belegt ein leeres Feld vor und bleibt
/// aenderbar. Auf einem Satelliten ohne Datenquellen liefert er nichts.
/// </remarks>
public static class SageSqlServerHint
{
    /// <summary>Systemdatenquelle; sie liegt immer auf dem Server, der auch die Mandanten fuehrt.</summary>
    private const string SystemApplication = "OLGlobal";

    public static string? FromRegistry(SageRegistrySnapshot? registry)
    {
        var candidates = (registry?.Datasources ?? [])
            .Where(datasource => !string.IsNullOrWhiteSpace(datasource.ServerName)).ToList();
        if (candidates.Count == 0) return null;

        // OLGlobal ist die belastbare Quelle; die uebrigen Datenquellen koennen auf
        // getrennten Instanzen liegen.
        var system = candidates.FirstOrDefault(datasource =>
            string.Equals(datasource.Applications, SystemApplication, StringComparison.OrdinalIgnoreCase));
        if (system is not null) return system.ServerName;

        // Sonst der Server, auf dem die meisten Datenquellen liegen. Bei Gleichstand
        // entscheidet der Name, damit der Vorschlag zwischen Laeufen stabil bleibt.
        return candidates.GroupBy(datasource => datasource.ServerName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;
    }
}
