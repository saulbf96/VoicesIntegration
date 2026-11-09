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

    /// <summary>
    /// Guarda MUCHOS avisos de un mensaje (no "resumen de lote") en una sola llamada (SP 31), en el orden de la lista.
    /// Devuelve, por posición, true si era nuevo. Si la BD rechaza el lote por un dato: <see cref="BatchRejectedException"/>.
    /// </summary>
    Task<IReadOnlyList<bool>> RegisterBatchAsync(IReadOnlyList<SmsToSave> items, CancellationToken cancellationToken);
}

/// <summary>Un aviso SMS listo para guardar (ya validado; detalle opcional).</summary>
public sealed record SmsToSave(string RawPayload, byte[] PayloadHash, DateTimeOffset ReceivedAtUtc, SmsMessageDetail? Detail);
