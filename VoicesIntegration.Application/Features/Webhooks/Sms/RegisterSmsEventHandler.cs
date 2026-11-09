using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Abstractions.Sms;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Webhooks.Sms;

/// <summary>
/// Campos mínimos de los webhooks de SMS de Voices para validar:
/// - "Message status": message_id, status, datetime (+ batch_id, origin_identifier opcionales).
/// - "Batch status":   type = "BATCH", batch_id, status, messages[]...
/// El JSON completo se guarda aparte (RAW_PAYLOAD) y el SP lee de ahí el resto.
/// </summary>
public sealed record SmsWebhookPayload
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("message_id")] public string? MessageId { get; init; }
    [JsonPropertyName("batch_id")] public string? BatchId { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonIgnore] public bool IsBatch => string.Equals(Type, "BATCH", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Caso de uso: guardar un aviso de SMS. Si es de un mensaje, intenta completar número y texto
/// con GET /v1/sms/messages/{id}; si esa consulta falla, SE GUARDA IGUAL (sin detalle).
/// </summary>
public sealed class RegisterSmsEventHandler(
    ISmsRepository repository,
    ISmsGateway gateway,
    ILogger<RegisterSmsEventHandler> logger)
{
    public const int MaxIdLength = 100;

    /// <returns>true si es nuevo; false si era un duplicado.</returns>
    public async Task<bool> HandleAsync(SmsWebhookPayload payload, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        var x = await PrepareAsync(payload, rawPayload, receivedAtUtc, cancellationToken);
        var isNew = await repository.RegisterAsync(x.RawPayload, x.PayloadHash, x.ReceivedAtUtc, x.Detail, cancellationToken);

        logger.LogDebug("SMS {Kind} {Id} {Status} → {Outcome}",
            payload.IsBatch ? "lote" : "mensaje", payload.IsBatch ? payload.BatchId : payload.MessageId,
            payload.Status, isNew ? "nuevo" : "duplicado");

        return isNew;
    }

    /// <summary>
    /// Valida el aviso y (si está prendido) consulta el detalle; NO guarda. Lo usa el guardado por lotes (SP 31).
    /// Si el dato es inválido: <see cref="DomainException"/> (el aviso va a "rechazados").
    /// </summary>
    public async Task<SmsToSave> PrepareAsync(SmsWebhookPayload payload, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rawPayload);

        // ---- Validaciones (si fallan → DomainException → el aviso va a "rechazados") ----
        if (payload.IsBatch)
        {
            if (string.IsNullOrWhiteSpace(payload.BatchId))
                throw new DomainException("Aviso de lote SMS sin batch_id.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(payload.MessageId))
                throw new DomainException("Aviso SMS sin message_id.");
            if (payload.MessageId.Length > MaxIdLength)
                throw new DomainException($"El message_id no puede exceder {MaxIdLength} caracteres.");
            if (string.IsNullOrWhiteSpace(payload.Status))
                throw new DomainException("Aviso SMS sin status.");
        }

        // ---- Detalle (número, texto, fechas): solo para avisos de un mensaje, sin bloquear ----
        SmsMessageDetail? detail = null;
        if (!payload.IsBatch)
        {
            try
            {
                detail = await gateway.GetMessageAsync(payload.MessageId!, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("No se pudo obtener el detalle del SMS {MessageId}; se guarda sin él. {Error}",
                    payload.MessageId, ex.Message);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return new SmsToSave(rawPayload, hash, receivedAtUtc, detail);
    }
}
