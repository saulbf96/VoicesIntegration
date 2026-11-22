using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Abstractions.Sms;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Webhooks.Sms;

public sealed record SmsWebhookPayload
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("message_id")] public string? MessageId { get; init; }
    [JsonPropertyName("batch_id")] public string? BatchId { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("event")] public string? Event { get; init; }
    [JsonPropertyName("campaign_id")] public string? CampaignId { get; init; }
    [JsonPropertyName("number")] public string? Number { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }

    [JsonIgnore] public bool IsBatch => string.Equals(Type, "BATCH", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsReply => string.Equals(Event, "Reply", StringComparison.OrdinalIgnoreCase);
}

// Guarda avisos SMS; si falla la consulta del detalle se guarda igual
public sealed class RegisterSmsEventHandler(
    ISmsRepository repository,
    ISmsGateway gateway,
    ILogger<RegisterSmsEventHandler> logger)
{
    public const int MaxIdLength = 100;
    public const int MaxNumberLength = 20;
    public const int MaxReplyTextLength = 1000;

    public async Task<bool> HandleAsync(SmsWebhookPayload payload, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.IsReply)
            return await HandleReplyAsync(payload, rawPayload, receivedAtUtc, cancellationToken);

        var x = await PrepareAsync(payload, rawPayload, receivedAtUtc, cancellationToken);
        var isNew = await repository.RegisterAsync(x.RawPayload, x.PayloadHash, x.ReceivedAtUtc, x.Detail, cancellationToken);

        logger.LogDebug("SMS {Kind} {Id} {Status} → {Outcome}",
            payload.IsBatch ? "lote" : "mensaje", payload.IsBatch ? payload.BatchId : payload.MessageId,
            payload.Status, isNew ? "nuevo" : "duplicado");

        return isNew;
    }

    public async Task<SmsToSave> PrepareAsync(SmsWebhookPayload payload, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rawPayload);

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

    // Respuesta del cliente (event = Reply)
    private async Task<bool> HandleReplyAsync(SmsWebhookPayload payload, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawPayload);

        if (string.IsNullOrWhiteSpace(payload.MessageId))
            throw new DomainException("Respuesta SMS sin message_id.");
        if (payload.MessageId.Length > MaxIdLength)
            throw new DomainException($"El message_id no puede exceder {MaxIdLength} caracteres.");
        if (string.IsNullOrWhiteSpace(payload.Number))
            throw new DomainException("Respuesta SMS sin number.");
        if (payload.Number.Trim().Length > MaxNumberLength)
            throw new DomainException($"El number no puede exceder {MaxNumberLength} caracteres.");

        DateTimeOffset? createdAt = DateTimeOffset.TryParse(payload.CreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;

        var text = payload.Text is { Length: > MaxReplyTextLength } t ? t[..MaxReplyTextLength] : payload.Text;
        var campaign = payload.CampaignId is { Length: > MaxIdLength } c ? c[..MaxIdLength] : payload.CampaignId;

        var reply = new SmsReplyToSave(
            campaign, payload.MessageId, payload.Number.Trim(), text, createdAt,
            SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload)), receivedAtUtc);

        var isNew = await repository.RegisterReplyAsync(reply, cancellationToken);
        logger.LogDebug("Respuesta SMS {MessageId} → {Outcome}", payload.MessageId, isNew ? "nueva" : "duplicada");
        return isNew;
    }
}
