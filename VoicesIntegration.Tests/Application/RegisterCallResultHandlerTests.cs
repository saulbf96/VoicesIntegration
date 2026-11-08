using Microsoft.Extensions.Logging.Abstractions;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Domain.Common;
using DomainCallResult = VoicesIntegration.Domain.Blaster.CallResult;

namespace VoicesIntegration.Tests.Application;

public class RegisterCallResultHandlerTests
{
    private const string RawJson = """{"message_id":"abc-123","status":"SENT","call_type":"initial","sip_code":200}""";

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 15, 0, 0, TimeSpan.Zero);

    private static readonly CallResultPayload Payload = new()
    {
        MessageId = "abc-123",
        Status = "SENT",
        CallType = "initial",
        SipCode = 200
    };


    [Fact]
    public async Task HandleAsync_EventoNuevo_SeGuardaYDevuelveRegistered()
    {
        var repository = new FakeRepository();
        var handler = CreateHandler(repository);

        var outcome = await handler.HandleAsync(Payload, RawJson, Now, CancellationToken.None);

        Assert.Equal(RegisterCallResultOutcome.Registered, outcome);
        var saved = Assert.Single(repository.Saved);
        Assert.Equal("abc-123", saved.MessageId);
        Assert.Equal(CallStatus.Sent, saved.Status);
        Assert.Equal(Now, saved.ReceivedAtUtc);   // la hora de LLEGADA, no la de proceso


        
    }

    [Fact]
    public async Task HandleAsync_MismoJsonDosVeces_ElSegundoEsDuplicado()
    {
        var repository = new FakeRepository();
        var handler = CreateHandler(repository);

        await handler.HandleAsync(Payload, RawJson, Now, CancellationToken.None);
        var second = await handler.HandleAsync(Payload, RawJson, Now, CancellationToken.None);

        Assert.Equal(RegisterCallResultOutcome.Duplicate, second);
        Assert.Single(repository.Saved);
    }


    [Fact]
    public async Task HandleAsync_EstadoNuevoDeVoices_SeGuardaTalCual()
    {
        var repository = new FakeRepository();
        var handler = CreateHandler(repository);

        await handler.HandleAsync(Payload with { Status = "queued_provider" }, RawJson, Now, CancellationToken.None);

        var saved = Assert.Single(repository.Saved);
        Assert.Equal(CallStatus.Other, saved.Status);
        Assert.Equal("QUEUED_PROVIDER", saved.StatusText);
    }

    [Fact]
    public async Task HandleAsync_SinEstado_NoGuardaNada()
    {
        var repository = new FakeRepository();
        var handler = CreateHandler(repository);

        await Assert.ThrowsAsync<DomainException>(() =>
            handler.HandleAsync(Payload with { Status = null }, RawJson, Now, CancellationToken.None));

        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task HandleAsync_WebhookRealConIdentifier_UsaIdentifierComoId()
    {
        // Así llega el webhook REAL de Voices: "identifier" en vez de "message_id".
        const string realJson = """{"identifier":"78c27271-cbe8-46ef-afac-2f22f51d2ae2","status":"FAILED","call_type":"initial","sip_code":480}""";
        var repository = new FakeRepository();
        var handler = CreateHandler(repository);
        var payload = new CallResultPayload
        {
            Identifier = "78c27271-cbe8-46ef-afac-2f22f51d2ae2",
            Status = "FAILED",
            CallType = "initial",
            SipCode = 480
        };

        var outcome = await handler.HandleAsync(payload, realJson, Now, CancellationToken.None);

        Assert.Equal(RegisterCallResultOutcome.Registered, outcome);
        Assert.Equal("78c27271-cbe8-46ef-afac-2f22f51d2ae2", Assert.Single(repository.Saved).MessageId);
    }

    private static RegisterCallResultHandler CreateHandler(FakeRepository repository)
        => new(repository, NullLogger<RegisterCallResultHandler>.Instance);

    /// <summary>Repositorio en memoria: simula la idempotencia del SP sin tocar SQL.</summary>
    private sealed class FakeRepository : IBlasterCallRepository
    {
        private readonly HashSet<string> _keys = [];
        public List<DomainCallResult> Saved { get; } = [];

        public Task<bool> RegisterCallResultAsync(DomainCallResult result, string rawPayload, byte[] payloadHash, CancellationToken cancellationToken)
        {
            if (!_keys.Add(result.MessageId + Convert.ToHexString(payloadHash)))
                return Task.FromResult(false);

            Saved.Add(result);
            return Task.FromResult(true);
        }

        public async Task<IReadOnlyList<bool>> RegisterCallResultsBatchAsync(IReadOnlyList<CallEventToSave> events, CancellationToken cancellationToken)
        {
            var r = new List<bool>();
            foreach (var e in events) r.Add(await RegisterCallResultAsync(e.Result, e.RawPayload, e.PayloadHash, cancellationToken));
            return r;
        }

        public Task<CallDetails?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken)
            => Task.FromResult<CallDetails?>(null);
    }
}
