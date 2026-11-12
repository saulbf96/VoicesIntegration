namespace VoicesIntegration.Application.Abstractions.Sms;

// Solo existe consulta por id, no hay descarga por fechas
public interface ISmsGateway
{
    Task<SmsMessageDetail?> GetMessageAsync(string messageId, CancellationToken cancellationToken);
}

public sealed record SmsMessageDetail(
    string? To,
    string? Message,
    string? Operator,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? FailedAt);
