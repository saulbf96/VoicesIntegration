namespace VoicesIntegration.Application.Abstractions.Monitoring;

public interface IDiagnosticoBd
{
    Task<DiagnosticoBdResultado> MedirAsync(CancellationToken cancellationToken);
}

public sealed record DiagnosticoBdResultado(
    double LatenciaMs, double RedMbPorSegundo, double TempdbSegundos, double BdRealSegundos, string Interpretacion);
