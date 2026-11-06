namespace VoicesIntegration.Application.Abstractions.Inbox;

/// <summary>Canales que llegan por webhook. Van en el nombre del archivo de la bandeja.</summary>
public static class WebhookChannels
{
    public const string Blaster = "blaster";
    public const string Sms = "sms";
}

/// <summary>
/// Bandeja de entrada DURABLE de webhooks: cada aviso se guarda aquí en cuanto llega
/// (antes de tocar la BD). Así, si la BD está lenta o caída, el aviso NO se pierde:
/// espera en la bandeja y se reintenta. La implementación (archivos en disco) está en Infrastructure.
/// </summary>
public interface IWebhookInbox
{
    /// <summary>Guarda el aviso tal cual llegó. Cuando termina, el aviso ya está a salvo en disco.</summary>
    Task SaveAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken);

    /// <summary>Guarda directo en "rechazados" un aviso que ni siquiera es JSON válido.</summary>
    Task SaveRejectedAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, string reason, CancellationToken cancellationToken);

    /// <summary>Los más antiguos primero (respeta el orden de llegada).</summary>
    IReadOnlyList<InboxItem> GetPending(int max);

    Task<string> ReadAsync(InboxItem item, CancellationToken cancellationToken);

    /// <summary>Ya se guardó en BD: se quita de la bandeja.</summary>
    void Complete(InboxItem item);

    /// <summary>Dato inválido (no tiene caso reintentar): se mueve a "rechazados" con el motivo.</summary>
    void Reject(InboxItem item, string reason);

    InboxStatus GetStatus();

    /// <summary>Borra rechazados viejos y temporales abandonados para no llenar el disco. Devuelve cuántos borró.</summary>
    int Cleanup(TimeSpan rejectedRetention, TimeSpan tmpMaxAge);

    /// <summary>Espera hasta que llegue un aviso nuevo o pase <paramref name="maxWait"/>.</summary>
    Task WaitForItemsAsync(TimeSpan maxWait, CancellationToken cancellationToken);
}


public sealed record InboxItem(string Id, string Channel, DateTimeOffset ReceivedAtUtc);

/// <param name="FreeDiskMb">Espacio libre (MB) en el disco de la bandeja; null si no se pudo leer.</param>
public sealed record InboxStatus(int Pending, int Rejected, DateTimeOffset? OldestPendingUtc, long? FreeDiskMb = null);

