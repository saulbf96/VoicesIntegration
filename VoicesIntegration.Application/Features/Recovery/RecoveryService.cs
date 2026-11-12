using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Notifications;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Recovery;

// Recupera llamadas que no llegaron por webhook: revisar y luego insertar faltantes
public sealed class RecoveryService(IRecoveryRepository repository, IRecoveryFileReader reader, INotificador notificador, IWebhookInbox inbox, ILogger<RecoveryService> logger)
{
    public const int FrenoPendientesAlto = 20_000;

    // Pausa entre bloques cuando la bandeja va atrasada
    public static readonly TimeSpan PausaAlCompartir = TimeSpan.FromSeconds(5);

    public const int BatchSize = 10_000;

    public const int BatchSizeRapido = 20_000;

    public const int RapidoSiBandejaMenorA = 2_000;

    // Deja pasar los webhooks entre lote y lote
    public static readonly TimeSpan PausaEntreLotes = TimeSpan.FromMilliseconds(50);

    public const int BulkWorkers = 2;

    public const int MaxMissingInMemory = 6_000_000;

    public async Task ReviewAsync(Guid jobId, string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await repository.UpdateAsync(jobId, RecoveryStates.Leyendo, 0, "Leyendo el archivo...", ct);
            var esSms = (await repository.GetAsync(jobId, ct))?.Canal == RecoveryChannels.Sms;

            var total = esSms
                ? await repository.BulkLoadSmsAsync(jobId, reader.ReadSmsAsync(path, ct), read => repository.UpdateAsync(jobId, null, read, null, ct), ct)
                : await repository.BulkLoadLightAsync(jobId, reader.ReadAsync(path, light: true, include: null, ct),
                    read => repository.UpdateAsync(jobId, null, read, null, ct), ct);

            if (total == 0)
                throw new DomainException("El archivo no tiene registros.");

            await repository.UpdateAsync(jobId, null, total, "Comparando contra la base de datos...", ct);
            if (esSms)
            {
                await repository.CompareSmsAsync(jobId, ct);
                TryDelete(path);
            }
            else
            {
                await repository.CompareAsync(jobId, ct);
            }
            logger.LogInformation("Recuperación {JobId}: {Total} filas leídas y comparadas en {Segundos:N0} s", jobId, total, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            var msg = ex is DomainException ? ex.Message : "Error al procesar el archivo: " + ex.Message;
            logger.LogError(ex, "Recuperación {JobId} falló al revisar", jobId);
            await SafeAsync(() => repository.CancelAsync(jobId, CancellationToken.None));
            await SafeAsync(() => repository.UpdateAsync(jobId, RecoveryStates.Error, null, Truncate(msg), CancellationToken.None));
            TryDelete(path);
            await AvisarAsync(jobId, "Recuperación con ERROR al revisar", msg);
        }
    }

