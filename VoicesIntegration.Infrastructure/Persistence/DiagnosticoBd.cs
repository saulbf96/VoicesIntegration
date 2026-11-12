using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using VoicesIntegration.Application.Abstractions.Monitoring;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>
/// Pruebas cortas (≈1 minuto) para ubicar el freno al guardar (2026-11-07: ~40 avisos/s sin importar el método).
/// No deja nada en la BD: la tabla temporal desaparece sola y los inserts de prueba se deshacen (ROLLBACK).
/// </summary>
internal sealed class DiagnosticoBd(ISqlConnectionFactory factory) : IDiagnosticoBd
{
    public async Task<DiagnosticoBdResultado> MedirAsync(CancellationToken ct)
    {
        await using var c = factory.CreateForBulk();
        await c.OpenAsync(ct);

        // 1) Latencia: 20 idas y vueltas sin trabajo.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            await using var cmd = new SqlCommand("SELECT 1", c) { CommandTimeout = 60 };
            await cmd.ExecuteScalarAsync(ct);
        }
        var latencia = sw.Elapsed.TotalMilliseconds / 20;

        // 2) Red: mandar 5 MB y que SQL solo diga cuánto recibió.
        var texto = new string('x', 2_621_440);   // 2.5 M caracteres = 5 MB en UTF-16
        sw.Restart();
        await using (var cmd = new SqlCommand("SELECT DATALENGTH(@p)", c) { CommandTimeout = 300 })
        {
            cmd.Parameters.Add("@p", SqlDbType.NVarChar, -1).Value = texto;
            await cmd.ExecuteScalarAsync(ct);
        }
        var redMbs = 5.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);

        // 3) Carga masiva de 2,000 filas (~2.3 KB) a una tabla temporal (#): red + tempdb.
        await using (var cmd = new SqlCommand("CREATE TABLE #diag (ID INT NOT NULL, TXT NVARCHAR(MAX) NOT NULL);", c))
            await cmd.ExecuteNonQueryAsync(ct);
        var t = new DataTable();
        t.Columns.Add("ID", typeof(int));
        t.Columns.Add("TXT", typeof(string));
        var fila = new string('d', 2300);
        for (var i = 0; i < 2000; i++) t.Rows.Add(i, fila);
        sw.Restart();
        using (var bulk = new SqlBulkCopy(c) { DestinationTableName = "#diag", BulkCopyTimeout = 300 })
            await bulk.WriteToServerAsync(t, ct);
        var tempdb = sw.Elapsed.TotalSeconds;

        // 4) BD real: 500 inserts en la tabla de eventos (con sus índices) y se DESHACEN.
        sw.Restart();
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync(ct))
        {
            await using (var cmd = new SqlCommand("""
                INSERT INTO dbo.TBL_EVENTOS_LLAMADAS_VOICES
                    ([identifier], TIPO_LLAMADA, [status], PAYLOAD_HASH, FECHA_RECEPCION)
                SELECT TOP (500) 'DIAG-' + CAST(NEWID() AS VARCHAR(36)), 'INITIAL', 'SENT',
                       HASHBYTES('SHA2_256', CAST(NEWID() AS VARCHAR(36))), SYSDATETIME()
                  FROM sys.all_columns;
                """, c, tx) { CommandTimeout = 300 })
                await cmd.ExecuteNonQueryAsync(ct);
            await tx.RollbackAsync(ct);
        }
        var bdReal = sw.Elapsed.TotalSeconds;

        var interp = (redMbs, bdReal, latencia) switch
        {
            _ when redMbs < 2 => $"RED LENTA: solo {redMbs:N1} MB/s entre la API y la BD. Cada aviso pesa ~5 KB → máximo ~{redMbs * 1024 / 5:N0} avisos/s. Hay que comprimir y/o revisar el enlace (TI/redes).",
            _ when bdReal > 5 => $"SERVIDOR DE BD LENTO: 500 inserts tardaron {bdReal:N1} s (normal < 0.5 s). Disco o log del 205, o mucha carga de otros procesos (DBA).",
            _ when latencia > 50 => $"LATENCIA ALTA: {latencia:N0} ms por ida y vuelta. Conviene mandar lotes grandes y pocas llamadas.",
            _ => "Red y BD responden bien en esta prueba; el freno sería otro (bloqueos en horas pico o el disco del servidor de la API)."
        };
        return new DiagnosticoBdResultado(Math.Round(latencia, 1), Math.Round(redMbs, 2), Math.Round(tempdb, 2), Math.Round(bdReal, 2), interp);
    }
}
