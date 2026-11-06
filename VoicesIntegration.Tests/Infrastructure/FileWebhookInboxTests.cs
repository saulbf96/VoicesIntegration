using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Infrastructure.Inbox;

namespace VoicesIntegration.Tests.Infrastructure;

/// <summary>Prueba la bandeja REAL en disco (carpeta temporal que se borra al final).</summary>
public sealed class FileWebhookInboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "voices-inbox-test-" + Guid.NewGuid().ToString("N"));

    private FileWebhookInbox Crear() => new(Options.Create(new InboxOptions { Path = _dir }));

    [Fact]
    public async Task Guardar_QuedaPendiente_ConElMismoContenidoYHora()
    {
        using var inbox = Crear();
        var llegada = new DateTimeOffset(2026, 11, 4, 15, 30, 0, TimeSpan.Zero);

        await inbox.SaveAsync(WebhookChannels.Blaster, """{"identifier":"X","nombre":"José"}""", llegada, CancellationToken.None);

        var item = Assert.Single(inbox.GetPending(10));
        Assert.Equal(llegada, item.ReceivedAtUtc);
        Assert.Equal("""{"identifier":"X","nombre":"José"}""", await inbox.ReadAsync(item, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tmp")));   // no quedan archivos a medias
    }

    [Fact]
    public async Task Pendientes_SalenEnOrdenDeLlegada()
    {
        using var inbox = Crear();
        var t = new DateTimeOffset(2026, 11, 4, 15, 0, 0, TimeSpan.Zero);
        await inbox.SaveAsync(WebhookChannels.Blaster, "{\"n\":2}", t.AddSeconds(2), CancellationToken.None);
        await inbox.SaveAsync(WebhookChannels.Blaster, "{\"n\":1}", t.AddSeconds(1), CancellationToken.None);

        var items = inbox.GetPending(10);

        Assert.Equal("{\"n\":1}", await inbox.ReadAsync(items[0], CancellationToken.None));
        Assert.Equal("{\"n\":2}", await inbox.ReadAsync(items[1], CancellationToken.None));
    }

    [Fact]
    public async Task Completar_LoQuita_Y_Rechazar_LoMueveConMotivo()
    {
        using var inbox = Crear();
        await inbox.SaveAsync(WebhookChannels.Blaster, "{\"a\":1}", DateTimeOffset.UtcNow, CancellationToken.None);
        await inbox.SaveAsync(WebhookChannels.Blaster, "{\"b\":2}", DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
        var items = inbox.GetPending(10);

        inbox.Complete(items[0]);
        inbox.Reject(items[1], "Estado desconocido");

        var s = inbox.GetStatus();
        Assert.Equal(0, s.Pending);
        Assert.Equal(1, s.Rejected);
        Assert.Equal("Estado desconocido", File.ReadAllText(Path.Combine(_dir, "rejected", items[1].Id + ".motivo.txt")));
    }

    [Fact]
    public async Task SobreviveReinicio_LoPendienteSigueAhi()
    {
        using (var inbox = Crear())
            await inbox.SaveAsync(WebhookChannels.Blaster, "{\"a\":1}", DateTimeOffset.UtcNow, CancellationToken.None);

        using var reiniciada = Crear();   // como si el IIS se hubiera reiniciado
        Assert.Single(reiniciada.GetPending(10));
    }

    [Fact]
    public async Task ElCanalViajaEnElNombre_YLosArchivosViejosSonBlaster()
    {
        using var inbox = Crear();
        await inbox.SaveAsync(WebhookChannels.Sms, "{\"m\":1}", DateTimeOffset.UtcNow, CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, "pending", "0000000000000000001_0123456789abcdef0123456789abcdef.json"), "{}");  // formato viejo

        var items = inbox.GetPending(10);

        Assert.Contains(items, i => i.Channel == WebhookChannels.Sms);
        Assert.Contains(items, i => i.Channel == WebhookChannels.Blaster);
    }

    [Fact]
    public async Task AlGuardarseEnBd_ElArchivoSeBorra_NoLlenaElDisco()
    {
        using var inbox = Crear();
        await inbox.SaveAsync(WebhookChannels.Blaster, "{\"a\":1}", DateTimeOffset.UtcNow, CancellationToken.None);

        inbox.Complete(inbox.GetPending(10)[0]);

        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "pending")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tmp")));
    }

    [Fact]
    public async Task Limpieza_BorraRechazadosViejosYTemporalesAbandonados_NoTocaLoReciente()
    {
        using var inbox = Crear();
        await inbox.SaveRejectedAsync(WebhookChannels.Blaster, "viejo", DateTimeOffset.UtcNow, "motivo", CancellationToken.None);
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "rejected")))
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-40));              // rechazado de hace 40 días
        var tmpViejo = Path.Combine(_dir, "tmp", "abandonado.json");
        File.WriteAllText(tmpViejo, "{}");
        File.SetLastWriteTimeUtc(tmpViejo, DateTime.UtcNow.AddHours(-3));           // temporal abandonado
        await inbox.SaveRejectedAsync(WebhookChannels.Blaster, "reciente", DateTimeOffset.UtcNow, "motivo", CancellationToken.None);

        var borrados = inbox.Cleanup(TimeSpan.FromDays(30), TimeSpan.FromHours(1));

        Assert.Equal(3, borrados);                                                   // .json + .motivo.txt viejos + tmp
        Assert.Equal(1, inbox.GetStatus().Rejected);                                 // el reciente se queda
        Assert.False(File.Exists(tmpViejo));
    }

    [Fact]
    public void Estado_IncluyeEspacioLibreEnDisco()
    {
        using var inbox = Crear();
        Assert.True(inbox.GetStatus().FreeDiskMb > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
