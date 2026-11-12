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

    [Theory]
    [InlineData("NEGOCIAR")]
    [InlineData("No molestar")]
    [InlineData("")]          // aunque venga vacío, se guarda
    [InlineData(null)]
    public async Task Respuesta_SeGuardaTalCual_SinConsultarAVoices(string? texto)
    {
        var repo = new FakeRepo();
        var gw = new FakeGateway();
        var h = new RegisterSmsEventHandler(repo, gw, NullLogger<RegisterSmsEventHandler>.Instance);
        var p = new SmsWebhookPayload
        {
            Event = "Reply", MessageId = "M1", Number = " 5500000000 ", Text = texto,
            CampaignId = "C1", CreatedAt = "2026-11-11T17:29:00.727Z",
        };

        var nuevo = await h.HandleAsync(p, """{"event":"Reply"}""", Now, CancellationToken.None);

        Assert.True(nuevo);
        var r = Assert.Single(repo.Respuestas);
        Assert.Equal(texto, r.Text);
        Assert.Equal("5500000000", r.Number);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 17, 29, 0, 727, TimeSpan.Zero), r.CreatedAtUtc);
        Assert.Empty(repo.Guardados);   // no va a la tabla de estados
        Assert.Equal(0, gw.Consultas);
    }

    [Fact]
    public async Task Respuesta_ConFechaRara_SeGuardaSinFecha()
    {
        var repo = new FakeRepo();
        var h = new RegisterSmsEventHandler(repo, new FakeGateway(), NullLogger<RegisterSmsEventHandler>.Instance);

        await h.HandleAsync(new SmsWebhookPayload { Event = "Reply", MessageId = "M1", Number = "5500000000", Text = "hola", CreatedAt = "ayer" },
            "{}", Now, CancellationToken.None);

        Assert.Null(Assert.Single(repo.Respuestas).CreatedAtUtc);
    }

    [Fact]
    public async Task Respuesta_TextoMuyLargo_SeRecorta()
    {
        var repo = new FakeRepo();
        var h = new RegisterSmsEventHandler(repo, new FakeGateway(), NullLogger<RegisterSmsEventHandler>.Instance);

        await h.HandleAsync(new SmsWebhookPayload { Event = "Reply", MessageId = "M1", Number = "5500000000", Text = new string('x', 5000) },
            "{}", Now, CancellationToken.None);

        Assert.Equal(RegisterSmsEventHandler.MaxReplyTextLength, Assert.Single(repo.Respuestas).Text!.Length);
    }

    [Theory]
    [InlineData(null, "5500000000")]   // sin message_id
    [InlineData("M1", null)]           // sin número
    [InlineData("M1", "   ")]
    public async Task Respuesta_SinComoLigarla_SeRechaza(string? id, string? numero)
    {
        var repo = new FakeRepo();
        var h = new RegisterSmsEventHandler(repo, new FakeGateway(), NullLogger<RegisterSmsEventHandler>.Instance);

        await Assert.ThrowsAsync<DomainException>(() =>
            h.HandleAsync(new SmsWebhookPayload { Event = "Reply", MessageId = id, Number = numero, Text = "hola" }, "{}", Now, CancellationToken.None));

        Assert.Empty(repo.Respuestas);
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

        public Task<IReadOnlyList<bool>> RegisterBatchAsync(IReadOnlyList<SmsToSave> items, CancellationToken ct)
        {
            foreach (var x in items) Guardados.Add((x.ReceivedAtUtc, x.Detail));
            return Task.FromResult<IReadOnlyList<bool>>(items.Select(_ => true).ToList());
        }

        public List<SmsReplyToSave> Respuestas { get; } = [];
        public Task<bool> RegisterReplyAsync(SmsReplyToSave reply, CancellationToken ct)
        {
            Respuestas.Add(reply);
            return Task.FromResult(true);
        }
    }
}