    public async Task InsertAsync(Guid jobId, string? path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var job = await repository.GetAsync(jobId, ct);
            if (job?.Estado == RecoveryStates.Cancelado) return;
            if (job is not null && RecoveryDayRule.WhyNot(job, inbox.GetStatus(), DateTimeOffset.UtcNow) is { } motivo)
                throw new DomainException(motivo);
            await repository.UpdateAsync(jobId, RecoveryStates.Insertando, null, "Buscando lo que falta...", ct);
            var faltantes = job?.Faltantes ?? 0;

            if (job?.Canal == RecoveryChannels.Sms)
            {
                await repository.InsertSmsAsync(jobId, ct);
                job = await repository.GetAsync(jobId, ct);
                logger.LogInformation("Recuperación SMS {JobId}: {Insertados} insertados, {Completados} completados, en {Segundos:N0} s",
                    jobId, job?.Insertadas, job?.ANumActualizados, sw.Elapsed.TotalSeconds);
                if (path is not null) TryDelete(path);
                await AvisarAsync(jobId, $"Recuperación de SMS terminada: {job?.Insertadas:N0} mensajes insertados",
                    $"Archivo: {job?.Archivo}\n" +
                    $"Leídos: {job?.FilasLeidas:N0} · Ya estaban: {job?.YaExistian:N0} · Insertados: {job?.Insertadas:N0} · " +
                    $"Completados (número/texto): {job?.ANumActualizados:N0} · Sin estado: {job?.FilasSinMarcar:N0}\n" +
                    $"Fechas del archivo: {job?.Desde:dd/MM/yyyy HH:mm} a {job?.Hasta:dd/MM/yyyy HH:mm}");
                return;
            }

            if (faltantes > 0)
            {
                if (path is null || !File.Exists(path))
                    throw new DomainException("Ya no está el archivo en el servidor (se borra a los 2 días). Vuelve a subirlo.");

                IdentifierSet? only = faltantes <= MaxMissingInMemory ? await repository.GetMissingIdsAsync(jobId, ct) : null;
                logger.LogInformation("Recuperación {JobId}: empieza a insertar (bandeja del webhook: {Pendientes} pendientes; el modo se decide bloque por bloque)",
                    jobId, inbox.GetStatus().Pending);
                await repository.UpdateAsync(jobId, null, null, $"Insertando: 0 de {faltantes:N0}", ct);
                await repository.InsertBatchAsync(jobId, -1, ct);
                await LoadAndInsertAsync(jobId, path, only, ct);
            }

            await repository.UpdateAsync(jobId, null, null, "Completando a_num y limpiando...", ct);
            await repository.InsertMissingAsync(jobId, ct);
            job = await repository.GetAsync(jobId, ct);
            logger.LogInformation("Recuperación {JobId}: {Insertadas} insertadas, a_num completado en {ANum}, en {Segundos:N0} s",
                jobId, job?.Insertadas, job?.ANumActualizados, sw.Elapsed.TotalSeconds);
            if (path is not null) TryDelete(path);
            await AvisarAsync(jobId, $"Recuperación terminada: {job?.Insertadas:N0} llamadas insertadas",
                $"Archivo: {job?.Archivo}\n" +
                $"Leídas: {job?.FilasLeidas:N0} · Ya estaban: {job?.YaExistian:N0} · " +
                $"Insertadas: {job?.Insertadas:N0} · Sin marcar por Voices: {job?.FilasSinMarcar:N0}\n" +
                $"Rango del archivo: {job?.Desde:dd/MM/yyyy HH:mm} a {job?.Hasta:HH:mm} · Tiempo de inserción: {(int)sw.Elapsed.TotalMinutes} min {sw.Elapsed.Seconds} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Recuperación {JobId} falló al insertar", jobId);
            var msg = ex is DomainException ? ex.Message : "Error al insertar (se puede reintentar): " + ex.Message;
            await SafeAsync(() => repository.UpdateAsync(jobId, RecoveryStates.Error, null, Truncate(msg), CancellationToken.None));
            await AvisarAsync(jobId, "Recuperación con ERROR al insertar", msg + "\nSe puede reintentar con \"Insertar faltantes\": no se duplica nada.");
        }
    }

    private async Task AvisarAsync(Guid jobId, string asunto, string cuerpo)
    {
        try { await notificador.EnviarAsync(asunto, cuerpo + $"\n\nRecuperación: {jobId}", CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning("No se pudo avisar por correo: {Error}", ex.Message); }
    }

    // Lector -> cargadores en paralelo -> insertador
    private async Task LoadAndInsertAsync(Guid jobId, string path, IdentifierSet? only, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var porCargar = Channel.CreateBounded<(int Lote, List<RecoveryRow> Rows)>(new BoundedChannelOptions(1) { SingleWriter = true });
        var porInsertar = Channel.CreateBounded<int>(new BoundedChannelOptions(BulkWorkers) { SingleReader = true });

        async Task Guard(Func<Task> work)
        {
            try { await work(); }
            catch { await cts.CancelAsync(); throw; }
        }

        var lector = Task.Run(() => Guard(async () =>
        {
            try
            {
                int Tamano() => inbox.GetStatus().Pending < RapidoSiBandejaMenorA ? BatchSizeRapido : BatchSize;
                var tam = Tamano();
                logger.LogInformation("Recuperación {JobId}: modo {Modo}", jobId, tam == BatchSizeRapido ? "RÁPIDO (bloques de 20,000)" : "con campañas (bloques de 10,000)");
                var batch = new List<RecoveryRow>(tam);
                var seenInBatch = new IdentifierSet();
                var lote = 0;
                long recorridas = 0;
                Func<string, bool>? include = only is null ? null : id => { recorridas++; return only.Remove(id); };
                var swLeer = Stopwatch.StartNew();
                long esperaMs = 0;

                await foreach (var row in reader.ReadAsync(path, light: false, include, token))
                {
                    if (row.SipCode is null && row.DialedAt is null && row.FailedAt is null) continue;
                    if (!seenInBatch.Add(row.Identifier)) continue;
                    batch.Add(row);
                    if (batch.Count < tam) continue;
                    var leerMs = swLeer.ElapsedMilliseconds;
                    await porCargar.Writer.WriteAsync((lote++, batch), token);
                    esperaMs = swLeer.ElapsedMilliseconds - leerMs;
                    logger.LogInformation(
                        "[RECOVERY LEER] Lote={Lote} Filas={Filas} RecorridasDelArchivo={Recorridas} Leer={Leer}ms EsperaACargadores={Espera}ms",
                        lote, batch.Count, recorridas, leerMs, esperaMs);
                    recorridas = 0;
                    swLeer.Restart();
                    var antes = tam;
                    tam = Tamano();
                    if (tam != antes)
                        logger.LogInformation("Recuperación {JobId}: cambia a modo {Modo}", jobId,
                            tam == BatchSizeRapido ? "RÁPIDO (bloques de 20,000: la bandeja está vacía)" : "con campañas (bloques de 10,000)");
                    batch = new List<RecoveryRow>(tam);
                    seenInBatch.Clear();
                }
                if (batch.Count > 0) await porCargar.Writer.WriteAsync((lote, batch), token);
            }
            finally { porCargar.Writer.TryComplete(); }
        }), token);

        var cargadores = Enumerable.Range(0, BulkWorkers).Select(_ => Task.Run(() => Guard(async () =>
        {
            await foreach (var (lote, rows) in porCargar.Reader.ReadAllAsync(token))
            {
                if (only is not null)
                {
                    await EsperarSiElWebhookVaAtrasadoAsync(jobId, token);
                    if (await repository.BulkInsertFinalAsync(jobId, rows, token))
                    {
                        await repository.AddInsertedAsync(jobId, rows.Count, token);
                        if (inbox.GetStatus().Pending >= RapidoSiBandejaMenorA) await Task.Delay(PausaEntreLotes, token);
                        continue;
                    }
                }
                await repository.BulkLoadBatchAsync(jobId, lote, rows, token);
                await porInsertar.Writer.WriteAsync(lote, token);
            }
        }), token)).ToArray();

        var cierre = Task.Run(async () =>
        {
            try { await Task.WhenAll(cargadores); } finally { porInsertar.Writer.TryComplete(); }
        }, CancellationToken.None);

        var insertador = Task.Run(() => Guard(async () =>
        {
            await foreach (var lote in porInsertar.Reader.ReadAllAsync(token))
            {
                await EsperarSiElWebhookVaAtrasadoAsync(jobId, token);
                await repository.InsertBatchAsync(jobId, lote, token);
                if (inbox.GetStatus().Pending >= RapidoSiBandejaMenorA) await Task.Delay(PausaEntreLotes, token);
            }
        }), token);

        var todas = new[] { lector, cierre, insertador };
        try
        {
            await Task.WhenAll(todas);
        }
        catch
        {
            var real = todas.Concat(cargadores).Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (real is not null) throw real;
            throw;
        }
    }

    // El webhook tiene prioridad sobre la recuperacion
    private async Task EsperarSiElWebhookVaAtrasadoAsync(Guid jobId, CancellationToken ct)
    {
        var pendientes = inbox.GetStatus().Pending;
        if (pendientes <= FrenoPendientesAlto) return;

        logger.LogInformation("Recuperación {JobId} compartiendo: el webhook tiene {Pendientes} pendientes; espera {Segundos} s entre bloques",
            jobId, pendientes, (int)PausaAlCompartir.TotalSeconds);
        await Task.Delay(PausaAlCompartir, ct);
    }

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];

    public static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { logger.LogWarning("No se pudo actualizar la bitácora: {Error}", ex.Message); }
    }
}
