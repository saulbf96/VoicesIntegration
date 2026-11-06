using Microsoft.Extensions.Logging.Abstractions;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Abstractions.Sms;
using VoicesIntegration.Application.Features.Webhooks.Sms;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Tests.Application;

public class RegisterSmsEventHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Mensaje_SeGuardaConElDetalleDeVoices()
    {
        var repo = new FakeRepo();
        var gw = new FakeGateway { Detalle = new SmsMessageDetail("5555555555", "Hola", "telcel", Now, Now, null) };
        var h = new RegisterSmsEventHandler(repo, gw, NullLogger<RegisterSmsEventHandler>.Instance);

        var nuevo = await h.HandleAsync(new SmsWebhookPayload { MessageId = "S1", Status = "SENT" }, """{"message_id":"S1","status":"SENT"}""", Now, CancellationToken.None);

        Assert.True(nuevo);
        Assert.Equal("5555555555", Assert.Single(repo.Guardados).Detalle!.To);
        Assert.Equal(Now, repo.Guardados[0].Recibido);
    }

    [Fact]
    public async Task SiLaConsultaDelDetalleFalla_SeGuardaIgualSinDetalle()
    {
        var repo = new FakeRepo();
        var h = new RegisterSmsEventHandler(repo, new FakeGateway { Falla = true }, NullLogger<RegisterSmsEventHandler>.Instance);

        await h.HandleAsync(new SmsWebhookPayload { MessageId = "S1", Status = "FAILED" }, "{}", Now, CancellationToken.None);

        Assert.Null(Assert.Single(repo.Guardados).Detalle);   // NO se perdió el aviso
    }

    [Fact]
    public async Task Lote_NoConsultaDetalle_YSeGuarda()
    {
        var repo = new FakeRepo();
        var gw = new FakeGateway();
        var h = new RegisterSmsEventHandler(repo, gw, NullLogger<RegisterSmsEventHandler>.Instance);

        await h.HandleAsync(new SmsWebhookPayload { Type = "BATCH", BatchId = "B1", Status = "COMPLETED" }, "{}", Now, CancellationToken.None);

        Assert.Single(repo.Guardados);
        Assert.Equal(0, gw.Consultas);
    }

    [Theory]
    [InlineData(null, "SENT", null)]   // mensaje sin message_id
    [InlineData("S1", null, null)]     // mensaje sin status
    [InlineData(null, "COMPLETED", "BATCH")]   // lote sin batch_id
    public async Task DatosIncompletos_LanzaDomainException_YNoGuarda(string? id, string? status, string? type)
    {
        var repo = new FakeRepo();
        var h = new RegisterSmsEventHandler(repo, new FakeGateway(), NullLogger<RegisterSmsEventHandler>.Instance);

        await Assert.ThrowsAsync<DomainException>(() =>
            h.HandleAsync(new SmsWebhookPayload { MessageId = id, Status = status, Type = type }, "{}", Now, CancellationToken.None));

        Assert.Empty(repo.Guardados);
    }

    private sealed class FakeGateway : ISmsGateway
    {
        public SmsMessageDetail? Detalle { get; init; }
        public bool Falla { get; init; }
        public int Consultas { get; private set; }

        public Task<SmsMessageDetail?> GetMessageAsync(string messageId, CancellationToken ct)
        {
            Consultas++;
            if (Falla) throw new HttpRequestException("Voices no responde");
            return Task.FromResult(Detalle);
        }
    }

    private sealed class FakeRepo : ISmsRepository
    {
        public List<(DateTimeOffset Recibido, SmsMessageDetail? Detalle)> Guardados { get; } = [];

        public Task<bool> RegisterAsync(string rawPayload, byte[] payloadHash, DateTimeOffset receivedAtUtc, SmsMessageDetail? detail, CancellationToken ct)
        {
            Guardados.Add((receivedAtUtc, detail));
            return Task.FromResult(true);
        }
    }
}
