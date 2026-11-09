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

    /// <param name="sqlParallel">Lotes de Blaster que se mandan a la BD al mismo tiempo (1 = uno tras otro).</param>
    public async Task<InboxBatchResult> ProcessPendingAsync(int batchSize, int maxDegreeOfParallelism, CancellationToken cancellationToken, int sqlParallel = 1)
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
        var smsMensajes = new ConcurrentBag<(InboxItem Item, string Raw, SmsWebhookPayload Payload)>();

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
                else if (item.Channel == WebhookChannels.Sms)
                {
                    // SMS de un mensaje → por lotes (SP 31). "Resumen de lote" (type = BATCH) → uno por uno (SP 9), como antes.
                    var payload = JsonSerializer.Deserialize<SmsWebhookPayload>(raw) ?? throw new DomainException("El aviso está vacío.");
                    if (payload.IsBatch) others.Add((item, raw));
                    else smsMensajes.Add((item, raw, payload));
                }
                else
                {
                    others.Add((item, raw));
                }
            }
            catch (Exception ex) when (HandleItemError(item, ex, c)) { }
        });

        var tLeer = sw.Elapsed;
        long bdTicks = 0, borrarTicks = 0;
        long bytes = blaster.Sum(b => (long)b.Raw.Length * 2);   // lo que viaja a SQL (UTF-16)

        // 2) Blaster: por lotes, en orden de llegada (sqlParallel lotes a la vez).
        var lista = blaster.OrderBy(b => b.Item.Id, StringComparer.Ordinal).ToList();
        var detener = 0;   // si la BD falla, no mandar más lotes en esta vuelta
        // Todos los avisos de una MISMA llamada van en el MISMO lote: así dos lotes en paralelo nunca compiten por la
        // misma llamada (el SP ya no bloquea rangos; script 33).
        await Parallel.ForEachAsync(LotesPorLlamada(lista, x => x.Event.Result.MessageId, SqlBatchSize),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, sqlParallel), CancellationToken = cancellationToken },
            async (chunk, _) =>
        {
            if (Volatile.Read(ref detener) == 1) { Interlocked.Add(ref c.Transient, chunk.Length); return; }
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IBlasterCallRepository>();
                var t0 = Stopwatch.GetTimestamp();
                // CancellationToken.None: el SP es atómico; no cortar a medias al apagar la API.
                var isNew = await repo.RegisterCallResultsBatchAsync(chunk.Select(x => x.Event).ToList(), CancellationToken.None);
                var t1 = Stopwatch.GetTimestamp();
                Interlocked.Add(ref bdTicks, t1 - t0);

                Parallel.For(0, chunk.Length, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, i =>
                {
                    inbox.Complete(chunk[i].Item);
                    if (isNew[i]) Interlocked.Increment(ref c.Processed);
                    else Interlocked.Increment(ref c.Duplicates);
                });
                Interlocked.Add(ref borrarTicks, Stopwatch.GetTimestamp() - t1);
            }
            catch (BatchRejectedException ex)
            {
                logger.LogWarning("Lote de {Count} avisos rechazado por la BD ({Reason}); se guardan uno por uno.", chunk.Length, ex.Message);
                await Parallel.ForEachAsync(chunk, options, async (x, _) => await ProcessOneAsync(x.Item, x.Raw, c));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Falla técnica (BD caída, timeout, bloqueo mutuo...): TODO el lote se queda en la bandeja.
                Volatile.Write(ref detener, 1);
                Interlocked.Add(ref c.Transient, chunk.Length);
                logger.LogError(ex, "Falla técnica al guardar un lote de {Count} avisos de Blaster; se reintentará.", chunk.Length);
            }
        });
        var tBd = Stopwatch.GetElapsedTime(0, bdTicks);
        var tBorrar = Stopwatch.GetElapsedTime(0, borrarTicks);

        // 3) SMS de un mensaje: POR LOTES (1 transacción por lote; antes 1 por SMS). Luego "resumen de lote" y otros: uno por uno.
        var tSms0 = sw.Elapsed;
        if (!smsMensajes.IsEmpty)
            await GuardarSmsPorLotesAsync(smsMensajes, options, sqlParallel, c, cancellationToken);
        await Parallel.ForEachAsync(others, options, async (x, _) => await ProcessOneAsync(x.Item, x.Raw, c));

        // Dónde se va el tiempo (para saber qué frena: disco/antivirus, red o BD).
        logger.LogInformation(
            "Tiempos del lote: {Total:N1} s = buscar {Buscar:N1} s + leer {Leer:N1} s ({Blaster} blaster, {SmsN} sms, {Otros} otros, {Mb:N1} MB) + BD {Bd:N1} s + borrar {Borrar:N1} s + SMS {Sms:N1} s",
            sw.Elapsed.TotalSeconds, tBuscar.TotalSeconds, (tLeer - tBuscar).TotalSeconds, blaster.Count, smsMensajes.Count, others.Count,
            bytes / 1048576.0, tBd.TotalSeconds, tBorrar.TotalSeconds, (sw.Elapsed - tSms0).TotalSeconds);

        return new InboxBatchResult(c.Processed, c.Duplicates, c.Rejected, c.Transient > 0, c.Busy > 0);
    }

    /// <summary>
    /// SMS de un mensaje por lotes de <see cref="SqlBatchSize"/>: se validan (y se consulta el detalle si está prendido)
    /// en paralelo, y se guardan con UNA llamada a la BD por lote, en orden de llegada.
    /// Si la BD rechaza un lote por un dato (o falta el script 31), ese lote se guarda uno por uno como antes.
    /// </summary>
    private async Task GuardarSmsPorLotesAsync(
        IEnumerable<(InboxItem Item, string Raw, SmsWebhookPayload Payload)> mensajes,
        ParallelOptions options, int sqlParallel, Counters c, CancellationToken cancellationToken)
    {
        var listos = new ConcurrentBag<(InboxItem Item, string Raw, SmsToSave Sms)>();
        await using (var prep = scopeFactory.CreateAsyncScope())
        {
            var handler = prep.ServiceProvider.GetRequiredService<RegisterSmsEventHandler>();
            await Parallel.ForEachAsync(mensajes, options, async (x, ct) =>
            {
                try { listos.Add((x.Item, x.Raw, await handler.PrepareAsync(x.Payload, x.Raw, x.Item.ReceivedAtUtc, ct))); }
                catch (Exception ex) when (HandleItemError(x.Item, ex, c)) { }
            });
        }

        var lista = listos.OrderBy(x => x.Item.ReceivedAtUtc).ThenBy(x => x.Item.Id, StringComparer.Ordinal).ToList();
        var detener = 0;
        // UNO TRAS OTRO: varios lotes de SMS a la vez se bloqueaban entre sí (deadlock 1205; 2026-11-08 13:16).
        await Parallel.ForEachAsync(lista.Chunk(SqlBatchSize),
            new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = cancellationToken },
            async (chunk, _) =>
        {
            if (Volatile.Read(ref detener) == 1) { Interlocked.Add(ref c.Transient, chunk.Length); return; }
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<ISmsRepository>();
                // CancellationToken.None: el SP es atómico; no cortar a medias al apagar la API.
                var isNew = await repo.RegisterBatchAsync(chunk.Select(x => x.Sms).ToList(), CancellationToken.None);
                for (var i = 0; i < chunk.Length; i++)
                {
                    inbox.Complete(chunk[i].Item);
                    if (isNew[i]) Interlocked.Increment(ref c.Processed);
                    else Interlocked.Increment(ref c.Duplicates);
                }
            }
            catch (BatchRejectedException ex)
            {
                logger.LogWarning("Lote de {Count} SMS rechazado por la BD ({Reason}); se guardan uno por uno.", chunk.Length, ex.Message);
                await Parallel.ForEachAsync(chunk, options, async (x, _) => await ProcessOneAsync(x.Item, x.Raw, c));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Volatile.Write(ref detener, 1);
                Interlocked.Add(ref c.Transient, chunk.Length);
                logger.LogError(ex, "Falla técnica al guardar un lote de {Count} SMS; se reintentará.", chunk.Length);
            }
        });
    }

    /// <summary>
    /// Arma lotes de ~<paramref name="tamano"/> sin partir una llamada: todos los avisos con la misma llave quedan juntos
    /// (en su orden de llegada). Las llamadas van en el orden en que apareció su primer aviso.
    /// </summary>
    internal static List<T[]> LotesPorLlamada<T>(IReadOnlyList<T> lista, Func<T, string> llave, int tamano)
    {
        var lotes = new List<T[]>();
        var actual = new List<T>(tamano);
        // Sin distinguir mayúsculas ni espacios al final: así compara SQL (collation CI) la llave única.
        foreach (var grupo in lista.GroupBy(x => (llave(x) ?? string.Empty).TrimEnd(), StringComparer.OrdinalIgnoreCase))
        {
            var avisos = grupo.ToList();
            if (actual.Count > 0 && actual.Count + avisos.Count > tamano)
            {
                lotes.Add(actual.ToArray());
                actual.Clear();
            }
            actual.AddRange(avisos);
        }
        if (actual.Count > 0) lotes.Add(actual.ToArray());
        return lotes;
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
