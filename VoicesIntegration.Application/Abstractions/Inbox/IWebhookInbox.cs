namespace VoicesIntegration.Application.Abstractions.Inbox;

public static class WebhookChannels
{
    public const string Blaster = "blaster";
    public const string Sms = "sms";
}

// Bandeja en disco: el aviso se guarda aqui antes de ir a la BD
public interface IWebhookInbox
{
    Task SaveAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken);

    Task SaveRejectedAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, string reason, CancellationToken cancellationToken);

    IReadOnlyList<InboxItem> GetPending(int max);

    Task<string> ReadAsync(InboxItem item, CancellationToken cancellationToken);

    void Complete(InboxItem item);

    void Reject(InboxItem item, string reason);

    InboxStatus GetStatus();

    int Cleanup(TimeSpan rejectedRetention, TimeSpan tmpMaxAge);

    Task WaitForItemsAsync(TimeSpan maxWait, CancellationToken cancellationToken);
}

public sealed record InboxItem(string Id, string Channel, DateTimeOffset ReceivedAtUtc);

public sealed record InboxStatus(int Pending, int Rejected, DateTimeOffset? OldestPendingUtc, long? FreeDiskMb = null);



