using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Application.Features.Recovery;

namespace VoicesIntegration.Tests.Application;

public class RecoveryDayRuleTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 9, 18, 0, 0, TimeSpan.Zero);
    private static readonly InboxStatus BandejaVacia = new(0, 0, null);

    private static RecoveryJob Job(DateTime? hasta) => new() { Hasta = hasta };

    [Fact]
    public void Dia_anterior_con_bandeja_vacia_se_permite() =>
        Assert.Null(RecoveryDayRule.WhyNot(Job(new DateTime(2026, 11, 8, 20, 0, 0)), BandejaVacia, Ahora));

    [Fact]
    public void Mismo_dia_se_rechaza() =>
        Assert.Contains("MISMO día", RecoveryDayRule.WhyNot(Job(new DateTime(2026, 11, 9, 8, 0, 0)), BandejaVacia, Ahora));

    [Fact]
    public void Archivo_que_termina_hoy_se_rechaza_aunque_empiece_ayer() =>
        Assert.NotNull(RecoveryDayRule.WhyNot(Job(new DateTime(2026, 11, 9, 0, 5, 0)), BandejaVacia, Ahora));

    [Fact]
    public void Medianoche_Mexico_no_UTC()
    {
        var noche = new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
        Assert.NotNull(RecoveryDayRule.WhyNot(Job(new DateTime(2026, 11, 9, 19, 0, 0)), BandejaVacia, noche));
    }

    [Fact]
    public void Pendientes_de_ese_dia_en_la_bandeja_se_rechaza()
    {
        var bandeja = new InboxStatus(500_000, 0, new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero));
        Assert.Contains("sin guardar", RecoveryDayRule.WhyNot(Job(new DateTime(2026, 11, 8, 20, 0, 0)), bandeja, Ahora));
    }

    [Fact]
    public void Pendientes_solo_de_hoy_no_bloquean_un_dia_anterior()
    {
        var bandeja = new InboxStatus(20_000, 0, new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero));
        Assert.Null(RecoveryDayRule.WhyNot(Job(new DateTime(2026, 11, 8, 20, 0, 0)), bandeja, Ahora));
    }

    [Fact]
    public void Sin_fechas_no_hay_nada_que_bloquear() =>
        Assert.Null(RecoveryDayRule.WhyNot(Job(null), BandejaVacia, Ahora));
}
