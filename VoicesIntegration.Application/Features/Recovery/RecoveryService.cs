using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Notifications;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Recovery;

/// <summary>
/// Recuperación de llamadas que NO llegaron por webhook (servidor caído, red, etc.). Pensado para archivos de hasta 10 millones:
/// 1) Revisar: lee el archivo en modo LIGERO (llave, sip_code, fechas, a_num), lo carga masivo a una temporal sin índices
///    pesados y compara contra lo guardado. Nada de textos largos en esta fase.
/// 2) Insertar: relee el archivo y toma COMPLETAS solo las que faltan, en lotes de 20 mil, y las manda con carga masiva
///    DIRECTO a la tabla final (3 lotes a la vez). Si un lote choca con un webhook que llegó en ese instante, ese lote va por
///    el camino seguro (tabla de carga + SP que descarta las existentes). Al final completa a_num y limpia lo temporal.
/// Nada se duplica: se puede subir el mismo archivo varias veces.
/// </summary>
public sealed class RecoveryService(IRecoveryRepository repository, IRecoveryFileReader reader, INotificador notificador, IWebhookInbox inbox, ILogger<RecoveryService> logger)
{
    /// <summary>Si la bandeja del webhook pasa de esto, la recuperación COMPARTE: espera <see cref="PausaAlCompartir"/> entre bloques.</summary>
    public const int FrenoPendientesAlto = 20_000;

    /// <summary>
    /// Espera entre bloques cuando la bandeja va atrasada. La recuperación NUNCA se detiene del todo
    /// (con campañas todo el día, esperar a que la bandeja baje dejaba la recuperación parada horas; 2026-11-08).
    /// </summary>
    public static readonly TimeSpan PausaAlCompartir = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Filas por bloque CON CAMPAÑAS ("medio rápido"). El usuario sube reportes a cualquier hora: con bloques de 2,000
    /// y 20 s de pausa un archivo tardaba horas (2026-11-08). Ahora la recuperación ya NO compite con el webhook por la
    /// tabla dentro de una misma transacción (va por tabla de paso + SP 20, y los avisos esperan seguros en la bandeja).
    /// </summary>
    public const int BatchSize = 10_000;

    /// <summary>
    /// MODO RÁPIDO (sin campañas: madrugada / fin del día): con la bandeja del webhook casi vacía, bloques de 20,000 sin pausas.
    /// Se decide BLOQUE POR BLOQUE: una recuperación subida de día acelera sola en cuanto bajan las campañas.
    /// </summary>
    public const int BatchSizeRapido = 20_000;

    /// <summary>Con menos pendientes que esto en la bandeja, la recuperación va en modo rápido.</summary>
    public const int RapidoSiBandejaMenorA = 2_000;

    /// <summary>Pausa entre lotes para que los webhooks (prioridad) entren a la tabla entre uno y otro.</summary>
    public static readonly TimeSpan PausaEntreLotes = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Conexiones mandando lotes a SQL al mismo tiempo. Leer el archivo es ~5 veces más rápido que mandar
    /// filas completas por una sola conexión (medido), así que se mandan 3 en paralelo. Insertar en la tabla
    /// final sigue siendo de UNO en UNO (así nunca se duplica). Bajó de 3 a 2 para dejar aire a los webhooks.
    /// </summary>
    public const int BulkWorkers = 2;

