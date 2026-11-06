using VoicesIntegration.Application.Abstractions.Sms;

namespace VoicesIntegration.Application.Abstractions.Persistence;

/// <summary>Persistencia de SMS (SP dbo.USP_GUARDAR_SMS_VOICES). Aquí no se sabe nada de SQL.</summary>
public interface ISmsRepository
{
    /// <summary>Guarda el aviso. Devuelve false si ya existía (reenvío de Voices).</summary>
    Task<bool> RegisterAsync(
        string rawPayload,
        byte[] payloadHash,
        DateTimeOffset receivedAtUtc,
        SmsMessageDetail? detail,
        CancellationToken cancellationToken);
}
