namespace VoicesIntegration.Application.Abstractions.Monitoring;

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

    public int Avisos_Blaster_Cerca { get; init; }

    public int Avisos_Sms_Cerca { get; init; }

    public string? Tipo { get; init; }

    public const string TipoNoLlegaron = "NO_LLEGARON_AVISOS";
}
