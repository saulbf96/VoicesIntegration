using Microsoft.Data.SqlClient;
using VoicesIntegration.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace VoicesIntegration.Tests.Infrastructure;

/// <summary>
/// Las conexiones de la API deben quedar con ARITHABORT ON (como SSMS). Con OFF, SQL usaba otro plan del SP del webhook:
/// 43.5 s contra 6–15 s por 1,000 avisos en el 205 (09/11/2026). Solo corre con la variable VOICES_MEDICION_BD.
/// </summary>
public class ConexionArithAbortTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Conexion_QuedaConArithAbortOn_AlAbrirYAlReusarDelPool()
    {
        var cs = Environment.GetEnvironmentVariable("VOICES_MEDICION_BD");
        if (string.IsNullOrWhiteSpace(cs)) { output.WriteLine("Sin VOICES_MEDICION_BD: no se prueba."); return; }
        var factory = new SqlConnectionFactory(cs);

        for (var vuelta = 1; vuelta <= 3; vuelta++)   // la 2a y 3a vez la conexión sale del pool
        {
            await using var c = factory.Create();
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT CAST(SESSIONPROPERTY('ARITHABORT') AS INT)", c);
            Assert.Equal(1, (int)(await cmd.ExecuteScalarAsync())!);
        }

        await using var bulk = factory.CreateForBulk();
        bulk.Open();
        await using var cmd2 = new SqlCommand("SELECT CAST(SESSIONPROPERTY('ARITHABORT') AS INT)", bulk);
        Assert.Equal(1, (int)(await cmd2.ExecuteScalarAsync())!);
    }
}
