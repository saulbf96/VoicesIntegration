using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Persistence;
using DomainCallResult = VoicesIntegration.Domain.Blaster.CallResult;

namespace VoicesIntegration.Application.Features.Webhooks.CallResult;

public enum RegisterCallResultOutcome
{
    Registered,
    Duplicate
}

// Guarda un aviso de Blaster; si Voices lo reenvia no se duplica
public sealed class RegisterCallResultHandler(
    IBlasterCallRepository repository,
    ILogger<RegisterCallResultHandler> logger)
{
    public async Task<RegisterCallResultOutcome> HandleAsync(
        CallResultPayload payload,
        string rawPayload,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        var e = Prepare(payload, rawPayload, receivedAtUtc);
        var isNew = await repository.RegisterCallResultAsync(e.Result, e.RawPayload, e.PayloadHash, cancellationToken);
        var result = e.Result;

        logger.LogDebug("Aviso {MessageId} {Status} {CallType} SIP {SipCode} → {Outcome}",
            result.MessageId, result.Status, result.CallType, result.SipCode, isNew ? "nuevo" : "duplicado");

        return isNew ? RegisterCallResultOutcome.Registered : RegisterCallResultOutcome.Duplicate;
    }

    // Valida y calcula la huella sin tocar la BD
    public static CallEventToSave Prepare(CallResultPayload payload, string rawPayload, DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rawPayload);

        var result = DomainCallResult.Create(
            payload.EffectiveMessageId,
            BlasterValueParser.ParseStatus(payload.Status),
            BlasterValueParser.ParseCallType(payload.CallType),
            receivedAtUtc,
            payload.SipCode,
            payload.SipDescription,
            payload.DurationMinutes,
            payload.DurationSeconds,
            payload.Operator,
            payload.IsMobile,
            payload.PhoneNumber,
            payload.OutboundRoute,
            statusText: payload.Status);

        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return new CallEventToSave(result, rawPayload, payloadHash);
    }
}
