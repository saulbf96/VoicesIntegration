using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Application.Features.Webhooks.Inbox;
using DomainCallResult = VoicesIntegration.Domain.Blaster.CallResult;

namespace VoicesIntegration.Tests.Application;

public class WebhookInboxProcessorTests
{
    private const string Valido = """{"identifier":"A1","status":"SENT","call_type":"initial","sip_code":200}""";
    private const string EstadoRaro = """{"identifier":"A2"}""";   // sin status: sí se rechaza

    [Fact]
    public async Task AvisoValido_SeGuardaYSaleDeLaBandeja()
    {
        var (processor, inbox, repo) = Crear();
        inbox.Agregar(Valido);

        var r = await processor.ProcessPendingAsync(100, 2, CancellationToken.None);

        Assert.Equal(1, r.Processed);
        Assert.Single(repo.Saved);
        Assert.Empty(inbox.Pendientes);
    }

    [Fact]
    public async Task AvisoInvalido_VaARechazados_NoSePierde()
    {
        var (processor, inbox, _) = Crear();
        inbox.Agregar(EstadoRaro);

        var r = await processor.ProcessPendingAsync(100, 2, CancellationToken.None);

        Assert.Equal(1, r.Rejected);
        Assert.Empty(inbox.Pendientes);
        Assert.Single(inbox.Rechazados);
    }

    [Fact]
    public async Task BdCaida_ElAvisoSeQuedaEnLaBandeja_ParaReintentar()
    {
        var (processor, inbox, repo) = Crear();
        inbox.Agregar(Valido);
        repo.Falla = true;   // simula SQL caído

        var r1 = await processor.ProcessPendingAsync(100, 2, CancellationToken.None);

        Assert.True(r1.TransientFailure);
        Assert.Single(inbox.Pendientes);   // ¡NO se perdió!
        Assert.Empty(repo.Saved);

        repo.Falla = false;  // vuelve la BD
        var r2 = await processor.ProcessPendingAsync(100, 2, CancellationToken.None);

        Assert.Equal(1, r2.Processed);
        Assert.Empty(inbox.Pendientes);
        Assert.Single(repo.Saved);
    }

    [Fact]
    public async Task ArchivoOcupadoPorAntivirus_SeReintenta_SinMarcarFallaDeBd()
    {
        var (processor, inbox, repo) = Crear();
        inbox.Agregar(Valido);
        inbox.LecturasOcupadas = 1;

        var r1 = await processor.ProcessPendingAsync(100, 1, CancellationToken.None);

        Assert.True(r1.Busy);
        Assert.False(r1.TransientFailure);   // la BD está bien: no se espera ni se dice "BD no respondió"
        Assert.Single(inbox.Pendientes);     // el aviso NO se perdió

        var r2 = await processor.ProcessPendingAsync(100, 1, CancellationToken.None);

        Assert.Equal(1, r2.Processed);
        Assert.Empty(inbox.Pendientes);
        Assert.Single(repo.Saved);
    }

    [Fact]
    public async Task MismoAvisoDosVeces_SeGuardaUnaSola()
    {
        var (processor, inbox, repo) = Crear();
        inbox.Agregar(Valido);
        inbox.Agregar(Valido);

        var r = await processor.ProcessPendingAsync(100, 1, CancellationToken.None);

        Assert.Equal(1, r.Processed);
        Assert.Equal(1, r.Duplicates);
        Assert.Single(repo.Saved);
        Assert.Empty(inbox.Pendientes);
    }

    private static readonly SmsRepoContador Sms = new();

    [Fact]
    public async Task AvisoSms_SeVaAlHandlerDeSms()
    {
        var (processor, inbox, repo) = Crear();
        var antes = Sms.Total;
        inbox.Agregar("""{"message_id":"S9","status":"SENT"}""", WebhookChannels.Sms);

        var r = await processor.ProcessPendingAsync(100, 1, CancellationToken.None);

        Assert.Equal(1, r.Processed);
        Assert.Equal(antes + 1, Sms.Total);
        Assert.Empty(repo.Saved);   // no se fue a Blaster
    }

