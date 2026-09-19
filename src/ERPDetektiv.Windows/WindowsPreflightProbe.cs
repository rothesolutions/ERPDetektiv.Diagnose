using ERPDetektiv.Contracts;

namespace ERPDetektiv.Windows;

/// <summary>Prueft, ob die Windows-Inventur ohne erhoehte Rechte laeuft.</summary>
public sealed class WindowsPreflightProbe(WindowsInventoryReader? inventoryReader = null) : IPreflightProbe
{
    private readonly WindowsInventoryReader _inventoryReader = inventoryReader ?? new WindowsInventoryReader();

    public string Id => "windows";

    public async Task<IReadOnlyList<PreflightResult>> RunAsync(CancellationToken cancellationToken)
    {
        var (inventory, error) = await _inventoryReader.ReadAsync(cancellationToken);
        if (error is not null)
            return
            [
                new PreflightResult(Id, "Windows-Inventur", PreflightState.Warning,
                    $"{error} Dienste, Energieplan und Hardwaredaten entfallen.")
            ];

        var services = inventory?.ToServiceSnapshots() ?? [];
        return
        [
            new PreflightResult(Id, "Windows-Inventur", PreflightState.Ok,
                $"{services.Count} SQL-/Sage-nahe Dienste erkannt."),
            services.Count == 0
                ? new PreflightResult(Id, "SQL-/Sage-Dienste", PreflightState.Warning,
                    "Kein passender Dienst gefunden. Läuft die Diagnose auf dem richtigen Server?")
                : new PreflightResult(Id, "SQL-/Sage-Dienste", PreflightState.Ok,
                    string.Join(", ", services.Take(5).Select(service => service.Name)) +
                    (services.Count > 5 ? " …" : string.Empty))
        ];
    }
}
