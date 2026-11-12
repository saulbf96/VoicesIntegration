using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Monitoring;
using VoicesIntegration.Application.Features.Webhooks.Inbox;

namespace VoicesIntegration.Infrastructure.Inbox;

// Vacia la bandeja a la BD; si falla espera cada vez mas
internal sealed class WebhookInboxWorker(
    IWebhookInbox inbox,
    WebhookInboxProcessor processor,
    ReceptionTracker tracker,
    IOptions<InboxOptions> options,
    ILogger<WebhookInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var retryDelay = TimeSpan.FromSeconds(1);
        logger.LogInformation("Bandeja de webhooks iniciada en {Path} (pendientes al arrancar: {Pending})",
            o.Path, inbox.GetStatus().Pending);

        var nextCleanup = DateTime.UtcNow;

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

                var r = await processor.ProcessPendingAsync(o.BatchSize, o.MaxDegreeOfParallelism, stoppingToken, o.SqlParallel, o.SqlBatchSize);
                tracker.MarkSaved(r.Processed + r.Duplicates);
                if (r.Error is not null) tracker.MarkError(r.Error);

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
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                if (r.Processed + r.Duplicates + r.Rejected < o.BatchSize)
                    await inbox.WaitForItemsAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error inesperado en la bandeja; continúa en 5 s");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("Bandeja detenida (pendientes: {Pending}; se procesan al volver a arrancar)", inbox.GetStatus().Pending);
    }
}
