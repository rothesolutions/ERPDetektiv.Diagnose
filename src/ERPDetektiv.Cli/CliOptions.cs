using System.Globalization;
using System.Text.Json;
using ERPDetektiv.Contracts;

namespace ERPDetektiv.Cli;

public enum ExitCode
{
    Success = 0,
    UsageError = 1,
    ExportFailed = 2,

    /// <summary>Paket geschrieben, aber mindestens ein Collector war unvollstaendig.</summary>
    IncompleteDiagnosis = 3,
    Cancelled = 4
}

public sealed record CliOptions(
    string Output,
    string? SqlServer,
    string? Database,
    bool Pseudonymize,
    bool PreflightOnly,
    CaseNotes? Notes)
{
    public const string SqlConnectionVariable = "ERPDETEKTIV_SQL_CONNECTION";

    private static readonly string[] KnownFlags = ["--pseudonymize", "--preflight", "--help", "-h"];

    private static readonly string[] KnownValueOptions =
        ["--output", "--sql-server", "--database", "--notes-file", "--symptom"];

    private static readonly JsonSerializerOptions NotesJson = new() { PropertyNameCaseInsensitive = true };

    public static string HelpText =>
        $"""
         ERPDetektiv Diagnose – lokaler technischer Snapshot eines Sage-100-/SQL-Server-Systems.

         Verwendung:
           ERPDetektiv.Cli [Optionen]

         Optionen:
           --output <Pfad>        Zieldatei des Diagnosepakets (Standard: erpdetektiv-diagnostic-<Zeitstempel>.zip)
           --sql-server <Name>    Bezeichnung des SQL-Servers für den Bericht
           --database <Name>      Datenbank für datenbankspezifische Details (Query Store, Log-Space)
           --pseudonymize         Host- und Servernamen im Export durch stabile Kennungen ersetzen
           --preflight            Nur den Vorabcheck ausführen: Erreichbarkeit, Berechtigungen,
                                  Sage-Erkennung und Exportpfad prüfen, ohne Paket zu schreiben
           --symptom <Text>       Beobachtetes Symptom als Fallnotiz beilegen
           --notes-file <Pfad>    Fallnotizen aus einer JSON-Datei übernehmen. Felder:
                                  symptom, observedSince, affectedArea, recentChanges, additionalNotes
           --help, -h             Diese Hilfe anzeigen

         Umgebungsvariablen:
           {SqlConnectionVariable}
                                  Connection String für den SQL-Snapshot. Wird weder
                                  protokolliert noch exportiert. Ohne diese Variable
                                  entsteht ein Snapshot ohne SQL-Daten.

         Exit-Codes:
           0  Diagnose vollständig beziehungsweise Vorabcheck ohne Beanstandung
           1  Fehlerhafte Aufrufparameter
           2  Diagnose oder Export fehlgeschlagen; Vorabcheck blockierend
           3  Paket geschrieben, aber mindestens ein Collector unvollständig;
              Vorabcheck mit Einschränkungen
           4  Abgebrochen
         """;

    public static CliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith('-')) continue;
            if (KnownFlags.Contains(argument, StringComparer.OrdinalIgnoreCase)) continue;
            if (!KnownValueOptions.Contains(argument, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unbekannte Option: {argument}");
            // Ein folgendes "--flag" ist kein Wert; sonst verschluckt eine vergessene
            // Angabe still die naechste Option.
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Für {argument} fehlt ein Wert.");
            index++;
        }

        var output = GetValue(args, "--output") ?? Path.Combine(Environment.CurrentDirectory,
            $"erpdetektiv-diagnostic-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip");
        return new CliOptions(output, GetValue(args, "--sql-server"), GetValue(args, "--database"),
            args.Contains("--pseudonymize", StringComparer.OrdinalIgnoreCase),
            args.Contains("--preflight", StringComparer.OrdinalIgnoreCase),
            ReadNotes(GetValue(args, "--notes-file"), GetValue(args, "--symptom")));
    }

    private static CaseNotes? ReadNotes(string? notesFile, string? symptom)
    {
        CaseNotes? notes = null;
        if (!string.IsNullOrWhiteSpace(notesFile))
        {
            if (!File.Exists(notesFile)) throw new ArgumentException($"Die Notizdatei {notesFile} existiert nicht.");
            try
            {
                notes = JsonSerializer.Deserialize<CaseNotes>(File.ReadAllText(notesFile), NotesJson);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException($"Die Notizdatei {notesFile} ist kein gültiges JSON: {exception.Message}");
            }
        }

        // --symptom hat Vorrang, damit ein schneller Aufruf die Datei ergaenzen kann.
        if (!string.IsNullOrWhiteSpace(symptom)) notes = (notes ?? new CaseNotes()) with { Symptom = symptom };
        return notes is null || notes.IsEmpty ? null : notes;
    }

    private static string? GetValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
