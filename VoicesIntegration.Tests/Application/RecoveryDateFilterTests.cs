using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Features.Recovery;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Tests.Application;

public class RecoveryDateFilterTests
{
    // 14/11/2026 12:00 Mexico (UTC-6)
    private static readonly DateTimeOffset Ahora = new(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
    private static readonly InboxStatus BandejaVacia = new(0, 0, null);

    [Fact]
    public void Corte_es_hoy_a_medianoche_Mexico_con_bandeja_vacia() =>
        Assert.Equal(new DateTime(2026, 11, 14), RecoveryDateFilter.CalcularCorte(Ahora, BandejaVacia));

    [Fact]
    public void Corte_usa_hora_Mexico_no_UTC()
    {
        // 15/11 02:00 UTC = 14/11 20:00 Mexico
        var noche = new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTime(2026, 11, 14), RecoveryDateFilter.CalcularCorte(noche, BandejaVacia));
    }

    [Fact]
    public void Corte_baja_al_dia_del_pendiente_mas_viejo()
    {
        var bandeja = new InboxStatus(500, 0, new DateTimeOffset(2026, 11, 13, 23, 0, 0, TimeSpan.Zero)); // 13/11 17:00 Mexico
        Assert.Equal(new DateTime(2026, 11, 13), RecoveryDateFilter.CalcularCorte(Ahora, bandeja));
    }

    [Fact]
    public void Pendientes_de_hoy_no_cambian_el_corte()
    {
        var bandeja = new InboxStatus(500, 0, Ahora.AddHours(-1));
        Assert.Equal(new DateTime(2026, 11, 14), RecoveryDateFilter.CalcularCorte(Ahora, bandeja));
    }

    [Fact]
    public void Omite_lo_de_hoy_y_cuenta_omitidas()
    {
        var f = new RecoveryDateFilter(new DateTime(2026, 11, 14));
        Assert.True(f.Incluir(new DateTime(2026, 11, 13, 23, 59, 59)));
        Assert.False(f.Incluir(new DateTime(2026, 11, 14, 0, 0, 0)));
        Assert.False(f.Incluir(new DateTime(2026, 11, 14, 9, 30, 0)));
        Assert.True(f.Incluir(null));
        Assert.Equal(2, f.Omitidas);
    }

    [Fact]
    public void Siete_dias_se_permiten()
    {
        var f = new RecoveryDateFilter(new DateTime(2026, 11, 14));
        Assert.True(f.Incluir(new DateTime(2026, 11, 7, 0, 1, 0)));
        Assert.True(f.Incluir(new DateTime(2026, 11, 13, 23, 0, 0)));
        Assert.Null(f.Rechazo);
    }

    [Fact]
    public void Ocho_dias_se_rechazan_al_detectarlo()
    {
        var f = new RecoveryDateFilter(new DateTime(2026, 11, 14));
        Assert.True(f.Incluir(new DateTime(2026, 11, 13, 10, 0, 0)));
        var ex = Assert.Throws<DomainException>(() => f.Incluir(new DateTime(2026, 11, 6, 10, 0, 0)));
        Assert.Contains("más de 7 días", ex.Message);
        Assert.Equal(ex.Message, f.Rechazo);
    }

    [Fact]
    public void Lo_de_hoy_no_cuenta_para_los_7_dias()
    {
        var f = new RecoveryDateFilter(new DateTime(2026, 11, 14));
        Assert.True(f.Incluir(new DateTime(2026, 11, 8, 10, 0, 0)));
        Assert.False(f.Incluir(new DateTime(2026, 11, 14, 10, 0, 0)));
        Assert.Null(f.Rechazo);
    }

    [Fact]
    public async Task Filtrar_deja_pasar_solo_lo_anterior_al_corte()
    {
        var f = new RecoveryDateFilter(new DateTime(2026, 11, 14));
        var fechas = new DateTime?[] { new DateTime(2026, 11, 13), new DateTime(2026, 11, 14, 8, 0, 0), null };
        var salida = new List<DateTime?>();
        await foreach (var x in f.Filtrar(fechas.ToAsyncEnumerable(), d => d)) salida.Add(x);
        Assert.Equal([new DateTime(2026, 11, 13), null], salida);
        Assert.Equal(1, f.Omitidas);
    }

    [Fact]
    public void Motivo_dice_de_hoy_o_el_dia_de_la_bandeja()
    {
        Assert.Equal("de hoy", new RecoveryDateFilter(new DateTime(2026, 11, 14)).MotivoCorte(Ahora));
        Assert.Contains("13/11/2026", new RecoveryDateFilter(new DateTime(2026, 11, 13)).MotivoCorte(Ahora));
    }
}
