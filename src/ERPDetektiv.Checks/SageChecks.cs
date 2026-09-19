using ERPDetektiv.Contracts;

namespace ERPDetektiv.Checks;

public sealed class SageServiceStateCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.System?.Services
        .Where(IsSageService)
        .Where(StartsAutomatically)
        .Where(service => !string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase))
        .Select(service => new Finding("sage.service.not-running", "Sage 100", "Sage-Dienst läuft nicht",
            $"Der Sage-Dienst {service.Name} ist auf automatischen Start gesetzt, hat aber den Status {service.Status}.",
            Severity.Warning,
            "Dienststatus, Windows-Ereignisprotokoll und Sage-Server-Manager prüfen.",
            [
                new Evidence("service", service.Name), new Evidence("status", service.Status),
                new Evidence("startType", service.StartType)
            ])) ?? [];

    private static bool IsSageService(ServiceSnapshot service) =>
        service.Name.StartsWith("Sage", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nur automatisch startende Dienste bewerten.</summary>
    /// <remarks>
    /// Sage 100 installiert Dienste, die planmaessig auf "Manual" oder "Disabled" stehen.
    /// Ein gestoppter Dienst ist dort der Normalzustand – eine Warnung waere ein Fehlalarm
    /// und wuerde die Glaubwuerdigkeit der uebrigen Findings kosten.
    /// </remarks>
    private static bool StartsAutomatically(ServiceSnapshot service) =>
        service.StartType.StartsWith("Auto", StringComparison.OrdinalIgnoreCase);
}

public sealed class SageSoapServiceCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.Sage100?.ApplicationServer
        ?.SoapServices
        .Select(service => new Finding("sage.soap.service.detected", "Sage 100", "Zusätzlicher SOAP-Service erkannt",
            $"SOAP-Service {service.Name} mit {service.Operations.Count} Operation(en) ist konfiguriert.",
            Severity.Info,
            "Beabsichtigte Bereitstellung, Zugriffsschutz und fachliche Notwendigkeit bestätigen.",
            [
                new Evidence("service", service.Name),
                new Evidence("operations", SnapshotFilters.Invariant(service.Operations.Count))
            ])) ?? [];
}

public sealed class SageAnonymousSDataEndpointCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.Sage100?.ApplicationServer
        ?.SDataEndpoints
        .Where(endpoint => endpoint.Binding.Contains("None", StringComparison.OrdinalIgnoreCase))
        .Select(endpoint => new Finding("sage.sdata.anonymous-endpoint", "Sage 100",
            "SData-Endpunkt ohne Anmeldung erkannt",
            $"Der SData-Endpunkt mit Binding {endpoint.Binding} ist konfiguriert.", Severity.Info,
            "Netzwerkfreigabe und vorgesehenen Zugriffsschutz prüfen; der Befund ist keine pauschale Fehlkonfiguration.",
            [new Evidence("binding", endpoint.Binding), new Evidence("address", endpoint.Address)])) ?? [];
}

public sealed class SageIsolationProcessStateCheck : ICheck
{
    public IEnumerable<Finding> Evaluate(DiagnosticSnapshot snapshot) => snapshot.Sage100?.ApplicationServer
        ?.IsolationProcesses?.States
        .Where(state => state.Count > 0 && (state.State.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                            state.State.Contains("fault", StringComparison.OrdinalIgnoreCase) ||
                                            state.State.Contains("failed", StringComparison.OrdinalIgnoreCase)))
        .Select(state => new Finding("sage.isolation-process.error-state", "Sage 100",
            "Fehlerhafte Isolationsprozesse erkannt",
            $"{state.Count} Service-Domain(s) melden den Zustand {state.State}.", Severity.Warning,
            "Sage-Application-Server-Logging und betroffene Service-Domains im Server Manager prüfen.",
            [
                new Evidence("state", state.State), new Evidence("count", SnapshotFilters.Invariant(state.Count))
            ])) ?? [];
}
