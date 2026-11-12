using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Monitoring;

namespace VoicesIntegration.Infrastructure.Monitoring;

/// <summary>
/// Cada minuto registra en la BD "la API está viva" (registro de disponibilidad). Un minuto sin registro de disponibilidad = la API
/// no estaba recibiendo, y esos avisos de Voices hay que recuperarlos del reporte de Voices.
/// Además, al cambiar de día escribe en el LOG el resumen del día anterior (horas con cortes y
/// rangos exactos a recuperar), para revisarlo cada mañana.
/// </summary>
internal sealed class HeartbeatWorker(
    IServiceScopeFactory scopeFactory,
    IWebhookInbox inbox,
    TimeProvider time,
    ILogger<HeartbeatWorker> logger) : BackgroundService
{
    private static readonly TimeZoneInfo Mexico = TryFindMexico();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = Environment.MachineName;
        var lastDay = TodayMexico();
        var failing = false;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IMonitoringRepository>();

                var status = inbox.GetStatus();
                await repo.RegisterHeartbeatAsync(server, status.Pending, status.Rejected, stoppingToken);
                if (failing) logger.LogInformation("Registro de disponibilidad restablecido.");
                failing = false;

                // Cambio de día: resumen del día anterior en el log.
                var today = TodayMexico();
                if (today != lastDay)
                {
                    await LogDailySummaryAsync(repo, lastDay, stoppingToken);
                    lastDay = today;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Sin BD no hay registro de disponibilidad (pero los avisos siguen a salvo en la bandeja). Avisar una sola vez.
                if (!failing) logger.LogWarning("No se pudo registrar la disponibilidad de la API: {Error}", ex.Message);
                failing = true;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task LogDailySummaryAsync(IMonitoringRepository repo, DateOnly day, CancellationToken ct)
    {
        var r = await repo.GetGapReportAsync(day, ct);
        var blaster = r.Horas.Sum(h => h.Avisos_Blaster);
        var sms = r.Horas.Sum(h => h.Avisos_Sms);
        var malas = r.Horas.Where(h => h.Estado.StartsWith("SIN SERVICIO") || h.Estado.StartsWith("CON CORTES")).ToList();

        if (malas.Count == 0)
        {
            logger.LogInformation("RESUMEN {Day:yyyy-MM-dd}: servicio completo. Avisos Blaster {Blaster}, SMS {Sms}.", day, blaster, sms);
            return;
        }

        logger.LogWarning("RESUMEN {Day:yyyy-MM-dd}: {Count} horas con cortes. Avisos Blaster {Blaster}, SMS {Sms}. RECUPERAR estos rangos del reporte de Voices:",
            day, malas.Count, blaster, sms);
        foreach (var v in r.VentanasSinServicio)
            logger.LogWarning("  {Tipo} de {Desde:HH:mm} a {Hasta:HH:mm} ({Minutos} min)",
                v.Tipo == DowntimeWindow.TipoNoLlegaron ? "NO LLEGARON AVISOS" : "SIN SERVICIO", v.Desde, v.Hasta, v.Minutos);
    }

    private DateOnly TodayMexico() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Mexico).DateTime);

    private static TimeZoneInfo TryFindMexico()
    {
        foreach (var id in new[] { "Central Standard Time (Mexico)", "America/Mexico_City" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.CreateCustomTimeZone("Mexico", TimeSpan.FromHours(-6), "Mexico", "Mexico");
    }
}
