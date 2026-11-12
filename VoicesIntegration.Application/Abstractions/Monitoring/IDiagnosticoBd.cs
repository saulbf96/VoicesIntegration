namespace VoicesIntegration.Application.Abstractions.Monitoring;

/// <summary>Mide la velocidad hacia la BD para saber si el freno es la RED o el SERVIDOR de BD.</summary>
public interface IDiagnosticoBd
{
    Task<DiagnosticoBdResultado> MedirAsync(CancellationToken cancellationToken);
}

/// <param name="LatenciaMs">Promedio de ida y vuelta de un SELECT 1 (red + servidor, sin trabajo).</param>
/// <param name="RedMbPorSegundo">Mandar 5 MB a SQL sin guardar nada: velocidad de la RED.</param>
/// <param name="TempdbSegundos">Carga masiva de 2,000 avisos (~2.3 KB) a una tabla temporal: red + tempdb.</param>
/// <param name="BdRealSegundos">500 inserts en la tabla de eventos dentro de una transacción que se DESHACE: disco/log de la BD.</param>
public sealed record DiagnosticoBdResultado(
    double LatenciaMs, double RedMbPorSegundo, double TempdbSegundos, double BdRealSegundos, string Interpretacion);
