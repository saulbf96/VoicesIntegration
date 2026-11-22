using Microsoft.Extensions.Logging.Abstractions;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Notifications;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Application.Features.Recovery;

namespace VoicesIntegration.Tests.Application;

// Si el servidor de base de datos se pausa, la recuperacion reintenta sola en vez de marcar ERROR
public class RecoveryRetryTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public async Task Error_pasajero_se_reintenta_y_termina()
    {
        var repo = new RepoFalso { FallasAntesDeInsertar = 2 };
        await Servicio(repo).InsertAsync(Id, null, CancellationToken.None);

        Assert.Equal(3, repo.IntentosInsertar);
        Assert.DoesNotContain(RecoveryStates.Error, repo.Estados);
    }

    [Fact]
    public async Task Si_no_vuelve_a_tiempo_marca_error()
    {
        var repo = new RepoFalso { FallasAntesDeInsertar = 99 };
        await Servicio(repo).InsertAsync(Id, null, CancellationToken.None);

        Assert.Equal(3, repo.IntentosInsertar);   // 1 + 2 reintentos
        Assert.Contains(RecoveryStates.Error, repo.Estados);
    }

    [Fact]
    public async Task Error_que_no_es_de_conexion_no_se_reintenta()
    {
        var repo = new RepoFalso { FallasAntesDeInsertar = 1, FallaNoPasajera = true };
        await Servicio(repo).InsertAsync(Id, null, CancellationToken.None);

        Assert.Equal(1, repo.IntentosInsertar);
        Assert.Contains(RecoveryStates.Error, repo.Estados);
    }

    private static RecoveryService Servicio(RepoFalso repo) =>
        new(repo, new LectorFalso(), new CorreoFalso(), new BandejaFalsa(), NullLogger<RecoveryService>.Instance)
        {
            Esperas = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]
        };

    private sealed class RepoFalso : IRecoveryRepository
    {
        public int FallasAntesDeInsertar { get; init; }
        public bool FallaNoPasajera { get; init; }
        public int IntentosInsertar { get; private set; }
        public List<string> Estados { get; } = [];

        public Task InsertSmsAsync(Guid jobId, CancellationToken ct)
        {
            IntentosInsertar++;
            if (IntentosInsertar <= FallasAntesDeInsertar)
                throw FallaNoPasajera ? new InvalidOperationException("SQL mal escrito") : new TimeoutException("el 205 no respondio");
            return Task.CompletedTask;
        }

        public bool IsTransient(Exception ex) => ex is TimeoutException;

        public Task<RecoveryJob?> GetAsync(Guid jobId, CancellationToken ct) =>
            Task.FromResult<RecoveryJob?>(new RecoveryJob { JobId = jobId, Canal = RecoveryChannels.Sms, Estado = RecoveryStates.PorInsertar });

        public Task UpdateAsync(Guid jobId, string? state, int? rowsRead, string? message, CancellationToken ct)
        {
            if (state is not null) Estados.Add(state);
            return Task.CompletedTask;
        }

        public Task<RecoveryPage> HistoryAsync(DateOnly? dia, string? canal, string? q, string? estado, int page, int size, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateAsync(Guid jobId, string fileName, long sizeBytes, string? user, string canal, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> BulkLoadSmsAsync(Guid jobId, IAsyncEnumerable<SmsRecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct) => throw new NotSupportedException();
        public Task CompareSmsAsync(Guid jobId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RecoveryJob>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> BulkLoadLightAsync(Guid jobId, IAsyncEnumerable<RecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentifierSet> GetMissingIdsAsync(Guid jobId, CancellationToken ct) => throw new NotSupportedException();
        public Task BulkLoadBatchAsync(Guid jobId, int lote, IReadOnlyList<RecoveryRow> rows, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BulkInsertFinalAsync(Guid jobId, IReadOnlyList<RecoveryRow> rows, CancellationToken ct) => throw new NotSupportedException();
        public Task AddInsertedAsync(Guid jobId, int count, CancellationToken ct) => throw new NotSupportedException();
        public Task InsertBatchAsync(Guid jobId, int lote, CancellationToken ct) => throw new NotSupportedException();
        public Task CompareAsync(Guid jobId, CancellationToken ct) => throw new NotSupportedException();
        public Task InsertMissingAsync(Guid jobId, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelAsync(Guid jobId, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveOmittedAsync(Guid jobId, int omitidas, DateTime corte, CancellationToken ct) => throw new NotSupportedException();
        public Task RecountInsertedAsync(Guid jobId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class LectorFalso : IRecoveryFileReader
    {
        public IAsyncEnumerable<RecoveryRow> ReadAsync(string path, bool light, Func<string, bool>? include, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<SmsRecoveryRow> ReadSmsAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CorreoFalso : INotificador
    {
        public Task EnviarAsync(string asunto, string cuerpo, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ProbarAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class BandejaFalsa : IWebhookInbox
    {
        public InboxStatus GetStatus() => new(0, 0, null);
        public Task SaveAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveRejectedAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<InboxItem> GetPending(int max) => throw new NotSupportedException();
        public Task<string> ReadAsync(InboxItem item, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Complete(InboxItem item) => throw new NotSupportedException();
        public void Reject(InboxItem item, string reason) => throw new NotSupportedException();
        public int Cleanup(TimeSpan rejectedRetention, TimeSpan tmpMaxAge) => throw new NotSupportedException();
        public Task WaitForItemsAsync(TimeSpan maxWait, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
