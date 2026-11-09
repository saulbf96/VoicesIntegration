using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace VoicesIntegration.Tests.Infrastructure;

/// <summary>
/// MEDICIÓN (no es prueba normal): mete lotes de 2,000 llamadas con el MISMO método de la recuperación
/// (RecoveryRepository.BulkInsertFinalAsync) en la BD indicada y muestra los tiempos. Al final borra lo que insertó.
/// Solo corre si existe la variable VOICES_MEDICION_BD con la cadena de conexión (nunca contra producción por accidente).
/// </summary>
public sealed class RecoveryBulkMedicionTests(ITestOutputHelper output)
{
    private const string Prefijo = "MEDICION-BULK-";

    [Fact]
    public async Task Medir_LotesDe2000_ComoLaRecuperacion()
    {
        var cs = Environment.GetEnvironmentVariable("VOICES_MEDICION_BD");
        if (string.IsNullOrWhiteSpace(cs)) { output.WriteLine("Sin VOICES_MEDICION_BD: no se mide."); return; }

        var lotes = int.TryParse(Environment.GetEnvironmentVariable("VOICES_MEDICION_LOTES"), out var l) ? l : 10;
        var repo = new RecoveryRepository(new SqlConnectionFactory(cs), new OutputLogger(output));
        var rnd = new Random(7);
        try
        {
            for (var b = 0; b < lotes; b++)
            {
                var rows = Enumerable.Range(0, 2000).Select(i => Fila(rnd)).ToList();
                Assert.True(await repo.BulkInsertFinalAsync(rows, CancellationToken.None));
            }
        }
        finally
        {
            await using var c = new SqlConnection(cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand($"DELETE FROM dbo.TBL_LLAMADAS_VOICES WHERE [identifier] LIKE '{Prefijo}%'", c) { CommandTimeout = 0 };
            output.WriteLine($"Limpieza: {await cmd.ExecuteNonQueryAsync()} filas de prueba borradas.");
        }
    }

    [Fact]
    public async Task Medir_CaminoTablaDePaso_MasSp20()
    {
        var cs = Environment.GetEnvironmentVariable("VOICES_MEDICION_BD");
        if (string.IsNullOrWhiteSpace(cs)) { output.WriteLine("Sin VOICES_MEDICION_BD: no se mide."); return; }

        var lotes = int.TryParse(Environment.GetEnvironmentVariable("VOICES_MEDICION_LOTES"), out var l) ? l : 10;
        var repo = new RecoveryRepository(new SqlConnectionFactory(cs), new OutputLogger(output));
        var rnd = new Random(11);
        var filas = int.TryParse(Environment.GetEnvironmentVariable("VOICES_MEDICION_FILAS"), out var f) ? f : 2000;
        var job = Guid.NewGuid();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var b = 0; b < lotes; b++)
            {
                var rows = Enumerable.Range(0, filas).Select(i => Fila(rnd)).ToList();
                await repo.BulkLoadBatchAsync(job, b, rows, CancellationToken.None);
                await repo.InsertBatchAsync(job, b, CancellationToken.None);
            }
            output.WriteLine($"TOTAL {lotes * filas} filas en {sw.ElapsedMilliseconds} ms ({lotes * filas * 1000.0 / sw.ElapsedMilliseconds:N0} filas/s)");

            await using var c = new SqlConnection(cs);
            await c.OpenAsync();
            await using var n = new SqlCommand($"SELECT COUNT(*) FROM dbo.TBL_LLAMADAS_VOICES WHERE [identifier] LIKE '{Prefijo}%' AND ORIGEN = 'RECUPERACION'", c);
            Assert.Equal(lotes * filas, (int)(await n.ExecuteScalarAsync())!);   // todas llegaron a la tabla final
        }
        finally
        {
            await using var c = new SqlConnection(cs);
            await c.OpenAsync();
            await using var cmd = new SqlCommand(
                $"DELETE FROM dbo.TBL_LLAMADAS_VOICES WHERE [identifier] LIKE '{Prefijo}%'; DELETE FROM dbo.TBL_RECUPERACION_CARGA_VOICES WHERE JOB_ID = '{job}';", c) { CommandTimeout = 0 };
            output.WriteLine($"Limpieza: {await cmd.ExecuteNonQueryAsync()} filas de prueba borradas.");
        }
    }

    /// <summary>Llamada inventada del tamaño de una real (~1.5 KB de data), identifier y teléfono al azar como en producción.</summary>
    private static RecoveryRow Fila(Random r)
    {
        var data = "{" + string.Join(",", Enumerable.Range(0, 20).Select(k => $"\"campo{k}\":\"{new string((char)('a' + r.Next(26)), 60)}\"")) + "}";
        var dial = new DateTime(2026, 11, 7, 8, 0, 0).AddSeconds(r.Next(50_000));
        return new RecoveryRow(Prefijo + Guid.NewGuid().ToString("N"), "c" + r.Next(500), "CAMPAÑA PRUEBA " + r.Next(500), "voice",
            "55" + r.Next(10_000_000, 99_999_999), "ivr", data, "mensaje de prueba", 1, 0, r.Next(60), null, null,
            0.5m, dial, null, 200, "OK", false, null, null, "5512345678");
    }

    private sealed class OutputLogger(ITestOutputHelper o) : ILogger<RecoveryRepository>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => o.WriteLine(formatter(state, exception));
    }
}
