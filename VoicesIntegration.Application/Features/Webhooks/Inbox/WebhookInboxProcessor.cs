using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Persistence;
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
///
/// Blaster se guarda POR LOTES (<see cref="SqlBatchSize"/> avisos en UNA llamada a la BD): uno por uno
/// tardaba ~380 ms por aviso desde el .14 (~40 avisos/s) y la bandeja no alcanzaba a Voices (2026-11-07).
/// Si la BD rechaza un lote por un dato (o aún no existe el SP del lote), ese lote se guarda uno por uno.
/// SMS sigue uno por uno (es mucho menos volumen y consulta a Voices el detalle de cada mensaje).
/// </summary>
public sealed class WebhookInboxProcessor(
    IWebhookInbox inbox,
    IServiceScopeFactory scopeFactory,
    ILogger<WebhookInboxProcessor> logger)
{
    /// <summary>Avisos de Blaster por llamada a la BD.</summary>
    public const int SqlBatchSize = 1000;

    public async Task<InboxBatchResult> ProcessPendingAsync(int batchSize, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var items = inbox.GetPending(batchSize);
        var tBuscar = sw.Elapsed;
        if (items.Count == 0)
            return new InboxBatchResult(0, 0, 0, false);

        var c = new Counters();
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken };
        var blaster = new ConcurrentBag<(InboxItem Item, string Raw, CallEventToSave Event)>();
        var others = new ConcurrentBag<(InboxItem Item, string Raw)>();

        // 1) Leer y validar (sin tocar la BD), en paralelo.
        await Parallel.ForEachAsync(items, options, async (item, ct) =>
        {
            try
            {
                var raw = await inbox.ReadAsync(item, ct);
                if (item.Channel == WebhookChannels.Blaster)
                {
                    var payload = JsonSerializer.Deserialize<CallResultPayload>(raw) ?? throw new DomainException("El aviso está vacío.");
                    blaster.Add((item, raw, RegisterCallResultHandler.Prepare(payload, raw, item.ReceivedAtUtc)));
                }
                else
                {
                    others.Add((item, raw));
                }
            }
            catch (Exception ex) when (HandleItemError(item, ex, c)) { }
        });

        var tLeer = sw.Elapsed;
        TimeSpan tBd = TimeSpan.Zero, tBorrar = TimeSpan.Zero;
        long bytes = blaster.Sum(b => (long)b.Raw.Length * 2);   // lo que viaja a SQL (UTF-16)

        // 2) Blaster: por lotes, en orden de llegada.
        var lista = blaster.OrderBy(b => b.Item.Id, StringComparer.Ordinal).ToList();
        foreach (var chunk in lista.Chunk(SqlBatchSize))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IBlasterCallRepository>();
                // CancellationToken.None: el SP es atómico; no cortar a medias al apagar la API.
                var t0 = sw.Elapsed;
                var isNew = await repo.RegisterCallResultsBatchAsync(chunk.Select(x => x.Event).ToList(), CancellationToken.None);
                var t1 = sw.Elapsed;
                tBd += t1 - t0;

                Parallel.For(0, chunk.Length, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, i =>
                {
                    inbox.Complete(chunk[i].Item);
                    if (isNew[i]) Interlocked.Increment(ref c.Processed);
                    else Interlocked.Increment(ref c.Duplicates);
                });
                tBorrar += sw.Elapsed - t1;
            }
            catch (BatchRejectedException ex)
            {
                logger.LogWarning("Lote de {Count} avisos rechazado por la BD ({Reason}); se guardan uno por uno.", chunk.Length, ex.Message);
                await Parallel.ForEachAsync(chunk, options, async (x, _) => await ProcessOneAsync(x.Item, x.Raw, c));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Falla técnica (BD caída, timeout...): TODO el lote se queda en la bandeja. No seguir con más lotes.
                Interlocked.Add(ref c.Transient, chunk.Length);
                logger.LogError(ex, "Falla técnica al guardar un lote de {Count} avisos de Blaster; se reintentará.", chunk.Length);
                break;
            }
        }

        // 3) SMS (y cualquier otro canal): uno por uno, como siempre.
        var tSms0 = sw.Elapsed;
        await Parallel.ForEachAsync(others, options, async (x, _) => await ProcessOneAsync(x.Item, x.Raw, c));

        // Dónde se va el tiempo (para saber qué frena: disco/antivirus, red o BD).
        logger.LogInformation(
            "Tiempos del lote: {Total:N1} s = buscar {Buscar:N1} s + leer {Leer:N1} s ({Blaster} blaster, {Otros} otros, {Mb:N1} MB) + BD {Bd:N1} s + borrar {Borrar:N1} s + SMS {Sms:N1} s",
            sw.Elapsed.TotalSeconds, tBuscar.TotalSeconds, (tLeer - tBuscar).TotalSeconds, blaster.Count, others.Count,
            bytes / 1048576.0, tBd.TotalSeconds, tBorrar.TotalSeconds, (sw.Elapsed - tSms0).TotalSeconds);

        return new InboxBatchResult(c.Processed, c.Duplicates, c.Rejected, c.Transient > 0, c.Busy > 0);
    }

    /// <summary>Guarda UN aviso (SMS, o Blaster cuando su lote fue rechazado).</summary>
    private async Task ProcessOneAsync(InboxItem item, string raw, Counters c)
    {
        try
        {
            // Un scope por aviso: repositorio y conexión propios (seguro en paralelo).
            await using var scope = scopeFactory.CreateAsyncScope();
            var isNew = await ProcessAsync(item, raw, scope.ServiceProvider);

            inbox.Complete(item);
            if (isNew) Interlocked.Increment(ref c.Processed);
            else Interlocked.Increment(ref c.Duplicates);
        }
        catch (Exception ex) when (HandleItemError(item, ex, c)) { }
    }

    /// <summary>Clasifica el error de un aviso. Devuelve false solo para la cancelación (que debe subir).</summary>
    private bool HandleItemError(InboxItem item, Exception ex, Counters c)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return false;
            case DomainException or JsonException:
                inbox.Reject(item, ex.Message);
                Interlocked.Increment(ref c.Rejected);
                logger.LogWarning("Aviso {InboxId} ({Channel}) rechazado: {Reason}", item.Id, item.Channel, ex.Message);
                return true;
            case IOException io when IsFileInUse(io):
                // Otro programa (normalmente el antivirus revisando el archivo nuevo) lo tiene abierto un instante.
                Interlocked.Increment(ref c.Busy);
                logger.LogInformation("Aviso {InboxId} ({Channel}) abierto un instante por otro programa (antivirus, indexador de Windows...); se reintenta en 1 s.", item.Id, item.Channel);
                return true;
            default:
                // Falla técnica: el archivo SE QUEDA en la bandeja para reintentarlo.
                Interlocked.Increment(ref c.Transient);
                logger.LogError(ex, "Falla técnica al guardar el aviso {InboxId} ({Channel}); se reintentará.", item.Id, item.Channel);
                return true;
        }
    }

    private sealed class Counters
    {
        public int Processed, Duplicates, Rejected, Transient, Busy;
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
