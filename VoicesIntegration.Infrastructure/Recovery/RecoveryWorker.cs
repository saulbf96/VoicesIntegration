using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Application.Features.Recovery;

namespace VoicesIntegration.Infrastructure.Recovery;

/// <summary>Trabajo pendiente para el proceso en segundo plano.</summary>
public sealed record RecoveryWork(Guid JobId, bool Insert, string? FilePath);

/// <summary>Cola en memoria (una recuperación a la vez, para no saturar la BD).</summary>
public sealed class RecoveryQueue
{
    private readonly Channel<RecoveryWork> _channel = Channel.CreateUnbounded<RecoveryWork>();
    public ValueTask EnqueueAsync(RecoveryWork work) => _channel.Writer.WriteAsync(work);
    internal ChannelReader<RecoveryWork> Reader => _channel.Reader;
}

/// <summary>
/// Procesa las recuperaciones en segundo plano (pueden tardar minutos con millones de filas),
/// así la pantalla no se queda esperando: solo consulta el avance.
/// Al arrancar: reanuda las inserciones pendientes y marca como error las lecturas interrumpidas.
/// Cada hora: cancela revisiones abandonadas y borra archivos viejos.
/// </summary>
internal sealed class RecoveryWorker(
    RecoveryQueue queue,
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

                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<RecoveryService>();
                if (work.Insert) await service.InsertAsync(work.JobId, work.FilePath ?? FindFile(work.JobId), stoppingToken);
                else await service.ReviewAsync(work.JobId, work.FilePath!, stoppingToken);
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

    /// <summary>El archivo de cada recuperación se guarda como {jobId}.csv o {jobId}.zip.</summary>
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
                    await queue.EnqueueAsync(new RecoveryWork(job.JobId, true, null));   // insertar es seguro de repetir
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
                await repo.CancelAsync(job.JobId, ct);   // libera la tabla temporal
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
