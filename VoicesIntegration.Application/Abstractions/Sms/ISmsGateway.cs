namespace VoicesIntegration.Application.Abstractions.Sms;

/// <summary>
/// Consulta a la API de SMS de Voices. Solo existe "consultar uno por id"
/// (GET /v1/sms/messages/{id}); NO hay descarga por fechas.
/// Se usa para completar número y texto, que el webhook de SMS no trae.
/// </summary>
public interface ISmsGateway
{
    /// <summary>Detalle del mensaje, o null si no existe / la consulta está desactivada.</summary>
    Task<SmsMessageDetail?> GetMessageAsync(string messageId, CancellationToken cancellationToken);
}

public sealed record SmsMessageDetail(
    string? To,
    string? Message,
    string? Operator,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? FailedAt);
