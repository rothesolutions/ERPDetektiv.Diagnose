using System.Globalization;
using ERPDetektiv.Contracts;

namespace ERPDetektiv.Checks;

/// <summary>Gemeinsame Abgrenzungen und Formatierungen der Checks.</summary>
internal static class SnapshotFilters
{
    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    /// <summary>Benutzerdatenbanken. Systemdatenbanken folgen eigenen Regeln.</summary>
    public static bool IsUserDatabase(DatabaseSnapshot database) =>
        !SystemDatabases.Contains(database.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Nur Online-Datenbanken bewerten.</summary>
    /// <remarks>
    /// Eine Datenbank im Status RESTORING oder OFFLINE hat planmaessig kein aktuelles
    /// Backup und keine belastbaren Dateiangaben; eine Warnung waere hier ein Fehlalarm.
    /// </remarks>
    public static bool IsOnline(DatabaseSnapshot database) =>
        string.Equals(database.Status, "ONLINE", StringComparison.OrdinalIgnoreCase);

    public static bool IsAssessableUserDatabase(DatabaseSnapshot database) =>
        IsUserDatabase(database) && IsOnline(database);

    /// <summary>Menschenlesbare Groesse mit einer Nachkommastelle.</summary>
    public static string FormatBytes(long value)
    {
        string[] units = ["Byte", "KiB", "MiB", "GiB", "TiB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Invariant(double value, string format = "0.#") =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
