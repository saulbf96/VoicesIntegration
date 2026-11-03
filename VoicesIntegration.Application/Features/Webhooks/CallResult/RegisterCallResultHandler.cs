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

/// <summary>
/// Caso de uso: recibir el webhook de Blaster y guardarlo.
/// Es idempotente: si Voices reenvía el mismo JSON, no se duplica.
/// </summary>
public sealed class RegisterCallResultHandler(
    IBlasterCallRepository repository,
    TimeProvider timeProvider,
    ILogger<RegisterCallResultHandler> logger)
{
    public async Task<RegisterCallResultOutcome> HandleAsync(
        CallResultPayload payload,
        string rawPayload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rawPayload);

        var result = DomainCallResult.Create(
            payload.EffectiveMessageId,
            BlasterValueParser.ParseStatus(payload.Status),
            BlasterValueParser.ParseCallType(payload.CallType),
            timeProvider.GetUtcNow(),
            payload.SipCode,
            payload.SipDescription,
            payload.DurationMinutes,
            payload.DurationSeconds,
            payload.Operator,
            payload.IsMobile,
            payload.PhoneNumber,
            payload.OutboundRoute);

        // Huella del JSON: el webhook no trae id de evento, así que el mismo
        // contenido exacto = el mismo evento reenviado.
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));

        // CancellationToken.None A PROPÓSITO: si Voices corta la conexión (timeout de su lado),
        // el aviso YA lo recibimos y no hay API para recuperarlo después → hay que terminar de
        // guardarlo. El SP tiene su propio timeout de comando, así que no se queda colgado.
        var isNew = await repository.RegisterCallResultAsync(result, rawPayload, payloadHash, CancellationToken.None);

        if (isNew)
        {
            logger.LogInformation(
                "Webhook Blaster registrado: {MessageId} {Status} {CallType} SIP {SipCode}",
                result.MessageId, result.Status, result.CallType, result.SipCode);
            return RegisterCallResultOutcome.Registered;
        }

        logger.LogInformation("Webhook Blaster duplicado ignorado: {MessageId}", result.MessageId);
        return RegisterCallResultOutcome.Duplicate;
    }
}