    private sealed class NoGateway : VoicesIntegration.Application.Abstractions.Sms.ISmsGateway
    {
        public Task<VoicesIntegration.Application.Abstractions.Sms.SmsMessageDetail?> GetMessageAsync(string id, CancellationToken ct)
            => Task.FromResult<VoicesIntegration.Application.Abstractions.Sms.SmsMessageDetail?>(null);
    }

    private sealed class SmsRepoContador : VoicesIntegration.Application.Abstractions.Persistence.ISmsRepository
    {
        private int _total;
        public int Total => _total;
        public Task<bool> RegisterAsync(string raw, byte[] hash, DateTimeOffset at, VoicesIntegration.Application.Abstractions.Sms.SmsMessageDetail? d, CancellationToken ct)
        { Interlocked.Increment(ref _total); return Task.FromResult(true); }
    }

    private static (WebhookInboxProcessor, FakeInbox, FakeRepository) Crear()
    {
        var inbox = new FakeInbox();
        var repo = new FakeRepository();
        var services = new ServiceCollection()
            .AddSingleton<IBlasterCallRepository>(repo)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddScoped<RegisterCallResultHandler>()
            .AddSingleton<VoicesIntegration.Application.Abstractions.Persistence.ISmsRepository>(Sms)
            .AddSingleton<VoicesIntegration.Application.Abstractions.Sms.ISmsGateway>(new NoGateway())
            .AddScoped<VoicesIntegration.Application.Features.Webhooks.Sms.RegisterSmsEventHandler>()
            .BuildServiceProvider();
        var processor = new WebhookInboxProcessor(inbox, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WebhookInboxProcessor>.Instance);
        return (processor, inbox, repo);
    }

    private sealed class FakeInbox : IWebhookInbox
    {
        private int _n;
        public Dictionary<string, string> Pendientes { get; } = [];
        public Dictionary<string, string> Rechazados { get; } = [];

        public Dictionary<string, string> Canal { get; } = [];
        public void Agregar(string raw, string canal = WebhookChannels.Blaster) { var k = $"{++_n:D5}.json"; Pendientes[k] = raw; Canal[k] = canal; }

        public Task SaveAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken ct) { Agregar(rawPayload, channel); return Task.CompletedTask; }
        public Task SaveRejectedAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, string reason, CancellationToken ct) { Rechazados[Guid.NewGuid().ToString()] = reason; return Task.CompletedTask; }
        public IReadOnlyList<InboxItem> GetPending(int max) => Pendientes.Keys.Order().Take(max).Select(k => new InboxItem(k, Canal[k], DateTimeOffset.UtcNow)).ToList();
        /// <summary>Simula el antivirus: las primeras N lecturas fallan con "archivo en uso".</summary>
        public int LecturasOcupadas { get; set; }

        public Task<string> ReadAsync(InboxItem item, CancellationToken ct)
        {
            if (LecturasOcupadas > 0)
            {
                LecturasOcupadas--;
                throw new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
            }
            return Task.FromResult(Pendientes[item.Id]);
        }
        public void Complete(InboxItem item) { lock (Pendientes) Pendientes.Remove(item.Id); }
        public void Reject(InboxItem item, string reason) { lock (Pendientes) { Pendientes.Remove(item.Id); Rechazados[item.Id] = reason; } }
        public InboxStatus GetStatus() => new(Pendientes.Count, Rechazados.Count, null);
        public int Cleanup(TimeSpan rejectedRetention, TimeSpan tmpMaxAge) => 0;
        public Task WaitForItemsAsync(TimeSpan maxWait, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeRepository : IBlasterCallRepository
    {
        private readonly HashSet<string> _keys = [];
        public List<DomainCallResult> Saved { get; } = [];
        public bool Falla { get; set; }

        public Task<bool> RegisterCallResultAsync(DomainCallResult result, string rawPayload, byte[] payloadHash, CancellationToken ct)
        {
            if (Falla) throw new TimeoutException("SQL no responde");
            lock (_keys)
            {
                if (!_keys.Add(result.MessageId + Convert.ToHexString(payloadHash))) return Task.FromResult(false);
                Saved.Add(result);
                return Task.FromResult(true);
            }
        }

        public Task<CallDetails?> GetByMessageIdAsync(string messageId, CancellationToken ct) => Task.FromResult<CallDetails?>(null);
    }
}
