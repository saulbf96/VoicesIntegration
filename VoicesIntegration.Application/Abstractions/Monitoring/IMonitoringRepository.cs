namespace VoicesIntegration.Application.Abstractions.Monitoring;

/// <summary>
/// Registros de disponibilidad de la API y reporte de huecos (SPs dbo.USP_REGISTRAR_DISPONIBILIDAD_VOICES y dbo.USP_REPORTE_HUECOS_VOICES).
/// El webhook es de un solo sentido (no se le puede preguntar a Voices qué faltó): por eso la API
/// registra cada minuto que estuvo viva, y así se sabe EXACTAMENTE qué rangos hay que recuperar.
/// </summary>
public interface IMonitoringRepository
{
    Task RegisterHeartbeatAsync(string server, int pending, int rejected, CancellationToken cancellationToken);

    Task<GapReport> GetGapReportAsync(DateOnly date, CancellationToken cancellationToken);
}

public sealed record GapReport(DateOnly Fecha, IReadOnlyList<HourCoverage> Horas, IReadOnlyList<DowntimeWindow> VentanasSinServicio);

public sealed record HourCoverage
{
    public string Hora { get; init; } = string.Empty;
    public int Minutos_Transcurridos { get; init; }
    public int Minutos_Con_Servicio { get; init; }
    public int Avisos_Blaster { get; init; }
    public int Avisos_Sms { get; init; }
    public string Estado { get; init; } = string.Empty;
}

public sealed record DowntimeWindow
{
    public DateTime Desde { get; init; }
    public DateTime Hasta { get; init; }
    public int Minutos { get; init; }


}
