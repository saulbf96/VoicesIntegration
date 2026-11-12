using System.Text.Json;
using VoicesIntegration.Infrastructure.Persistence;

namespace VoicesIntegration.Tests.Infrastructure;

public class DatoClienteTests
{
    [Theory]
    [InlineData("""{"CLIENTEUNICO":"1-1-1779-25596","NOMBRECTE":"X","SALDOTOTAL":"13359"}""", "1-1-1779-25596")]
    [InlineData("""{"NOMBRECTE":"X","DIQUE":"24 a 28","CLIENTEUNICO":"1-1-1779-25596"}""", "1-1-1779-25596")]
    [InlineData("""{"id_unico":"A1"}""", "A1")]
    [InlineData("""{"Id Cliente":"A2"}""", "A2")]
    [InlineData("""{"idCliente":12345}""", "12345")]
    [InlineData("""{"ID_CUENTA":"C9"}""", "C9")]
    [InlineData("""{"Número de Cliente":"N7"}""", "N7")]
    [InlineData("""{"cliente_id":" B3 "}""", "B3")]
    [InlineData("""{"NUM_CTA":"T1"}""", "T1")]
    public void SacaElIdSinImportarNombreNiPosicion(string data, string esperado)
        => Assert.Equal(esperado, DatoCliente.Reducir(data));

    [Fact]
    public void PrefiereClienteUnicoSobreCuenta()
        => Assert.Equal("U1", DatoCliente.Reducir("""{"ID_CUENTA":"C1","IDCLIENTE":"K1","CLIENTE_UNICO":"U1"}"""));

    [Fact]
    public void PrefiereClienteSobreCuenta()
        => Assert.Equal("K1", DatoCliente.Reducir("""{"ID_CUENTA":"C1","IDCLIENTE":"K1"}"""));

    [Theory]
    [InlineData("""{"NOMBRECTE":"AURORA","CLASIFICACIONCTE":"EXTRAJUDICIAL","SALDOTOTAL":"1"}""")]
    [InlineData("""{"prefijo":"19086","saldo":"$47,060.00"}""")]
    [InlineData("""{"CLIENTEUNICO":""}""")]
    [InlineData("""{"CLIENTEUNICO":{"x":1}}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("texto que no es json")]
    public void SinIdReconocido_GuardaTalCual(string data)
        => Assert.Equal(data, DatoCliente.Reducir(data));

    [Fact]
    public void Vacio_SigueVacio()
    {
        Assert.Null(DatoCliente.Reducir((string?)null));
        Assert.Equal("", DatoCliente.Reducir(""));
    }

    [Theory]
    [InlineData("""{"identifier":"x","data":{"NOMBRECTE":"A","CLIENTEUNICO":"1-1-9"}}""", "1-1-9")]
    [InlineData("""{"identifier":"x","data":{"nombre":"Ana"}}""", """{"nombre":"Ana"}""")]
    [InlineData("""{"identifier":"x","data":[1,2]}""", "[1,2]")]
    [InlineData("""{"identifier":"x","data":"texto"}""", null)]
    [InlineData("""{"identifier":"x"}""", null)]
    public void Webhook_GuardaIdOJsonComoAntes(string aviso, string? esperado)
    {
        using var doc = JsonDocument.Parse(aviso);
        Assert.Equal(esperado, BlasterCallRepository.DataAGuardar(doc.RootElement));
    }
}
