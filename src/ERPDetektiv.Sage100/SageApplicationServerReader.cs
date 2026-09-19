using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using ERPDetektiv.Contracts;
using ERPDetektiv.Platform;

namespace ERPDetektiv.Sage100;

public interface ISageApplicationServerReader
{
    Task<CollectionResult<SageApplicationServerSnapshot>> CollectAsync(string installationPath,
        CancellationToken cancellationToken);
}

public sealed record SageApplicationServerCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool TimedOut { get; init; }
}

public interface ISageApplicationServerCommandRunner
{
    Task<SageApplicationServerCommandResult> ExecuteAsync(string installationPath, CancellationToken cancellationToken);
}

public sealed class SageApplicationServerReader(ISageApplicationServerCommandRunner? commandRunner = null)
    : ISageApplicationServerReader
{
    private readonly ISageApplicationServerCommandRunner _commandRunner =
        commandRunner ?? new PowerShellSageApplicationServerCommandRunner();

    public const string Id = "sage100.application-server";

    public async Task<CollectionResult<SageApplicationServerSnapshot>> CollectAsync(string installationPath,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var coreAssembly = Path.Combine(installationPath, "Sagede.ApplicationServer.Administration.PowerShell.dll");
        var clientAssembly = Path.Combine(installationPath, "Sagede.ApplicationServer.Administration.Client.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(coreAssembly) || !File.Exists(clientAssembly))
            return Result(null, CollectorState.Skipped, started,
                "Sage Application Server oder seine Administrationsbibliotheken nicht gefunden.");

        try
        {
            var execution = await _commandRunner.ExecuteAsync(installationPath, cancellationToken);
            if (execution.TimedOut)
                return Result(null, CollectorState.Failed, started,
                    "Die Sage-Administrationsabfrage wurde nach Ablauf der Zeitgrenze abgebrochen.");
            if (execution.ExitCode != 0)
                return Result(null, CollectorState.Failed, started,
                    CleanError(execution.StandardError, execution.StandardOutput));

            var payload = JsonSerializer.Deserialize<HelperPayload>(execution.StandardOutput, JsonOptions);
            if (payload is null)
                return Result(null, CollectorState.Failed, started,
                    "Die Sage-Administrationsabfrage lieferte keine lesbaren Daten.");

            var snapshot = new SageApplicationServerSnapshot(
                payload.Settings ?? [],
                payload.IsolationProcesses,
                payload.SDataEndpoints ?? [],
                payload.SoapServices ?? [])
            {
                ServiceDomains = payload.ServiceDomains ?? []
            };
            return Result(snapshot, CollectorState.Succeeded, started,
                $"{snapshot.Settings.Count} Application-Server-Einstellungen, {snapshot.IsolationProcesses?.ServiceDomainCount ?? 0} Service-Domains, {snapshot.SDataEndpoints.Count} SData- und {snapshot.SoapServices.Count} SOAP-Services erfasst.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Result(null, CollectorState.Failed, started,
                $"Sage-Administrationsabfrage fehlgeschlagen: {exception.Message}");
        }
    }

    private static CollectionResult<SageApplicationServerSnapshot> Result(SageApplicationServerSnapshot? value,
        CollectorState state, DateTimeOffset started, string message) =>
        new(value, new CollectorStatus(Id, state, started, DateTimeOffset.UtcNow, message));

    /// <summary>Erste echte Fehlerzeile des Hilfsprozesses.</summary>
    /// <remarks>
    /// PowerShell serialisiert bei umgeleitetem Fehlerstrom auch den
    /// Fortschrittsstrom als CLIXML. Ohne Filter landet "#&lt; CLIXML" als
    /// Fehlermeldung im Status und verdeckt die eigentliche Ursache.
    /// </remarks>
    private static string CleanError(string standardError, string standardOutput)
    {
        var source = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        var line = source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(candidate => !candidate.StartsWith("#< CLIXML", StringComparison.Ordinal) &&
                                         !candidate.StartsWith("<Objs", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(line)
            ? "Die Sage-Administrationsabfrage ist fehlgeschlagen."
            : $"Sage-Administrationsabfrage fehlgeschlagen: {line}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record HelperPayload(
        IReadOnlyList<SageApplicationServerSettingSnapshot>? Settings,
        SageIsolationProcessSummary? IsolationProcesses,
        IReadOnlyList<SageServiceEndpointSnapshot>? SDataEndpoints,
        IReadOnlyList<SageSoapServiceSnapshot>? SoapServices,
        IReadOnlyList<SageServiceDomainSnapshot>? ServiceDomains);
}

[ExcludeFromCodeCoverage]
public sealed class PowerShellSageApplicationServerCommandRunner(TimeSpan? timeout = null)
    : ISageApplicationServerCommandRunner
{
    /// <summary>Zeitgrenze fuer die Sage-Abfrage.</summary>
    /// <remarks>
    /// Ohne Grenze haengt das Werkzeug unbegrenzt, wenn der Application Server unter Last
    /// nicht antwortet – genau in der Situation, in der es eingesetzt wird.
    /// </remarks>
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);

    public async Task<SageApplicationServerCommandResult> ExecuteAsync(string installationPath,
        CancellationToken cancellationToken)
    {
        // Die Sage-Administrationsbibliotheken sind 32-Bit; die 64-Bit-PowerShell kann sie nicht laden.
        var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64",
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShell))
            throw new FileNotFoundException("Die erforderliche 32-Bit-Windows-PowerShell wurde nicht gefunden.",
                powerShell);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(CreateScript(installationPath)));
        var startInfo = new ProcessStartInfo(powerShell)
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }
        };
        var result = await ProcessRunner.RunAsync(startInfo, _timeout, cancellationToken);
        return new SageApplicationServerCommandResult(result.ExitCode, result.StandardOutput, result.StandardError)
            { TimedOut = result.TimedOut };
    }

    /// <summary>Ressourcenname der gemeinsamen Sage-Abfrage.</summary>
    /// <remarks>
    /// Das Skript liegt als Datei unter Scripts/ und ist damit die einzige Quelle
    /// dieser Abfrage: tools/Build-Recorder.ps1 bettet dieselbe Datei in das
    /// Messwerkzeug ein. Frueher existierte sie zweimal - einmal hier als
    /// C#-Zeichenkette, einmal im Skript - und lief auseinander.
    /// </remarks>
    private const string ScriptResource = "ERPDetektiv.Sage100.Scripts.Get-SageApplicationServerInfo.ps1";

    /// <summary>Platzhalter fuer das Installationsverzeichnis im Abfrageskript.</summary>
    public const string RootPlaceholder = "__SAGE_APPLICATION_SERVER_ROOT__";

    private static string CreateScript(string installationPath)
    {
        var path = installationPath.Replace("'", "''", StringComparison.Ordinal);
        return LoadScriptTemplate().Replace(RootPlaceholder, path, StringComparison.Ordinal);
    }

    internal static string LoadScriptTemplate()
    {
        var assembly = typeof(PowerShellSageApplicationServerCommandRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(ScriptResource)
                           ?? throw new InvalidOperationException(
                               $"Die eingebettete Ressource {ScriptResource} fehlt.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
