using System.Globalization;
using System.Text;
using ERPDetektiv.Contracts;

namespace ERPDetektiv.Core;

public static class HtmlSummary
{
    private const string Style =
        "body{font:15px Segoe UI,Arial,sans-serif;color:#1f2937;margin:32px;max-width:1100px}h1{margin-bottom:4px}h2{margin-top:32px}.meta{color:#6b7280}table{border-collapse:collapse;width:100%;margin-top:10px}th,td{border:1px solid #d1d5db;padding:8px;text-align:left;vertical-align:top}th{background:#f3f4f6}.finding{border-left:5px solid #f59e0b;background:#fffbeb;padding:12px 16px;margin:10px 0}.finding.critical{border-color:#dc2626;background:#fef2f2}.finding.info{border-color:#2563eb;background:#eff6ff}.badge{font-weight:600}.evidence{margin:8px 0 0;padding-left:20px;color:#4b5563}.ok{padding:12px;background:#ecfdf5;color:#065f46}";

    private const string NotCollected = "nicht erfasst";

    public static string Create(DiagnosticSnapshot snapshot) => Create(snapshot, null);

    public static string Create(DiagnosticSnapshot snapshot, CaseNotes? notes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var system = snapshot.System;
        var sql = snapshot.SqlServer;
        var report = new StringBuilder("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\">")
            .Append("<title>ERPDetektiv Diagnose</title><style>").Append(Style).Append("</style></head><body>")
            .Append("<h1>ERPDetektiv Diagnose</h1><p class=\"meta\">Lokaler technischer Snapshot · erstellt ")
            .Append(Escape(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)))
            .Append("</p>");

