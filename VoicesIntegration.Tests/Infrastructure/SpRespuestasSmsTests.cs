using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using VoicesIntegration.Application.Abstractions.Sms;
using VoicesIntegration.Application.Features.Webhooks.Sms;
using VoicesIntegration.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace VoicesIntegration.Tests.Infrastructure;

/// <summary>
/// BD real (solo con VOICES_MEDICION_BD; scripts 40/41 corridos): una respuesta de cliente (event = Reply)
/// pasa por el handler y el SP 41, se guarda tal cual en hora México, y su reenvío exacto es duplicado.
/// Usa message_id RSP-... y lo borra al final.
/// </summary>
public sealed class SpRespuestasSmsTests(ITestOutputHelper output)
{
    private sealed class SinGateway : ISmsGateway
    {
        public Task<SmsMessageDetail?> GetMessageAsync(string messageId, CancellationToken ct) =>
            throw new InvalidOperationException("Una respuesta no debe consultar a Voices.");
    }

    [Fact]
    public async Task Respuesta_SeGuardaEnHoraMexico_YElReenvioEsDuplicado()
    {
        var cs = Environment.GetEnvironmentVariable("VOICES_MEDICION_BD");
        if (string.IsNullOrWhiteSpace(cs)) { output.WriteLine("Sin VOICES_MEDICION_BD: no se prueba."); return; }

        var id = "RSP-" + Guid.NewGuid().ToString("N");
        // Misma forma que mandó Voices el 11/11/2026.
        var raw = $$"""{"number":"5500000000","message_id":"{{id}}","created_at":"2026-11-11T17:29:00.727Z","event":"Reply","campaign_id":"6af4a58fe40c267803a6684a","text":"Negociar ñ \"comillas\""}""";
        var payload = JsonSerializer.Deserialize<SmsWebhookPayload>(raw)!;
        var h = new RegisterSmsEventHandler(new SmsRepository(new SqlConnectionFactory(cs)), new SinGateway(),
            NullLogger<RegisterSmsEventHandler>.Instance);
        var recibido = new DateTimeOffset(2026, 11, 11, 17, 29, 1, TimeSpan.Zero);

        try
        {
            Assert.True(await h.HandleAsync(payload, raw, recibido, CancellationToken.None));
            Assert.False(await h.HandleAsync(payload, raw, recibido, CancellationToken.None));   // reenvío exacto

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT COUNT(*), MAX([text]), MAX([number]), MAX(FECHA_RESPUESTA), MAX(FECHA_RECEPCION), MAX([campaign_id]) " +
                "FROM dbo.TBL_RESPUESTAS_SMS_VOICES WHERE [message_id] = @id", cn);
            cmd.Parameters.AddWithValue("@id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(1, r.GetInt32(0));
            Assert.Equal("Negociar ñ \"comillas\"", r.GetString(1));
            Assert.Equal("5500000000", r.GetString(2));
            Assert.Equal(new DateTime(2026, 11, 11, 11, 29, 0, 727), r.GetDateTime(3));   // UTC-6
            Assert.Equal(new DateTime(2026, 11, 11, 11, 29, 1), r.GetDateTime(4));
            Assert.Equal("6af4a58fe40c267803a6684a", r.GetString(5));
        }
        finally
        {
            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync();
            await using var del = new SqlCommand("DELETE FROM dbo.TBL_RESPUESTAS_SMS_VOICES WHERE [message_id] = @id", cn);
            del.Parameters.AddWithValue("@id", id);
            await del.ExecuteNonQueryAsync();
        }
    }
}
