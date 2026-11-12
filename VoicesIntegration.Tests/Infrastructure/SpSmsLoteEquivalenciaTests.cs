using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Abstractions.Sms;
using VoicesIntegration.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace VoicesIntegration.Tests.Infrastructure;

/// <summary>
/// EQUIVALENCIA (BD real, solo con VOICES_MEDICION_BD): los MISMOS avisos SMS por el SP 9 (uno por uno, en orden)
/// y por el SP 31 (por lotes) deben dejar EXACTAMENTE las mismas filas y los mismos duplicados.
/// Usa message_id SQA-/SQB- y los borra al final.
/// </summary>
public sealed class SpSmsLoteEquivalenciaTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);
    private static readonly SmsMessageDetail Det1 = new("5512345678", "Hola José \"comillas\"", "telcel", T0.AddMinutes(-5), T0.AddMinutes(-4), null);
    private static readonly SmsMessageDetail Det2 = new("5599999999", null, null, null, null, T0.AddMinutes(-1));

    // (JSON con {P} = prefijo del message_id, segundo de llegada, detalle)
    private static readonly (string Json, int Seg, SmsMessageDetail? Det)[][] Lotes =
    [
        [
            ("""{"message_id":"{P}1","status":"SENT","datetime":"2026-11-08T12:00:00-06:00"}""", 0, null),
            ("""{"message_id":"{P}2","status":"FAILED","batch_id":"B1","origin_identifier":"O1","datetime":"2026-11-08T12:00:01-06:00"}""", 1, Det1),
            ("""{"message_id":"{P}3","status":"SENT","datetime":"2026-11-08T12:05:00-06:00"}""", 2, null),
            ("""{"message_id":"{P}3","status":"DELIVERED","datetime":"2026-11-08T12:01:00-06:00"}""", 3, null),   // más viejo: no gana
            ("""{"message_id":"{P}4","status":"SENT","datetime":"2026-11-08T12:00:00-06:00"}""", 4, null),
            ("""{"message_id":"{P}4","status":"SENT","datetime":"2026-11-08T12:00:00-06:00"}""", 4, null),       // repetido exacto
            ("""{"message_id":"{P}5","status":"QUEUED"}""", 5, null),                                              // sin fecha
            ("""{"message_id":"{P}5","status":"SENT","datetime":"2026-11-08T12:10:00-06:00"}""", 6, Det2),
            ("""{"message_id":"{P}6","status":"ESTADO_MUY_LARGO_QUE_PASA_DE_TREINTA_CARACTERES","datetime":"ayer"}""", 7, null),
        ],
        [
            ("""{"message_id":"{P}1","status":"DELIVERED","datetime":"2026-11-08T12:30:00-06:00"}""", 60, null),
            ("""{"message_id":"{P}2","status":"SENT","datetime":"2026-11-08T11:00:00-06:00"}""", 61, Det2),      // viejo: estado no; detalle sí
            ("""{"message_id":"{P}3","status":"SENT","datetime":"2026-11-08T12:05:00-06:00"}""", 2, null),       // reenvío exacto: duplicado
            ("""{"message_id":"{P}7","status":"SENT","batch_id":"B7","datetime":"2026-11-08T12:31:00Z"}""", 62, null),
            ("""{"message_id":"{P}5","status":"READ","datetime":"2026-11-08T12:10:00-06:00"}""", 63, null),      // misma fecha: SÍ gana (>=)
            ("""{"message_id":"{P}8","status":"SENT","datetime":"2026-11-08T12:40:00-06:00"}""", 64, null),
            ("""{"message_id":"{P}8","status":"FAILED","datetime":"2026-11-08T12:41:00-06:00"}""", 65, Det1),
            ("""{"message_id":"{P}8","status":"SENT","datetime":"2026-11-08T12:40:30-06:00"}""", 66, null),
        ],
    ];

    private static SmsToSave Item(string json, int seg, SmsMessageDetail? det) =>
        new(json, SHA256.HashData(Encoding.UTF8.GetBytes(json)), T0.AddSeconds(seg), det);

    [Fact]
    public async Task MismosSms_Sp9UnoPorUno_YSp31PorLotes_DejanLoMismo()
    {
        var cs = Environment.GetEnvironmentVariable("VOICES_MEDICION_BD");
        if (string.IsNullOrWhiteSpace(cs)) { output.WriteLine("Sin VOICES_MEDICION_BD: no se prueba."); return; }

        var repo = new SmsRepository(new SqlConnectionFactory(cs));
        try
        {
            await Limpiar(cs);
            var nuevosA = new List<bool>();
            var nuevosB = new List<bool>();
            foreach (var lote in Lotes)
            {
                foreach (var (json, seg, det) in lote)   // SP 9: uno por uno, en orden
                {
                    var x = Item(json.Replace("{P}", "SQA-"), seg, det);
                    nuevosA.Add(await repo.RegisterAsync(x.RawPayload, x.PayloadHash, x.ReceivedAtUtc, x.Detail, CancellationToken.None));
                }
                var b = lote.Select(l => Item(l.Json.Replace("{P}", "SQB-"), l.Seg, l.Det)).ToList();
                nuevosB.AddRange(await repo.RegisterBatchAsync(b, CancellationToken.None));
            }

            Assert.Equal(nuevosA, nuevosB);

            var sms = await Diferencias(cs, """
                SELECT REPLACE([message_id], '{P}', '') AS ID, [to], [message], [status], [operator], [batch_id], [origin_identifier],
                       [created_at], [sent_at], [failed_at], [status_at], DETALLE_OBTENIDO, ORIGEN, FECHA_REGISTRO, FECHA_ULTIMO_EVENTO, FECHA_SMS
                  FROM dbo.TBL_SMS_VOICES WHERE [message_id] LIKE '{P}%'
                """);
            var eventos = await Diferencias(cs, """
                SELECT REPLACE([message_id], '{P}', '') AS ID, TIPO, [batch_id], [status], [status_at], FECHA_RECEPCION
                  FROM dbo.TBL_EVENTOS_SMS_VOICES WHERE [message_id] LIKE '{P}%'
                """);
            output.WriteLine($"SMS: {sms.Filas} por cada SP, diferencias {sms.Distintas}. Eventos: {eventos.Filas}, diferencias {eventos.Distintas}.");
            Assert.True(sms.Filas >= 8);
            Assert.Equal(0, sms.Distintas);
            Assert.Equal(0, eventos.Distintas);
        }
        finally
        {
            await Limpiar(cs);
        }
    }

    private static async Task<(int Filas, int Distintas)> Diferencias(string cs, string select)
    {
        var a = select.Replace("{P}", "SQA-");
        var b = select.Replace("{P}", "SQB-");
        var sql = $"""
            SELECT (SELECT COUNT(*) FROM ({a}) x),
                   (SELECT COUNT(*) FROM (SELECT * FROM ({a}) x EXCEPT SELECT * FROM ({b}) y) d1)
                 + (SELECT COUNT(*) FROM (SELECT * FROM ({b}) y EXCEPT SELECT * FROM ({a}) x) d2)
            """;
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (rd.GetInt32(0), rd.GetInt32(1));
    }

    private static async Task Limpiar(string cs)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("""
            DELETE FROM dbo.TBL_EVENTOS_SMS_VOICES WHERE [message_id] LIKE 'SQA-%' OR [message_id] LIKE 'SQB-%';
            DELETE FROM dbo.TBL_SMS_VOICES WHERE [message_id] LIKE 'SQA-%' OR [message_id] LIKE 'SQB-%';
            """, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
