using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Application.Features.Webhooks.Sms;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Webhooks.Inbox;

/// <summary>Resultado de procesar un lote de la bandeja.</summary>
/// <param name="TransientFailure">Falló la BD (u otra falla técnica): esperar cada vez más antes de reintentar.</param>
/// <param name="Busy">Algún archivo estaba abierto por otro programa (antivirus): reintentar en 1 s, la BD está bien.</param>
public sealed record InboxBatchResult(int Processed, int Duplicates, int Rejected, bool TransientFailure, bool Busy = false);

/// <summary>
/// Pasa los avisos de la bandeja a la BD, según su canal (Blaster o SMS).
/// Reglas para NO perder datos:
/// - Éxito o duplicado      → se quita de la bandeja.
/// - Dato inválido          → "rechazados" con el motivo (reintentar no lo arreglaría).
/// - Falla técnica (BD...)  → se queda en la bandeja y se reintenta en el siguiente ciclo.
/// </summary>
public sealed class WebhookInboxProcessor(
    IWebhookInbox inbox,
    IServiceScopeFactory scopeFactory,
    ILogger<WebhookInboxProcessor> logger)
{
    public async Task<InboxBatchResult> ProcessPendingAsync(int batchSize, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        var items = inbox.GetPending(batchSize);
        if (items.Count == 0)
            return new InboxBatchResult(0, 0, 0, false);

        int processed = 0, duplicates = 0, rejected = 0, transient = 0, busy = 0;

        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                try
                {
                    var raw = await inbox.ReadAsync(item, ct);

                    // Un scope por aviso: repositorio y conexión propios (seguro en paralelo).
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var isNew = await ProcessAsync(item, raw, scope.ServiceProvider);

                    inbox.Complete(item);
                    if (isNew) Interlocked.Increment(ref processed);
                    else Interlocked.Increment(ref duplicates);
                }
                catch (Exception ex) when (ex is DomainException or JsonException)
                {
                    inbox.Reject(item, ex.Message);
                    Interlocked.Increment(ref rejected);
                    logger.LogWarning("Aviso {InboxId} ({Channel}) rechazado: {Reason}", item.Id, item.Channel, ex.Message);
                }
                catch (IOException ex) when (IsFileInUse(ex))
                {
                    // Otro programa (normalmente el antivirus revisando el archivo nuevo) lo tiene abierto un instante.
                    // No es falla de la BD: el archivo se queda y se reintenta en 1 s.
                    Interlocked.Increment(ref busy);
                    logger.LogInformation("Aviso {InboxId} ({Channel}) abierto un instante por otro programa (antivirus, indexador de Windows...); se reintenta en 1 s.", item.Id, item.Channel);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Falla técnica: el archivo SE QUEDA en la bandeja para reintentarlo.
                    Interlocked.Increment(ref transient);
                    logger.LogError(ex, "Falla técnica al guardar el aviso {InboxId} ({Channel}); se reintentará.", item.Id, item.Channel);
                }
            });

        return new InboxBatchResult(processed, duplicates, rejected, transient > 0, busy > 0);
    }

    /// <summary>Error de Windows "el archivo está en uso por otro proceso" (ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION).</summary>
    private static bool IsFileInUse(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static async Task<bool> ProcessAsync(InboxItem item, string raw, IServiceProvider services)
    {
        switch (item.Channel)
        {
            case WebhookChannels.Sms:
            {
                var payload = JsonSerializer.Deserialize<SmsWebhookPayload>(raw) ?? throw new DomainException("El aviso está vacío.");
                var handler = services.GetRequiredService<RegisterSmsEventHandler>();
                return await handler.HandleAsync(payload, raw, item.ReceivedAtUtc, CancellationToken.None);
            }
            case WebhookChannels.Blaster:
            {
                var payload = JsonSerializer.Deserialize<CallResultPayload>(raw) ?? throw new DomainException("El aviso está vacío.");
                var handler = services.GetRequiredService<RegisterCallResultHandler>();
                return await handler.HandleAsync(payload, raw, item.ReceivedAtUtc, CancellationToken.None)
                       == RegisterCallResultOutcome.Registered;
            }
            default:
                throw new DomainException($"Canal desconocido: '{item.Channel}'.");
        }
    }
}
