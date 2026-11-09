using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Infrastructure.Inbox;

namespace VoicesIntegration.Tests.Infrastructure;

/// <summary>Prueba la bandeja por SEGMENTOS en disco real (carpeta temporal que se borra al final).</summary>
public sealed class SegmentedWebhookInboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voices-seg-test-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

    private (SegmentedWebhookInbox seg, FileWebhookInbox legacy) Crear(int maxItems = 5000, int maxSeconds = 1)
    {
        var o = Options.Create(new InboxOptions { Path = _dir, SegmentMaxItems = maxItems, SegmentMaxSeconds = maxSeconds });
        var legacy = new FileWebhookInbox(o);
        return (new SegmentedWebhookInbox(legacy, o, NullLogger<SegmentedWebhookInbox>.Instance), legacy);
    }

    private static async Task<IReadOnlyList<InboxItem>> EsperarPendientes(IWebhookInbox inbox, int esperados, int max = 10_000)
    {
        for (var i = 0; i < 100; i++)
        {
            var p = inbox.GetPending(max);
            if (p.Count >= esperados) return p;
            await Task.Delay(100);
        }
        return inbox.GetPending(max);
    }

    [Fact]
    public async Task Guardar_MuchosALaVez_QuedanEnPocosArchivos_YEnOrden()
    {
        var (inbox, _) = Crear(maxItems: 300);
        await using var _d = inbox;

        await Task.WhenAll(Enumerable.Range(0, 1000).Select(i =>
            inbox.SaveAsync(WebhookChannels.Blaster, $$"""{"identifier":"A{{i}}","nombre":"José"}""", T0.AddMilliseconds(i), CancellationToken.None)));

        var items = await EsperarPendientes(inbox, 1000);
        Assert.Equal(1000, items.Count);
        Assert.True(Directory.GetFiles(Path.Combine(_dir, "segments", "ready")).Length <= 10);   // no 1,000 archivos
        Assert.Contains("José", await inbox.ReadAsync(items[0], CancellationToken.None));          // acentos intactos
    }

    [Fact]
    public async Task Completar_TodosLosAvisos_BorraElSegmento()
    {
        var (inbox, _) = Crear();
        await using var _d = inbox;
        for (var i = 0; i < 5; i++)
            await inbox.SaveAsync(WebhookChannels.Sms, $$"""{"id":{{i}}}""", T0, CancellationToken.None);

        var items = await EsperarPendientes(inbox, 5);
        foreach (var it in items.Take(4)) inbox.Complete(it);
        Assert.Single(inbox.GetPending(10));                                  // falta uno: el segmento se queda
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "segments", "ready")));

        inbox.Complete(items[4]);
        Assert.Empty(inbox.GetPending(10));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "segments", "ready")));
    }

    [Fact]
    public async Task Rechazar_VaARechazadosConMotivo()
    {
        var (inbox, _) = Crear();
        await using var _d = inbox;
        await inbox.SaveAsync(WebhookChannels.Blaster, """{"identifier":"MALO"}""", T0, CancellationToken.None);
        var item = Assert.Single(await EsperarPendientes(inbox, 1));

        inbox.Reject(item, "sin status");

        Assert.Empty(inbox.GetPending(10));
        Assert.Equal(1, inbox.GetStatus().Rejected);
        Assert.Contains(Directory.GetFiles(Path.Combine(_dir, "rejected"), "*.motivo.txt"), f => File.ReadAllText(f) == "sin status");
    }

    [Fact]
    public async Task SiLaApiSeApagaANMedias_ElSegmentoAbiertoSeRecuperaAlArrancar_YLaLineaCortadaSeIgnora()
    {
        var open = Directory.CreateDirectory(Path.Combine(_dir, "segments", "open")).FullName;
        await File.WriteAllTextAsync(Path.Combine(open, "0638981000000000000_000001.jsonl"),
            "{\"c\":\"blaster\",\"t\":638981000000000000,\"p\":\"{\\\"identifier\\\":\\\"OK\\\"}\"}\n{\"c\":\"blaster\",\"t\":63898");

        var (inbox, _) = Crear();
        await using var _d = inbox;

        var item = Assert.Single(inbox.GetPending(10));
        Assert.Equal("""{"identifier":"OK"}""", await inbox.ReadAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task LaBandejaVieja_SeProcesaPrimero()
    {
        // Avisos que quedaron en el formato viejo (un archivo por aviso).
        var o = Options.Create(new InboxOptions { Path = _dir });
        using (var viejo = new FileWebhookInbox(o))
            await viejo.SaveAsync(WebhookChannels.Blaster, """{"identifier":"VIEJO"}""", T0, CancellationToken.None);

        var (inbox, _) = Crear();
        await using var _d = inbox;
        await inbox.SaveAsync(WebhookChannels.Blaster, """{"identifier":"NUEVO"}""", T0.AddMinutes(1), CancellationToken.None);
        await Task.Delay(1500);   // que se cierre el segmento

        var primero = Assert.Single(inbox.GetPending(10));
        Assert.Contains("VIEJO", await inbox.ReadAsync(primero, CancellationToken.None));
        inbox.Complete(primero);

        var segundo = Assert.Single(await EsperarPendientes(inbox, 1));
        Assert.Contains("NUEVO", await inbox.ReadAsync(segundo, CancellationToken.None));
    }

    [Fact]
    public async Task Estado_CuentaPendientesSinRecorrerMillonesDeArchivos()
    {
        var (inbox, _) = Crear();
        await using var _d = inbox;
        for (var i = 0; i < 7; i++)
            await inbox.SaveAsync(WebhookChannels.Blaster, $$"""{"identifier":"E{{i}}"}""", T0, CancellationToken.None);

        Assert.Equal(7, inbox.GetStatus().Pending);   // aún abierto o ya listo: cuenta igual
        await EsperarPendientes(inbox, 7);
        Assert.Equal(7, inbox.GetStatus().Pending);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
