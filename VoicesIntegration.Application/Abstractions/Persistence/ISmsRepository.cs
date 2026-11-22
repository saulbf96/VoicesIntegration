using VoicesIntegration.Application.Abstractions.Sms;

namespace VoicesIntegration.Application.Abstractions.Persistence;

public interface ISmsRepository
{
    Task<bool> RegisterAsync(
        string rawPayload,
        byte[] payloadHash,
        DateTimeOffset receivedAtUtc,
        SmsMessageDetail? detail,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<bool>> RegisterBatchAsync(IReadOnlyList<SmsToSave> items, CancellationToken cancellationToken);

    Task<bool> RegisterReplyAsync(SmsReplyToSave reply, CancellationToken cancellationToken);
}

public sealed record SmsToSave(string RawPayload, byte[] PayloadHash, DateTimeOffset ReceivedAtUtc, SmsMessageDetail? Detail);

public sealed record SmsReplyToSave(
    string? CampaignId,
    string MessageId,
    string Number,
    string? Text,
    DateTimeOffset? CreatedAtUtc,
    byte[] PayloadHash,
    DateTimeOffset ReceivedAtUtc);
