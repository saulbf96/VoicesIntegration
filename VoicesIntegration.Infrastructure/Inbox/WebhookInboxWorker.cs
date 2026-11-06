using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Features.Webhooks.Inbox;

namespace VoicesIntegration.Infrastructure.Inbox;

/// <summary>
/// Proceso en segundo plano: vacía la bandeja hacia la BD.
/// - Despierta en cuanto llega un aviso (o cada 5 s como respaldo).
/// - Si la BD falla, espera cada vez más (1, 2, 4... hasta MaxRetryDelaySeconds) y reintenta.
/// - Escribe un resumen por lote en el log (no una línea por aviso).
/// </summary>
internal sealed class WebhookInboxWorker(
    IWebhookInbox inbox,
    WebhookInboxProcessor processor,
    IOptions<InboxOptions> options,
    ILogger<WebhookInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var retryDelay = TimeSpan.FromSeconds(1);
        logger.LogInformation("Bandeja de webhooks iniciada en {Path} (pendientes al arrancar: {Pending})",
            o.Path, inbox.GetStatus().Pending);

        var nextCleanup = DateTime.UtcNow;   // limpia al arrancar y luego cada hora

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextCleanup)
                {
                    var borrados = inbox.Cleanup(TimeSpan.FromDays(o.RejectedRetentionDays), TimeSpan.FromHours(1));
                    if (borrados > 0)
                        logger.LogInformation("Limpieza de bandeja: {Count} archivos viejos borrados (rechazados > {Days} días o temporales abandonados)",
                            borrados, o.RejectedRetentionDays);
                    nextCleanup = DateTime.UtcNow.AddHours(1);
                }

                var r = await processor.ProcessPendingAsync(o.BatchSize, o.MaxDegreeOfParallelism, stoppingToken);

                if (r.Processed + r.Duplicates + r.Rejected > 0)
                    logger.LogInformation("Lote: {Processed} guardados, {Duplicates} duplicados, {Rejected} rechazados",
                        r.Processed, r.Duplicates, r.Rejected);

                if (r.TransientFailure)
                {
                    logger.LogWarning("La BD no respondió bien; reintento en {Delay} s", (int)retryDelay.TotalSeconds);
                    await Task.Delay(retryDelay, stoppingToken);
                    retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, o.MaxRetryDelaySeconds));
                    continue;
                }

                retryDelay = TimeSpan.FromSeconds(1);

                if (r.Busy)
                {
                    // Un archivo estaba abierto por otro programa (antivirus): la BD está bien, reintentar pronto.
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                // Si el lote vino lleno, hay más: seguir sin esperar.
                if (r.Processed + r.Duplicates + r.Rejected < o.BatchSize)
                    await inbox.WaitForItemsAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nunca dejar morir el proceso: registrar y seguir.
                logger.LogError(ex, "Error inesperado en la bandeja; continúa en 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("Bandeja detenida (pendientes: {Pending}; se procesan al volver a arrancar)", inbox.GetStatus().Pending);
    }
}
