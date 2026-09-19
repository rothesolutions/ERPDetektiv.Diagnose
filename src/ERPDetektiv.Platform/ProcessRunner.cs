using System.Diagnostics;

namespace ERPDetektiv.Platform;

/// <summary>Ergebnis eines Hilfsprozesses. <see cref="TimedOut"/> unterscheidet den
/// abgebrochenen Lauf vom regulären Fehlschlag mit Exit-Code.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>Erste nicht leere Zeile der Fehlerausgabe, ersatzweise der Standardausgabe.</summary>
    public string FirstErrorLine(string fallback)
    {
        var source = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        var line = source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? fallback : line;
    }
}

/// <summary>Startet kurzlebige Hilfsprozesse und liest deren Ausgabe.</summary>
/// <remarks>
/// Beide Ausgabestroeme werden gleichzeitig gelesen. Sequenzielles Lesen kann sich
/// verklemmen, sobald der Kindprozess den Puffer des noch nicht gelesenen Stroms
/// fuellt. Jeder Lauf hat ausserdem eine harte Zeitgrenze: Diagnose laeuft auf
/// produktiven Systemen, ein haengender Hilfsprozess darf das Werkzeug nicht blockieren.
/// </remarks>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

        // Beide Stroeme parallel leeren, damit kein voller Puffer den Kindprozess anhaelt.
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask, TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process);
            return new ProcessResult(-1, await Salvage(outputTask), await Salvage(errorTask), TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            throw;
        }
    }

    /// <summary>Startet <c>powershell.exe</c> mit einem Inline-Befehl ohne Profil.</summary>
    public static Task<ProcessResult> RunPowerShellAsync(string command, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }
        };
        return RunAsync(startInfo, timeout, cancellationToken);
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* bereits beendet */ }
        catch (NotSupportedException) { /* Remote-Prozess, hier nicht erreichbar */ }
        catch (System.ComponentModel.Win32Exception) { /* Zugriff verweigert oder Prozess verschwunden */ }
    }

    /// <summary>Liefert die bis zum Abbruch gelesene Ausgabe, ohne einen Folgefehler zu werfen.</summary>
    private static async Task<string> Salvage(Task<string> readTask)
    {
        try { return await readTask; }
        catch (OperationCanceledException) { return string.Empty; }
        catch (IOException) { return string.Empty; }
    }
}
