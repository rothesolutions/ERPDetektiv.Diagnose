using ERPDetektiv.Application;
using ERPDetektiv.Cli;
using ERPDetektiv.Contracts;
using ERPDetektiv.Core;

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(CliOptions.HelpText);
    return (int)ExitCode.Success;
}

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CliOptions.HelpText);
    return (int)ExitCode.UsageError;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var sqlConnection = Environment.GetEnvironmentVariable(CliOptions.SqlConnectionVariable);

if (options.PreflightOnly)
{
    try
    {
        var preflight = DiagnosticComposition.CreatePreflightRunner(options.Output, sqlConnection, options.Database);
        var report = await preflight.RunAsync(null, cancellation.Token);
        foreach (var result in report.Results)
            Console.WriteLine($"[{Symbol(result.State)}] {result.Name}: {result.Message}");
        Console.WriteLine();
        Console.WriteLine(report.Summary());
        return (int)(report.HasFailures ? ExitCode.ExportFailed :
            report.HasWarnings ? ExitCode.IncompleteDiagnosis : ExitCode.Success);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Der Vorabcheck wurde abgebrochen.");
        return (int)ExitCode.Cancelled;
    }
}

var sqlCollector = DiagnosticComposition.CreateSqlCollector(sqlConnection, options.SqlServer, options.Database);
if (sqlCollector is null)
    Console.WriteLine(
        $"Hinweis: {CliOptions.SqlConnectionVariable} ist nicht gesetzt – der SQL-Snapshot wird übersprungen.");

var runner = DiagnosticComposition.CreateRunner(sqlConnection, options.SqlServer, options.Database);

try
{
    var progress = new Progress<string>(Console.WriteLine);
    var snapshot = await runner.RunAsync(progress, cancellation.Token);
    DiagnosticExporter.Export(snapshot, options.Output,
        new ExportOptions(PseudonymizeIdentifiers: options.Pseudonymize), notes: options.Notes);

    Console.WriteLine($"Diagnosepaket erstellt: {Path.GetFullPath(options.Output)}");
    if (options.Notes is not null) Console.WriteLine("Fallnotizen wurden als notes.json beigelegt.");
    Console.WriteLine(
        $"Findings: {snapshot.Findings.Count}; Collector-Status: {string.Join(", ", snapshot.CollectionStatus.Select(status => $"{status.CollectorId}={status.State}"))}");
    foreach (var status in snapshot.CollectionStatus.Where(status =>
                 status.State is CollectorState.Failed or CollectorState.Partial))
        Console.Error.WriteLine($"  {status.CollectorId}: {status.Message}");

    // Der Exit-Code unterscheidet den vollstaendigen vom unvollstaendigen Lauf, damit
    // Support-Faelle und Skripte das Ergebnis auswerten koennen.
    return (int)(snapshot.CollectionStatus.Any(status =>
        status.State is CollectorState.Failed or CollectorState.Partial)
        ? ExitCode.IncompleteDiagnosis
        : ExitCode.Success);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Die Diagnose wurde abgebrochen; es wurde kein Paket geschrieben.");
    return (int)ExitCode.Cancelled;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Das Diagnosepaket konnte nicht geschrieben werden: {exception.Message}");
    return (int)ExitCode.ExportFailed;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Die Diagnose ist fehlgeschlagen: {exception.Message}");
    return (int)ExitCode.ExportFailed;
}

static string Symbol(PreflightState state) => state switch
{
    PreflightState.Ok => "  ok  ",
    PreflightState.Warning => " warn ",
    PreflightState.Failed => "FEHLER",
    _ => " über "
};
