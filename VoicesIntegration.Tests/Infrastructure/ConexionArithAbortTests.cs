using Microsoft.Data.SqlClient;
using VoicesIntegration.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace VoicesIntegration.Tests.Infrastructure;

public class ConexionArithAbortTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Conexion_QuedaConArithAbortOn_AlAbrirYAlReusarDelPool()
    {
        var cs = Environment.GetEnvironmentVariable("VOICES_MEDICION_BD");
        if (string.IsNullOrWhiteSpace(cs)) { output.WriteLine("Sin VOICES_MEDICION_BD: no se prueba."); return; }
        var factory = new SqlConnectionFactory(cs);

        for (var vuelta = 1; vuelta <= 3; vuelta++)
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