    /// <summary>
    /// Hasta cuántas faltantes se filtran en memoria (≈ 25 bytes c/u como GUID → ~150 MB con 6 millones).
    /// Si faltan más, se toman todas las marcadas del archivo y SQL descarta las que ya existen.
    /// </summary>
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
                TryDelete(path);   // SMS: el reporte ya quedó completo en la temporal, no hace falta releerlo
            }
            else
            {
                await repository.CompareAsync(jobId, ct);
            }
            logger.LogInformation("Recuperación {JobId}: {Total} filas leídas y comparadas en {Segundos:N0} s", jobId, total, sw.Elapsed.TotalSeconds);
            // El archivo SE QUEDA: hace falta para cargar completas las faltantes al insertar.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = ex is DomainException ? ex.Message : "Error al procesar el archivo: " + ex.Message;
            logger.LogError(ex, "Recuperación {JobId} falló al revisar", jobId);
            await SafeAsync(() => repository.CancelAsync(jobId, CancellationToken.None));   // limpia lo temporal
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
            await repository.UpdateAsync(jobId, RecoveryStates.Insertando, null, "Buscando lo que falta...", ct);
            var job = await repository.GetAsync(jobId, ct);
            var faltantes = job?.Faltantes ?? 0;

            if (job?.Canal == RecoveryChannels.Sms)
            {
                // SMS: el reporte ya está completo en la temporal; un SP inserta lo que falta, completa número/texto y limpia.
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

                // Pocas faltantes: se filtran aquí y solo esas viajan a SQL. Muchísimas: viajan todas las marcadas.
                IdentifierSet? only = faltantes <= MaxMissingInMemory ? await repository.GetMissingIdsAsync(jobId, ct) : null;
                logger.LogInformation("Recuperación {JobId}: empieza a insertar (bandeja del webhook: {Pendientes} pendientes; el modo se decide bloque por bloque)",
                    jobId, inbox.GetStatus().Pending);
                await repository.UpdateAsync(jobId, null, null, $"Insertando: 0 de {faltantes:N0}", ct);
                await repository.InsertBatchAsync(jobId, -1, ct);   // borra lotes que hayan quedado de un intento anterior
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    /// <summary>
    /// Línea de producción: 1 lector (archivo → lotes) → 3 cargadores (lotes → tabla de carga, en paralelo)
    /// → 1 insertador (tabla de carga → tabla final, de uno en uno). Todos trabajan a la vez.
    /// </summary>
    private async Task LoadAndInsertAsync(Guid jobId, string path, IdentifierSet? only, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var porCargar = Channel.CreateBounded<(int Lote, List<RecoveryRow> Rows)>(new BoundedChannelOptions(1) { SingleWriter = true });
        var porInsertar = Channel.CreateBounded<int>(new BoundedChannelOptions(BulkWorkers) { SingleReader = true });

        async Task Guard(Func<Task> work)
        {
            try { await work(); }
            catch { await cts.CancelAsync(); throw; }   // si una etapa falla, se detienen todas
        }

        var lector = Task.Run(() => Guard(async () =>
        {
            try
            {
                // Tamaño del bloque decidido BLOQUE POR BLOQUE: con campañas 10,000; con la bandeja vacía 20,000.
                int Tamano() => inbox.GetStatus().Pending < RapidoSiBandejaMenorA ? BatchSizeRapido : BatchSize;
                var tam = Tamano();
                logger.LogInformation("Recuperación {JobId}: modo {Modo}", jobId, tam == BatchSizeRapido ? "RÁPIDO (bloques de 20,000)" : "con campañas (bloques de 10,000)");
                var batch = new List<RecoveryRow>(tam);
                var seenInBatch = new IdentifierSet();   // repetidos dentro del mismo lote (entre lotes los quita SQL)
                var lote = 0;
                // Con la lista de faltantes: cada una se toma UNA sola vez (Remove), aunque el archivo la repita.
                long recorridas = 0;   // medición: filas del archivo revisadas para armar cada lote
                Func<string, bool>? include = only is null ? null : id => { recorridas++; return only.Remove(id); };
                var swLeer = Stopwatch.StartNew();
                long esperaMs = 0;

                await foreach (var row in reader.ReadAsync(path, light: false, include, token))
                {
                    if (row.SipCode is null && row.DialedAt is null && row.FailedAt is null) continue;   // Voices no la marcó
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
                // SIEMPRE por la tabla de carga (no toca la tabla final ni estorba al webhook) y luego el SP 20 dentro del 205.
                // Medido en el 205 (2026-11-08): SqlBulkCopy DIRECTO a TBL_LLAMADAS = 15–115 s por 2,000 (y bloquea al webhook);
                // tabla de paso + INSERT…SELECT = 0.12 s por 2,000.
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
                await EsperarSiElWebhookVaAtrasadoAsync(jobId, token);   // si la bandeja se atrasa, comparte (también en modo rápido)
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
            // Devolver el error REAL (no el "cancelado" de las etapas que se detuvieron por él).
            var real = todas.Concat(cargadores).Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (real is not null) throw real;
            throw;
        }
    }

    /// <summary>
    /// Reparto con el webhook: si la bandeja va atrasada, la recuperación sigue pero deja aire entre bloques.
    /// Nunca se queda parada (el usuario puede subir reportes a cualquier hora y siempre avanzan).
    /// </summary>
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
