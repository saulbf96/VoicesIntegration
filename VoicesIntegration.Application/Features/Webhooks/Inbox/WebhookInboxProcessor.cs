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

public sealed record InboxBatchResult(int Processed, int Duplicates, int Rejected, bool TransientFailure, bool Busy = false, string? Error = null);

// Pasa la bandeja a la BD. Duplicado se quita, dato invalido se rechaza, falla de BD se reintenta
public sealed class WebhookInboxProcessor(
    IWebhookInbox inbox,
    IServiceScopeFactory scopeFactory,
    ILogger<WebhookInboxProcessor> logger)
{
    public const int SqlBatchSize = 1000;

    public async Task<InboxBatchResult> ProcessPendingAsync(int batchSize, int maxDegreeOfParallelism, CancellationToken cancellationToken, int sqlParallel = 1,
        int sqlBatchSize = SqlBatchSize)
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
                    var payload = JsonSerializer.Deserialize<SmsWebhookPayload>(raw) ?? throw new DomainException("El aviso está vacío.");
                    if (payload.IsBatch || payload.IsReply) others.Add((item, raw));
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
        long bytes = blaster.Sum(b => (long)b.Raw.Length * 2);

        var lista = blaster.OrderBy(b => b.Item.Id, StringComparer.Ordinal).ToList();
        var detener = 0;
        await Parallel.ForEachAsync(LotesPorLlamada(lista, x => x.Event.Result.MessageId, Math.Max(1, sqlBatchSize)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, sqlParallel), CancellationToken = cancellationToken },
            async (chunk, _) =>
        {
            if (Volatile.Read(ref detener) == 1) { Interlocked.Add(ref c.Transient, chunk.Length); return; }
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IBlasterCallRepository>();
                var t0 = Stopwatch.GetTimestamp();
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
                Volatile.Write(ref detener, 1);
                Interlocked.Add(ref c.Transient, chunk.Length);
                c.Error = ex.Message;
                logger.LogError(ex, "Falla técnica al guardar un lote de {Count} avisos de Blaster; se reintentará.", chunk.Length);
            }
        });
        var tBd = Stopwatch.GetElapsedTime(0, bdTicks);
        var tBorrar = Stopwatch.GetElapsedTime(0, borrarTicks);

        var tSms0 = sw.Elapsed;
        if (!smsMensajes.IsEmpty)
            await GuardarSmsPorLotesAsync(smsMensajes, options, sqlParallel, c, cancellationToken);
        await Parallel.ForEachAsync(others, options, async (x, _) => await ProcessOneAsync(x.Item, x.Raw, c));

        logger.LogInformation(
            "Tiempos del lote: {Total:N1} s = buscar {Buscar:N1} s + leer {Leer:N1} s ({Blaster} blaster, {SmsN} sms, {Otros} otros, {Mb:N1} MB) + BD {Bd:N1} s + borrar {Borrar:N1} s + SMS {Sms:N1} s",
            sw.Elapsed.TotalSeconds, tBuscar.TotalSeconds, (tLeer - tBuscar).TotalSeconds, blaster.Count, smsMensajes.Count, others.Count,
            bytes / 1048576.0, tBd.TotalSeconds, tBorrar.TotalSeconds, (sw.Elapsed - tSms0).TotalSeconds);

        return new InboxBatchResult(c.Processed, c.Duplicates, c.Rejected, c.Transient > 0, c.Busy > 0, c.Error);
    }

    // SMS por lotes con una sola llamada al SP
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
        await Parallel.ForEachAsync(lista.Chunk(SqlBatchSize),
            new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = cancellationToken },
            async (chunk, _) =>
        {
            if (Volatile.Read(ref detener) == 1) { Interlocked.Add(ref c.Transient, chunk.Length); return; }
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<ISmsRepository>();
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
                c.Error = ex.Message;
                logger.LogError(ex, "Falla técnica al guardar un lote de {Count} SMS; se reintentará.", chunk.Length);
            }
        });
    }

    // Arma lotes sin partir los avisos de una misma llamada
    internal static List<T[]> LotesPorLlamada<T>(IReadOnlyList<T> lista, Func<T, string> llave, int tamano)
    {
        var lotes = new List<T[]>();
        var actual = new List<T>(tamano);
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

    private async Task ProcessOneAsync(InboxItem item, string raw, Counters c)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var isNew = await ProcessAsync(item, raw, scope.ServiceProvider);

            inbox.Complete(item);
            if (isNew) Interlocked.Increment(ref c.Processed);
            else Interlocked.Increment(ref c.Duplicates);
        }
        catch (Exception ex) when (HandleItemError(item, ex, c)) { }
    }

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
                Interlocked.Increment(ref c.Busy);
                logger.LogInformation("Aviso {InboxId} ({Channel}) abierto un instante por otro programa (antivirus, indexador de Windows...); se reintenta en 1 s.", item.Id, item.Channel);
                return true;
            default:
                Interlocked.Increment(ref c.Transient);
                c.Error = ex.Message;
                logger.LogError(ex, "Falla técnica al guardar el aviso {InboxId} ({Channel}); se reintentará.", item.Id, item.Channel);
                return true;
        }
    }

    private sealed class Counters
    {
        public int Processed, Duplicates, Rejected, Transient, Busy;
        public volatile string? Error;
    }

    // Archivo en uso por otro proceso
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
