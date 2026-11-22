using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace VoicesIntegration.Tests.Infrastructure;

public sealed class SpLoteV2EquivalenciaTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 16, 0, 0, TimeSpan.Zero);

    private static readonly (string Json, int Seg)[][] Lotes =
    [
        [
            ("""{"identifier":"{ID}1","status":"SENT","call_type":"initial","campaign_id":"C1","campaign_name":"Campaña Ñandú \"comillas\"","channel":"voice","phone_number":"5512345678","type":"ivr","data":{"nombre":"José","deuda":123.45,"tags":["a","b"],"n":null},"message":"hola é","call_attempts":2,"duration_minutes":1,"duration_seconds":75,"credits":0.5,"dialed_at":"2026-11-08T10:00:00-06:00","failed_at":null,"sip_code":200,"sip_description":"OK","a_num":"5500000000","operator":"Telcel","is_mobile":true,"outbound_route":"R1","audio":"https://x/a.mp3"}""", 0),
            ("""{"identifier":"{ID}2","status":"SENT","call_type":"transfer","sip_code":200,"sip_description":"OK tr","duration_minutes":2,"duration_seconds":130,"duration_transfer_minutes":3,"duration_transfer_seconds":190}""", 1),
            ("""{"identifier":"{ID}3","status":"FAILED","call_type":"initial","campaign_name":"C3","data":{"x":1},"sip_code":486,"sip_description":"Busy","failed_at":"2026-11-08T09:59:59.123Z","is_mobile":false,"duration_transfer_minutes":9}""", 2),
            ("""{"identifier":"{ID}3","status":"SENT","call_type":"transfer","sip_code":200,"duration_minutes":4,"duration_seconds":5}""", 3),
            ("""{"identifier":"{ID}4","status":"SENT","data":[1,2,3]}""", 4),
            ("""{"identifier":"{ID}4","status":"SENT","data":[1,2,3]}""", 4),
            ("""{"identifier":"{ID}5","status":"TO_REDIAL","campaign_name":"viejo","data":{"v":1}}""", 5),
            ("""{"identifier":"{ID}5","status":"SENT","campaign_name":"nuevo","data":{"v":2}}""", 6),
            ("""{"identifier":"{ID}6","status":"QUEUED_PROVIDER","credits":"1,5","dialed_at":"ayer","call_attempts":"3","data":"texto","message":"MSG_LARGO","campaign_name":"NOMBRE_LARGO","phone_number":"55-12 34","audio":"AUDIO_LARGO"}""", 7),
            ("""{"identifier":"{ID}7","status":"FAILED"}""", 20),
        ],
        [
            ("""{"identifier":"{ID}1","status":"FAILED","call_type":"initial","campaign_id":"C9","campaign_name":"Otra","data":{"nombre":"Ana"},"sip_code":503,"dialed_at":"2026-11-08T11:00:00+00:00","is_mobile":false}""", 60),
            ("""{"identifier":"{ID}1","status":"SENT","call_type":"transfer","sip_code":200,"sip_description":"tr ok","duration_minutes":7,"duration_seconds":8,"duration_transfer_seconds":44}""", 61),
            ("""{"identifier":"{ID}7","status":"SENT","campaign_name":"más viejo: NO debe pisar"}""", 10),
            ("""{"identifier":"{ID}2","status":"SENT","call_type":"transfer","sip_code":200,"sip_description":"OK tr","duration_minutes":2,"duration_seconds":130,"duration_transfer_minutes":3,"duration_transfer_seconds":190}""", 1),
            ("""{"identifier":"{ID}8","status":"SENT","call_type":"transfer","sip_code":200}""", 62),
            ("""{"identifier":"{ID}8","status":"SENT","call_type":"initial","campaign_name":"inicial después de transfer","data":{"z":true}}""", 63),
        ],
    ];

    private static string Texto(string json, string prefijo) => json
        .Replace("{ID}", prefijo)
        .Replace("MSG_LARGO", new string('m', 4100))
        .Replace("NOMBRE_LARGO", new string('n', 350))
        .Replace("AUDIO_LARGO", new string('a', 450));

    [Fact]
    public async Task MismosAvisos_SpViejoYSpV2_DejanLasMismasFilas()
    {
        var cs = Environment.GetEnvironmentVariable("VOICES_MEDICION_BD");
        if (string.IsNullOrWhiteSpace(cs)) { output.WriteLine("Sin VOICES_MEDICION_BD: no se prueba."); return; }

        var repo = new BlasterCallRepository(new SqlConnectionFactory(cs));
        try
        {
            await Limpiar(cs);
            var dupA = new List<bool>();
            var dupB = new List<bool>();
            foreach (var lote in Lotes)
            {
                var a = lote.Select(x => Preparar(Texto(x.Json, "EQA-"), x.Seg)).ToList();
                var b = lote.Select(x => Preparar(Texto(x.Json, "EQB-"), x.Seg)).ToList();
                dupA.AddRange(await GuardarConSpViejo(cs, a));
                dupB.AddRange((await repo.RegisterCallResultsBatchAsync(b, CancellationToken.None)).Select(n => !n));
            }

            Assert.Equal(dupA, dupB);

            var llamadas = await Diferencias(cs, """
                SELECT REPLACE([identifier], '{P}', '') AS ID, [campaign_id], [campaign_name], [channel], [phone_number], [type],
                       [data], [message], [call_attempts], [duration_minutes], [duration_seconds], [duration_transfer_minutes],
                       [duration_transfer_seconds], [credits], [dialed_at], [failed_at], [sip_code], [sip_description], [transfer],
                       [sip_code_transfer], [sip_description_transfer], [a_num], [status], [operator], [is_mobile], [outbound_route],
                       [audio], ORIGEN, FECHA_REGISTRO, FECHA_ULTIMO_EVENTO, FECHA_LLAMADA
                  FROM dbo.TBL_LLAMADAS_VOICES WHERE [identifier] LIKE '{P}%'
                """);
            // El SP viejo anota cada aviso en TBL_EVENTOS_LLAMADAS_VOICES; el V2 (script 45) solo su huella
            // en TBL_HUELLAS_LLAMADAS_VOICES. Deben quedar los mismos avisos (identifier + hora; el hash cambia porque el JSON lleva EQA/EQB).
            var huellas = await DiferenciasHuellas(cs);

            output.WriteLine($"Llamadas: {llamadas.Filas} por cada SP, diferencias {llamadas.Distintas}. Avisos: {huellas.Filas}, diferencias {huellas.Distintas}.");
            Assert.True(llamadas.Filas >= 8, "deben existir las 8 llamadas de prueba");
            Assert.Equal(0, llamadas.Distintas);
            Assert.True(huellas.Filas >= 12, "deben existir las huellas de los avisos de prueba");
            Assert.Equal(0, huellas.Distintas);

            // Paso 1 de /recuperar (SP 12): los avisos por minuto que anota el V2 deben ser los mismos que contaba eventos.
            var porMinuto = await DiferenciasAvisosPorMinuto(cs);
            output.WriteLine($"Avisos por minuto: {porMinuto.Filas} minutos, diferencias {porMinuto.Distintas}.");
            Assert.True(porMinuto.Filas >= 2, "deben existir avisos en al menos 2 minutos");
            Assert.Equal(0, porMinuto.Distintas);
        }
        finally
        {
            await Limpiar(cs);
        }
    }

    private static CallEventToSave Preparar(string json, int seg) =>
        RegisterCallResultHandler.Prepare(JsonSerializer.Deserialize<CallResultPayload>(json)!, json, T0.AddSeconds(seg));

    private static async Task<IReadOnlyList<bool>> GuardarConSpViejo(string cs, IReadOnlyList<CallEventToSave> events)
    {
        var t = new DataTable();
        t.Columns.Add("FILA", typeof(int));
        t.Columns.Add("IDENTIFIER", typeof(string));
        t.Columns.Add("TIPO_LLAMADA", typeof(string));
        t.Columns.Add("STATUS", typeof(string));
        t.Columns.Add("SIP_CODE", typeof(int));
        t.Columns.Add("SIP_DESCRIPTION", typeof(string));
        t.Columns.Add("DURATION_MINUTES", typeof(int));
        t.Columns.Add("DURATION_SECONDS", typeof(int));
        t.Columns.Add("PAYLOAD_HASH", typeof(byte[]));
        t.Columns.Add("RAW_PAYLOAD", typeof(string));
        t.Columns.Add("RECEIVED_AT_UTC", typeof(DateTime));
        for (var i = 0; i < events.Count; i++)
        {
            var (r, raw, hash) = (events[i].Result, events[i].RawPayload, events[i].PayloadHash);
            t.Rows.Add(i, r.MessageId, r.CallType == CallType.Transfer ? "TRANSFER" : "INITIAL", r.StatusText,
                (object?)r.SipCode ?? DBNull.Value, (object?)r.SipDescription ?? DBNull.Value,
                (object?)r.DurationMinutes ?? DBNull.Value, (object?)r.DurationSeconds ?? DBNull.Value,
                hash, raw, r.ReceivedAtUtc.UtcDateTime);
        }

        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("dbo.USP_GUARDAR_LLAMADAS_LOTE_VOICES", c) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add(new SqlParameter("@Avisos", SqlDbType.Structured) { TypeName = "dbo.TT_AVISOS_LLAMADA_VOICES", Value = t });
        var dup = new bool[events.Count];
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) dup[rd.GetInt32(0)] = rd.GetBoolean(1);
        return dup;
    }

    private static async Task<(int Filas, int Distintas)> Diferencias(string cs, string select)
    {
        var a = select.Replace("{P}", "EQA-");
        var b = select.Replace("{P}", "EQB-");
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

    private static async Task<(int Filas, int Distintas)> DiferenciasHuellas(string cs)
    {
        const string a = "SELECT REPLACE([identifier], 'EQA-', '') AS ID, FECHA_RECEPCION FROM dbo.TBL_EVENTOS_LLAMADAS_VOICES WHERE [identifier] LIKE 'EQA-%'";
        const string b = "SELECT REPLACE([identifier], 'EQB-', '') AS ID, FECHA_RECEPCION FROM dbo.TBL_HUELLAS_LLAMADAS_VOICES WHERE [identifier] LIKE 'EQB-%'";
        var sql = $"""
            SELECT (SELECT COUNT(*) FROM ({b}) y),
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

    // Los avisos de prueba caen el 08/11/2026 entre 10:00 y 10:02 (hora Mexico).
    private const string RangoPrueba = "MINUTO >= '2026-11-08T09:59:00' AND MINUTO < '2026-11-08T10:03:00'";

    private static async Task<(int Filas, int Distintas)> DiferenciasAvisosPorMinuto(string cs)
    {
        const string a = "SELECT DATEADD(MINUTE, DATEDIFF(MINUTE, 0, FECHA_RECEPCION), CAST(0 AS DATETIME)) AS M, COUNT(*) AS N FROM dbo.TBL_EVENTOS_LLAMADAS_VOICES WHERE [identifier] LIKE 'EQA-%' GROUP BY DATEADD(MINUTE, DATEDIFF(MINUTE, 0, FECHA_RECEPCION), CAST(0 AS DATETIME))";
        const string b = $"SELECT CAST(MINUTO AS DATETIME) AS M, SUM(AVISOS) AS N FROM dbo.TBL_AVISOS_POR_MINUTO_VOICES WHERE CANAL = 'L' AND {RangoPrueba} GROUP BY MINUTO";
        var sql = $"""
            SELECT (SELECT COUNT(*) FROM ({b}) y),
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
        await using var cmd = new SqlCommand($"""
            DELETE FROM dbo.TBL_EVENTOS_LLAMADAS_VOICES WHERE [identifier] LIKE 'EQA-%' OR [identifier] LIKE 'EQB-%';
            DELETE FROM dbo.TBL_HUELLAS_LLAMADAS_VOICES WHERE [identifier] LIKE 'EQA-%' OR [identifier] LIKE 'EQB-%';
            DELETE FROM dbo.TBL_LLAMADAS_VOICES WHERE [identifier] LIKE 'EQA-%' OR [identifier] LIKE 'EQB-%';
            IF OBJECT_ID(N'dbo.TBL_AVISOS_POR_MINUTO_VOICES', N'U') IS NOT NULL
                DELETE FROM dbo.TBL_AVISOS_POR_MINUTO_VOICES WHERE {RangoPrueba};
            """, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
