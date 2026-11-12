using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Application.Features.Recovery;

namespace VoicesIntegration.Infrastructure.Recovery;

public sealed record RecoveryWork(Guid JobId, bool Insert, string? FilePath);

// Una recuperacion a la vez
public sealed class RecoveryQueue
{
    private readonly Channel<RecoveryWork> _channel = Channel.CreateUnbounded<RecoveryWork>();
    public ValueTask EnqueueAsync(RecoveryWork work) => _channel.Writer.WriteAsync(work);
    internal ChannelReader<RecoveryWork> Reader => _channel.Reader;
}

// Permite cancelar desde la pantalla el trabajo que esta corriendo
public sealed class RecoveryCancellations
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    internal CancellationTokenSource Start(Guid jobId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[jobId] = cts;
        return cts;
    }

    internal void End(Guid jobId) => _running.TryRemove(jobId, out _);

    public bool Cancel(Guid jobId)
    {
        if (!_running.TryGetValue(jobId, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }
}

// Procesa la recuperacion en segundo plano
internal sealed class RecoveryWorker(
    RecoveryQueue queue,
    RecoveryCancellations cancellations,
    IServiceScopeFactory scopeFactory,
    IOptions<RecoveryOptions> options,
    ILogger<RecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.Value.Path);
        await ResumeAsync(stoppingToken);
        var nextCleanup = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextCleanup)
                {
                    await CleanupAsync(stoppingToken);
                    nextCleanup = DateTime.UtcNow.AddHours(1);
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromMinutes(5));
                RecoveryWork work;
                try { work = await queue.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { continue; }

                await ProcessAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error inesperado en la recuperación; continúa");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(RecoveryWork work, CancellationToken stoppingToken)
    {
        using var cts = cancellations.Start(work.JobId, stoppingToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<RecoveryService>();
            if (work.Insert) await service.InsertAsync(work.JobId, work.FilePath ?? FindFile(work.JobId), cts.Token);
            else await service.ReviewAsync(work.JobId, work.FilePath!, cts.Token);
        }
        catch (Exception) when (cts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            await MarkCancelledAsync(work, stoppingToken);
        }
        finally
        {
            cancellations.End(work.JobId);
        }
        if (!stoppingToken.IsCancellationRequested) await EnsureNotStuckAsync(work, stoppingToken);
    }

    // Si la BD no dejo guardar el error, el trabajo se quedaria "en cola" para siempre
    private async Task EnsureNotStuckAsync(RecoveryWork work, CancellationToken ct)
    {
        for (var intento = 1; intento <= 3; intento++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IRecoveryRepository>();
                var estado = (await repo.GetAsync(work.JobId, ct))?.Estado;
                if (work.Insert && estado == RecoveryStates.PorInsertar)
                    await repo.UpdateAsync(work.JobId, RecoveryStates.Error, null,
                        "No pudo empezar a insertar (la base de datos no respondió). Da \"Insertar faltantes\" otra vez; no se duplica nada.", ct);
                else if (!work.Insert && estado is RecoveryStates.Subido or RecoveryStates.Leyendo)
                    await repo.UpdateAsync(work.JobId, RecoveryStates.Error, null,
                        "No se pudo leer el archivo (la base de datos no respondió). Vuelve a subirlo.", ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Recuperación {JobId}: no se pudo revisar su estado final (intento {Intento}): {Error}", work.JobId, intento, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    // Lo ya insertado se queda; solo se limpia lo temporal
    private async Task MarkCancelledAsync(RecoveryWork work, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecoveryRepository>();
        await repo.CancelAsync(work.JobId, ct);
        var job = await repo.GetAsync(work.JobId, ct);
        var msg = work.Insert
            ? $"Cancelado: se insertaron {job?.Insertadas ?? 0:N0} de {job?.Faltantes ?? 0:N0}."
            : "Cancelado al revisar. No se guardó nada.";
        await repo.UpdateAsync(work.JobId, null, null, msg, ct);
        RecoveryService.TryDelete(FindFile(work.JobId));
        logger.LogInformation("Recuperación {JobId} cancelada desde la pantalla: {Mensaje}", work.JobId, msg);
    }

    internal string? FindFile(Guid jobId) =>
        Directory.EnumerateFiles(options.Value.Path, jobId.ToString("N") + ".*").FirstOrDefault();

    private async Task ResumeAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRecoveryRepository>();
            foreach (var job in await repo.ListAsync(ct))
            {
                if (job.Estado is RecoveryStates.PorInsertar or RecoveryStates.Insertando)
                {
                    await queue.EnqueueAsync(new RecoveryWork(job.JobId, true, null));
                }
                else if (job.Estado is RecoveryStates.Subido or RecoveryStates.Leyendo)
                {
                    await repo.CancelAsync(job.JobId, ct);
                    await repo.UpdateAsync(job.JobId, RecoveryStates.Error, null,
                        "La API se reinició mientras leía el archivo. Vuelve a subirlo.", ct);
                    RecoveryService.TryDelete(FindFile(job.JobId));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("No se pudieron reanudar recuperaciones pendientes: {Error}", ex.Message);
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IRecoveryRepository>();
            var limite = DateTime.Now.AddHours(-options.Value.ReviewedExpireHours);
            foreach (var job in (await repo.ListAsync(ct)).Where(j => j.Estado == RecoveryStates.Revisado && j.FechaActualizacion < limite))
            {
                await repo.CancelAsync(job.JobId, ct);
                RecoveryService.TryDelete(FindFile(job.JobId));
                logger.LogInformation("Recuperación {JobId} revisada y nunca insertada: cancelada automáticamente", job.JobId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Limpieza de recuperaciones falló: {Error}", ex.Message);
        }

        foreach (var f in Directory.EnumerateFiles(options.Value.Path))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(2))
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