        // Die Fallnotizen stehen vor den Messwerten: Sie sagen, wonach zu suchen ist.
        AppendCaseNotes(report, notes);
        AppendOverview(report, system, sql);
        AppendFindings(report, snapshot.Findings);
        AppendDrives(report, system);
        AppendDatabases(report, sql);
        AppendSageApplicationServer(report, snapshot.Sage100?.ApplicationServer);
        AppendSageRegistry(report, snapshot.Sage100?.Registry);
        AppendCollectionStatus(report, snapshot.CollectionStatus);
        return report.Append("</body></html>").ToString();
    }

    private static void AppendCaseNotes(StringBuilder report, CaseNotes? notes)
    {
        if (notes is null || notes.IsEmpty) return;
        report.Append("<h2>Fallnotizen</h2><p class=\"meta\">Freiwillige Angaben der Anwenderin oder des Anwenders – Beobachtungen, keine Messwerte.</p><table>");
        AppendNoteRow(report, "Beobachtetes Symptom", notes.Symptom);
        AppendNoteRow(report, "Seit wann", notes.ObservedSince);
        AppendNoteRow(report, "Betroffener Bereich", notes.AffectedArea);
        AppendNoteRow(report, "Letzte Änderungen", notes.RecentChanges);
        AppendNoteRow(report, "Weitere Hinweise", notes.AdditionalNotes);
        report.Append("</table>");
    }

    private static void AppendNoteRow(StringBuilder report, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        report.Append(CultureInfo.InvariantCulture,
            $"<tr><th style=\"width:220px\">{Escape(label)}</th><td>{Escape(value.Trim()).Replace("\n", "<br>", StringComparison.Ordinal)}</td></tr>");
    }

    private static void AppendOverview(StringBuilder report, SystemSnapshot? system, SqlServerSnapshot? sql)
    {
        report.Append("<h2>Überblick</h2><table>")
            .Append(Row("System", system?.HostName, "Windows", system?.OperatingSystem))
            .Append(Row("Prozessor", system?.ProcessorName, "Logische Kerne",
                system is null ? null : Invariant(system.ProcessorCount)))
            .Append(Row("Host-RAM", system?.TotalMemoryBytes is { } memory ? FormatBytes(memory) : null, "Energieplan",
                system?.PowerPlan))
            .Append(Row("SQL Server", sql?.Server, "SQL-Version", sql?.Version))
            .Append(Row("Edition", sql?.Edition, "Virtualisierung", sql?.VirtualMachineType))
            .Append(Row("CPU / Sockets", FormatCpuTopology(sql), "SQL-Start",
                sql?.StartedAt?.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)))
            .Append("</table>");
    }

    private static string? FormatCpuTopology(SqlServerSnapshot? sql) => sql?.CpuCount is null
        ? null
        : string.Create(CultureInfo.InvariantCulture,
            $"{sql.CpuCount} logisch · {sql.SocketCount} Socket(s) · {sql.CoresPerSocket} Kerne/Socket");

    private static void AppendFindings(StringBuilder report, IReadOnlyList<Finding> findings)
    {
        report.Append(CultureInfo.InvariantCulture, $"<h2>Findings ({findings.Count})</h2>");
        if (findings.Count == 0)
        {
            report.Append("<p class=\"ok\">Keine Community-Findings ausgelöst.</p>");
            return;
        }

        // Kritisches zuerst; die Reihenfolge der Checks ist keine Priorisierung.
        foreach (var finding in findings.OrderByDescending(item => item.Severity))
            report.Append(RenderFinding(finding));
    }

    private static void AppendDrives(StringBuilder report, SystemSnapshot? system)
    {
        report.Append("<h2>Laufwerke</h2><table><tr><th>Laufwerk</th><th>Gesamt</th><th>Frei</th><th>Belegt</th></tr>");
        if (system?.Drives is not { Count: > 0 })
        {
            report.Append("<tr><td colspan=\"4\">Keine Laufwerksdaten erfasst.</td></tr></table>");
            return;
        }

        foreach (var drive in system.Drives)
        {
            var usedPercent = drive.TotalBytes > 0
                ? Invariant((drive.TotalBytes - drive.FreeBytes) * 100d / drive.TotalBytes, "0.#") + " %"
                : NotCollected;
            report.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{Escape(drive.Name)}</td><td>{Escape(FormatBytes(drive.TotalBytes))}</td><td>{Escape(FormatBytes(drive.FreeBytes))}</td><td>{Escape(usedPercent)}</td></tr>");
        }

        report.Append("</table>");
    }

    private static void AppendDatabases(StringBuilder report, SqlServerSnapshot? sql)
    {
        report.Append(
            "<h2>Datenbanken</h2><table><tr><th>Name</th><th>Status</th><th>Größe</th><th>Recovery</th><th>Zugriffszeit</th><th>Query Store</th><th>Log-Auslastung</th><th>Letztes Full Backup</th></tr>");
        if (sql?.Databases is not { Count: > 0 })
        {
            report.Append("<tr><td colspan=\"8\">Keine SQL-Daten erfasst.</td></tr></table>");
            return;
        }

        foreach (var database in sql.Databases)
            report.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{Escape(database.Name)}</td><td>{Escape(database.Status)}</td><td>{Escape(FormatBytes(database.SizeBytes))}</td><td>{Escape(database.RecoveryModel)}</td><td>{Escape(FormatLatency(database))}</td><td>{Escape(FormatQueryStore(database))}</td><td>{Escape(FormatLogSpace(database))}</td><td>{Escape(database.LastBackupAt?.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) ?? NotCollected)}</td></tr>");
        report.Append("</table>");
        report.Append(
            "<p class=\"meta\">Zugriffszeit: mittlere Lese- und Schreibdauer je Zugriff seit Instanzstart. Richtwert: unter 10 ms unauffällig, über 20 ms auffällig.</p>");
    }

    private static void AppendCollectionStatus(StringBuilder report, IReadOnlyList<CollectorStatus> statuses)
    {
        report.Append("<h2>Erfassungsstatus</h2><table><tr><th>Collector</th><th>Status</th><th>Hinweis</th></tr>");
        foreach (var status in statuses)
            report.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{Escape(status.CollectorId)}</td><td>{Escape(status.State.ToString())}</td><td>{Escape(status.Message ?? "")}</td></tr>");
        report.Append("</table>");
    }

    private static string Row(string leftName, string? leftValue, string rightName, string? rightValue) =>
        $"<tr><th>{Escape(leftName)}</th><td>{Escape(leftValue ?? NotCollected)}</td><th>{Escape(rightName)}</th><td>{Escape(rightValue ?? NotCollected)}</td></tr>";

    private static string RenderFinding(Finding finding) =>
        $"<section class=\"finding {finding.Severity.ToString().ToLowerInvariant()}\"><span class=\"badge\">{Escape(finding.Severity.ToString())}</span><h3>{Escape(finding.Name)}</h3><p>{Escape(finding.Description)}</p><p><strong>Empfehlung:</strong> {Escape(finding.Recommendation)}</p><ul class=\"evidence\">{string.Concat(finding.Evidence.Select(item => $"<li><strong>{Escape(item.Key)}:</strong> {Escape(item.Value)}</li>"))}</ul></section>";

    private static string FormatBytes(long value)
    {
        string[] units = ["Byte", "KiB", "MiB", "GiB", "TiB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {units[unit]}");
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatLatency(DatabaseSnapshot database)
    {
        if (database.ReadLatencyMs is null && database.WriteLatencyMs is null) return NotCollected;
        var read = database.ReadLatencyMs is { } r ? Invariant(r, "0.##") : "–";
        var write = database.WriteLatencyMs is { } w ? Invariant(w, "0.##") : "–";
        return $"{read} / {write} ms";
    }

    private static string FormatQueryStore(DatabaseSnapshot database) => database.QueryStoreCollectionState switch
    {
        FeatureCollectionState.Available when database.QueryStoreEnabled is true => "Aktiv",
        FeatureCollectionState.Available when database.QueryStoreEnabled is false => "Deaktiviert",
        FeatureCollectionState.NotAvailable => "Nicht verfügbar",
        FeatureCollectionState.NotSupported => "Nicht unterstützt",
        FeatureCollectionState.AccessDenied => "Berechtigung fehlt",
        FeatureCollectionState.Failed => "Abfrage fehlgeschlagen",
        _ => "Nicht erfasst"
    };

    private static string FormatLogSpace(DatabaseSnapshot database) => database.LogSpaceCollectionState switch
    {
        FeatureCollectionState.Available when database.LogSpaceUsedPercent is not null =>
            Invariant(database.LogSpaceUsedPercent.Value, "0.#") + " %",
        FeatureCollectionState.NotAvailable => "Nicht verfügbar",
        FeatureCollectionState.NotSupported => "Nicht unterstützt",
        FeatureCollectionState.AccessDenied => "Berechtigung fehlt",
        FeatureCollectionState.Failed => "Abfrage fehlgeschlagen",
        _ => "Nicht erfasst"
    };

    private static void AppendSageApplicationServer(StringBuilder report,
        SageApplicationServerSnapshot? applicationServer)
    {
        if (applicationServer is null) return;
        var isolation = applicationServer.IsolationProcesses;
        report.Append("<h2>Sage Application Server</h2><table>")
            .Append(Row("Service-Domains", isolation is null ? null : Invariant(isolation.ServiceDomainCount),
                "Asynchron", isolation is null ? null : Invariant(isolation.AsyncCount)))
            .Append(Row("Kontextwechsel", isolation is null ? null : isolation.ContextSwitchesTotal.ToString(CultureInfo.InvariantCulture),
                "Gesamt-Calls", isolation is null ? null : isolation.TotalCalls.ToString(CultureInfo.InvariantCulture)))
            .Append(Row("Working Set", isolation is null ? null : FormatBytes(isolation.WorkingSetBytes),
                "Ältester Prozess",
                isolation?.OldestProcessAgeSeconds is null
                    ? null
                    : TimeSpan.FromSeconds(isolation.OldestProcessAgeSeconds.Value).ToString("g", CultureInfo.InvariantCulture)))
            .Append(Row("SData-Endpunkte", Invariant(applicationServer.SDataEndpoints.Count), "SOAP-Services",
                Invariant(applicationServer.SoapServices.Count)))
            .Append("</table>");

        report.Append(
            "<h3>Application-Server-Einstellungen</h3><table><tr><th>Bereich</th><th>Einstellung</th><th>Wert</th></tr>");
        foreach (var setting in applicationServer.Settings)
            report.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{Escape(setting.Section)}</td><td>{Escape(setting.Name)}</td><td>{Escape(setting.Value)}</td></tr>");
        report.Append("</table>");

        report.Append("<h3>Service-Endpunkte</h3><table><tr><th>Typ</th><th>Binding</th><th>Adresse</th></tr>");
        foreach (var endpoint in applicationServer.SDataEndpoints)
            report.Append(CultureInfo.InvariantCulture,
                $"<tr><td>SData</td><td>{Escape(endpoint.Binding)}</td><td>{Escape(endpoint.Address)}</td></tr>");
        foreach (var service in applicationServer.SoapServices)
            report.Append(CultureInfo.InvariantCulture,
                $"<tr><td>SOAP</td><td>{Escape(service.Name)}</td><td>{Escape(string.Join(", ", service.Operations))}</td></tr>");
        report.Append("</table>");
    }

    /// <summary>Farm-Sicht aus der Registry.</summary>
    /// <remarks>
    /// Bewusst der einzige Abschnitt, der ueber das untersuchte System hinausgeht: Welche
    /// Rolle ein Application Server hat, entscheidet darueber, wie seine Lastwerte zu
    /// lesen sind. Auf einem Satelliten sind die Listen leer; dann bleibt der Abschnitt
    /// bis auf den Verweis auf den Sage-Server leer.
    /// </remarks>
    private static void AppendSageRegistry(StringBuilder report, SageRegistrySnapshot? registry)
    {
        if (registry is null) return;
        report.Append("<h2>Sage-Umgebung</h2><table>")
            .Append(Row("Sage-Version", registry.ProductVersion, "Sage-Server",
                registry.SageServer?.TrimStart('\\')))
            .Append(Row("Dieses System", registry.IsSageServer ? "Sage-Server" : "Zusätzliche Station",
                "Stationen", Invariant(registry.Stations.Count)))
            .Append("</table>");

        if (registry.ApplicationServers.Count > 0)
        {
            report.Append(
                "<h3>Application Server</h3><table><tr><th>Host</th><th>Zustand</th><th>Rolle</th><th>SData-Endpunkte</th></tr>");
            foreach (var server in registry.ApplicationServers)
                report.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{Escape(server.Host)}</td><td>{Escape(server.State ?? "unbekannt")}</td><td>{Escape(DescribeRole(server))}</td><td>{Invariant(server.SDataEndpoints.Count)}</td></tr>");
            report.Append("</table>");
        }

        if (registry.Datasources.Count > 0)
        {
            report.Append(
                "<h3>Datenquellen</h3><table><tr><th>Datenquelle</th><th>Server</th><th>Datenbank</th><th>Applikationen</th><th>Mandanten</th></tr>");
            foreach (var datasource in registry.Datasources)
                report.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{Escape(datasource.Name)}</td><td>{Escape(datasource.ServerName ?? "–")}</td><td>{Escape(datasource.DatabaseName ?? "–")}</td><td>{Escape(datasource.Applications ?? "–")}</td><td>{Invariant(datasource.MandatorCount)}</td></tr>");
            report.Append("</table>");
        }

        if (registry.NamedUsers.Count > 0)
        {
            report.Append(
                "<h3>Benannte Benutzer</h3><p class=\"meta\">Belegte Plätze je Applikation. Das lizenzierte Maximum steht nicht in der Registry.</p><table><tr><th>Applikation</th><th>Belegt</th></tr>");
            foreach (var entry in registry.NamedUsers)
                report.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{Escape(entry.Application)}</td><td>{Invariant(entry.Count)}</td></tr>");
            report.Append("</table>");
        }
    }

    private static string DescribeRole(SageRegistryApplicationServerSnapshot server) => server.Capability switch
    {
        3 => "Master, Lastverteilung",
        1 => "Lastverteilung",
        0 => "Einzelbetrieb",
        null => "unbekannt",
        _ => Invariant(server.Capability.Value)
    };

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value);
}
