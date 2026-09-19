using ERPDetektiv.Contracts;

namespace ERPDetektiv.Checks;

/// <summary>Mittlere Zugriffszeit auf die Datenbankdateien.</summary>
/// <remarks>
/// Der einzige Regelkandidat aus der Vier-Server-Messreihe, der die Pruefung gegen ein
/// gut eingestelltes System ueberstanden hat. Der Grund: Sein Grenzwert stammt nicht aus
/// der Messung, sondern aus etablierter Praxis - unter 10 ms gut, ueber 20 ms auffaellig,
/// unabhaengig von Systemgroesse und Last. Auf dem Referenzsystem lagen die
/// Sage-Datenbanken bei 1,8 ms Lesen und 0,4 ms Schreiben.
///
/// Bewusst nicht die Summe der Wartezeit: Sechstausend Sekunden klingen dramatisch,
/// verteilt auf Millionen Zugriffe sind es zwei Millisekunden.
/// </remarks>
public sealed class SqlIoLatencyCheck(double warningThresholdMs = 20, long minimumOperations = 1000) : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.SqlServer?.Databases
        .Where(SnapshotFilters.IsOnline)
        .Select(database => (database, Slowest: Slowest(database)))
        .Where(item => item.Slowest is not null)
        .Select(item => new Finding("sql.io.latency", "SQL Server", "Zugriffszeit auf Datenbankdateien auffällig",
            $"{item.database.Name}: {SnapshotFilters.Invariant(item.Slowest!.Value.Value, "0.#")} ms mittlere {item.Slowest!.Value.Kind} je Zugriff.",
            Severity.Warning,
            "Datenträger, Virtualisierung und Speicheranbindung prüfen. Richtwert: unter 10 ms unauffällig, über 20 ms auffällig.",
            [
                new Evidence("database", item.database.Name),
                new Evidence("readLatencyMs", Format(item.database.ReadLatencyMs)),
                new Evidence("writeLatencyMs", Format(item.database.WriteLatencyMs)),
                new Evidence("readCount", Format(item.database.ReadCount)),
                new Evidence("writeCount", Format(item.database.WriteCount)),
                new Evidence("thresholdMs", SnapshotFilters.Invariant(warningThresholdMs, "0.#"))
            ])) ?? [];

    /// <summary>Der auffälligere der beiden Werte, sofern genügend Zugriffe vorliegen.</summary>
    /// <remarks>
    /// Ohne Mindestanzahl wuerde eine Datenbank mit fuenf Zugriffen, davon einer langsam,
    /// eine Warnung ausloesen. Der Mittelwert ist dort schlicht nicht aussagekraeftig.
    /// </remarks>
    private (string Kind, double Value)? Slowest(DatabaseSnapshot database)
    {
        (string Kind, double Value)? worst = null;
        if (database.ReadCount >= minimumOperations && database.ReadLatencyMs >= warningThresholdMs)
            worst = ("Lesezeit", database.ReadLatencyMs.Value);
        if (database.WriteCount >= minimumOperations && database.WriteLatencyMs >= warningThresholdMs &&
            (worst is null || database.WriteLatencyMs > worst.Value.Value))
            worst = ("Schreibzeit", database.WriteLatencyMs!.Value);
        return worst;
    }

    private static string Format(double? value) =>
        value is null ? "nicht erfasst" : SnapshotFilters.Invariant(value.Value, "0.##");

    private static string Format(long? value) =>
        value is null ? "nicht erfasst" : SnapshotFilters.Invariant(value.Value);
}

/// <summary>Page Life Expectancy gegen eine an der Bufferpool-Größe skalierte Referenz.</summary>
/// <remarks>
/// Die verbreitete Pauschalregel "unter 300 Sekunden" stammt aus einer Zeit mit deutlich
/// kleineren Servern und ist heute wertlos. Die skalierte Fassung
/// 300 × (Bufferpool-GB / 4) traegt der Groesse Rechnung; 300 Sekunden bleiben die
/// Untergrenze. Ohne die Bufferpool-Groesse schweigt der Check.
/// </remarks>
public sealed class SqlPageLifeExpectancyCheck : ICheck
{
    private const long MinimumReferenceSeconds = 300;
    private const double ReferencePerFourGigabytes = 300;

    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        var sql = snapshot.SqlServer;
        if (sql?.PageLifeExpectancySeconds is not { } ple || sql.BufferPoolBytes is not { } bufferPool ||
            bufferPool <= 0) return [];

        var bufferPoolGb = bufferPool / 1024d / 1024d / 1024d;
        var reference = Math.Max(MinimumReferenceSeconds, (long)(ReferencePerFourGigabytes * bufferPoolGb / 4));
        if (ple >= reference) return [];

        return
        [
            new Finding("sql.memory.page-life-expectancy", "SQL Server", "Page Life Expectancy unter der Referenz",
                $"Datenseiten verbleiben im Mittel {ple} Sekunden im Arbeitsspeicher; für einen Bufferpool von {SnapshotFilters.FormatBytes(bufferPool)} wären mindestens {reference} Sekunden zu erwarten.",
                Severity.Warning,
                "Arbeitsspeicher, max server memory und speicherintensive Abfragen prüfen. Ein einmalig niedriger Wert kann von einem Neustart oder einer großen Abfrage stammen; entscheidend ist der Verlauf.",
                [
                    new Evidence("pageLifeExpectancySeconds", SnapshotFilters.Invariant(ple)),
                    new Evidence("referenceSeconds", SnapshotFilters.Invariant(reference)),
                    new Evidence("bufferPoolBytes", SnapshotFilters.Invariant(bufferPool))
                ])
        ];
    }
}

/// <summary>Abfragen, die auf Arbeitsspeicher warten.</summary>
/// <remarks>
/// Nahezu binaer und damit ohne Kalibrierung verwendbar: Wartet zum Erfassungszeitpunkt
/// eine Abfrage auf ihren Speicher, bekommt sie ihn nicht. Belegter Arbeitsspeicher allein
/// waere dagegen kein Marker - SQL Server nimmt sich, was max server memory erlaubt, und
/// liegt im Normalbetrieb genau dort.
/// </remarks>
public sealed class SqlMemoryGrantsPendingCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot)
    {
        if (snapshot.SqlServer?.MemoryGrantsPending is not { } pending || pending <= 0) return [];
        return
        [
            new Finding("sql.memory.grants-pending", "SQL Server", "Abfragen warten auf Arbeitsspeicher",
                $"Zum Erfassungszeitpunkt warteten {pending} Abfrage(n) auf eine Speicherzuteilung.",
                Severity.Warning,
                "Speicherbedarf großer Abfragen, max server memory und Resource Governor prüfen. Eine einzelne Momentaufnahme kann eine Spitze treffen; wiederholt gemessen ist der Wert ein deutlicher Hinweis.",
                [new Evidence("memoryGrantsPending", SnapshotFilters.Invariant(pending))])
        ];
    }
}
