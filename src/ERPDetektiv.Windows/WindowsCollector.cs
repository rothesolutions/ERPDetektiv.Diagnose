using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ERPDetektiv.Contracts;
using ERPDetektiv.Platform;

namespace ERPDetektiv.Windows;

/// <summary>Fragt Windows-Systemdaten in einem einzigen PowerShell-Lauf ab.</summary>
/// <remarks>
/// Bewusst eine Abfrage statt drei Prozessstarts: Der Collector laeuft auf produktiven
/// Servern und kann bei Bedarf wiederholt ausgefuehrt werden.
/// </remarks>
public sealed class WindowsInventoryReader
{
    /// <summary>Filter fuer SQL- und Sage-nahe Dienste. Bewusst eng, damit keine fremden
    /// Dienste in den Export geraten.</summary>
    private const string ServiceFilter =
        "$_.Name -match '^(MSSQL|SQLSERVERAGENT|SQLBrowser|SQLTELEMETRY|Sagede|Sage)' -or $_.DisplayName -match '^(SQL Server|SQL Server Agent|Sage)'";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<(WindowsInventory? Inventory, string? Error)> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return (null, "Die Windows-Inventur ist nur unter Windows verfügbar.");
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            $system = Get-CimInstance Win32_ComputerSystem
            $processor = @(Get-CimInstance Win32_Processor) | Select-Object -First 1
            # Win32_PowerPlan ist nicht auf jedem System verfuegbar; powercfg liefert die
            # GUID zuverlaessig und sprachunabhaengig.
            $powerPlanName = $null
            $powerPlanGuid = $null
            try {
              $powercfg = [string](powercfg /getactivescheme)
              $powerPlanGuid = [regex]::Match($powercfg, '[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}').Value
              # Der Anzeigename kann selbst Klammern enthalten ("HP Optimized (Modern Standby)").
              $open = $powercfg.IndexOf('(')
              $close = $powercfg.LastIndexOf(')')
              if ($open -ge 0 -and $close -gt $open) { $powerPlanName = $powercfg.Substring($open + 1, $close - $open - 1) }
            } catch { }
            $services = @(Get-CimInstance Win32_Service | Where-Object { {{ServiceFilter}} } | Select-Object Name, State, StartMode)
            [pscustomobject]@{
              TotalPhysicalMemoryBytes = [long]$system.TotalPhysicalMemory
              ProcessorName = [string]$processor.Name
              PowerPlanName = $powerPlanName
              PowerPlanGuid = $powerPlanGuid
              Services = $services
            } | ConvertTo-Json -Depth 4 -Compress
            """;
        var result = await ProcessRunner.RunPowerShellAsync(command, TimeSpan.FromSeconds(30), cancellationToken);
        if (result.TimedOut) return (null, "Die Windows-Inventur wurde nach 30 Sekunden abgebrochen.");
        if (!result.Succeeded)
            return (null, $"Die Windows-Inventur ist fehlgeschlagen: {result.FirstErrorLine("Unbekannter Fehler")}");
        try
        {
            return (JsonSerializer.Deserialize<WindowsInventory>(result.StandardOutput, JsonOptions), null);
        }
        catch (JsonException exception)
        {
            return (null, $"Die Windows-Inventur lieferte keine lesbaren Daten: {exception.Message}");
        }
    }
}

public sealed record WindowsInventory(
    long? TotalPhysicalMemoryBytes,
    string? ProcessorName,
    string? PowerPlanName,
    string? PowerPlanGuid,
    [property: JsonPropertyName("Services")] IReadOnlyList<WindowsServiceWire>? Services)
{
    public IReadOnlyList<ServiceSnapshot> ToServiceSnapshots() =>
        (Services ?? [])
        .Select(service => new ServiceSnapshot(service.Name ?? "unbekannt", service.State ?? "Unknown",
            service.StartMode ?? "Unknown"))
        .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record WindowsServiceWire(string? Name, string? State, string? StartMode);

public sealed class WindowsCollector(WindowsInventoryReader? inventoryReader = null) : ICollector<SystemSnapshot>
{
    private readonly WindowsInventoryReader _inventoryReader = inventoryReader ?? new WindowsInventoryReader();

    public string Id => "windows";

    public async Task<CollectionResult<SystemSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady)
            .Select(drive => new DriveInfoSnapshot(drive.Name, drive.TotalSize, drive.AvailableFreeSpace)).ToList();
        var (inventory, error) = await _inventoryReader.ReadAsync(cancellationToken);
        var services = inventory?.ToServiceSnapshots() ?? [];
        var snapshot = new SystemSnapshot(
            RuntimeInformation.OSDescription,
            Environment.MachineName,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            // Physischer Host-RAM. Frueher stand hier das GC-Limit des eigenen Prozesses -
            // ein plausibel aussehender, aber falscher Wert.
            inventory?.TotalPhysicalMemoryBytes,
            TimeZoneInfo.Local.Id,
            drives,
            services,
            inventory?.PowerPlanName,
            await ReadDotnetRuntimesAsync(cancellationToken),
            inventory?.ProcessorName,
            inventory?.PowerPlanGuid);
        return new CollectionResult<SystemSnapshot>(snapshot,
            new CollectorStatus(Id, error is null ? CollectorState.Succeeded : CollectorState.Partial, started,
                DateTimeOffset.UtcNow,
                error ?? $"{services.Count} relevante SQL-/Sage-Dienste erfasst."));
    }

    private static async Task<IReadOnlyList<string>> ReadDotnetRuntimesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(new ProcessStartInfo("dotnet", "--list-runtimes"),
                TimeSpan.FromSeconds(10), cancellationToken);
            return result.Succeeded
                ? result.StandardOutput.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
        catch (OperationCanceledException) { throw; }
        catch (System.ComponentModel.Win32Exception)
        {
            // Ohne installiertes .NET-SDK/-Runtime gibt es kein "dotnet" im Pfad. Das ist
            // auf Sage-Servern der Normalfall und kein Fehler.
            return [];
        }
    }
}
