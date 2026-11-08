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
/// Caso de uso: guardar en BD un aviso del webhook de Blaster.
/// Es idempotente: si Voices reenvía el mismo JSON, no se duplica.
/// Lo llama el procesador de la bandeja (no el endpoint directamente).
/// </summary>
public sealed class RegisterCallResultHandler(
    IBlasterCallRepository repository,
    ILogger<RegisterCallResultHandler> logger)
{
    /// <param name="receivedAtUtc">Momento en que NOSOTROS recibimos el aviso (no cuando se procesa).</param>
    public async Task<RegisterCallResultOutcome> HandleAsync(
        CallResultPayload payload,
        string rawPayload,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        var e = Prepare(payload, rawPayload, receivedAtUtc);
        var isNew = await repository.RegisterCallResultAsync(e.Result, e.RawPayload, e.PayloadHash, cancellationToken);
        var result = e.Result;

        // Debug (no Information): con cientos de miles de avisos al día, una línea por aviso
        // llenaría el disco. El procesador escribe un resumen por lote.
        logger.LogDebug("Aviso {MessageId} {Status} {CallType} SIP {SipCode} → {Outcome}",
            result.MessageId, result.Status, result.CallType, result.SipCode, isNew ? "nuevo" : "duplicado");

        return isNew ? RegisterCallResultOutcome.Registered : RegisterCallResultOutcome.Duplicate;
    }

    /// <summary>
    /// Valida el aviso y calcula su huella, SIN tocar la BD (lo usa también el guardado por lotes).
    /// Lanza DomainException si el aviso es inválido.
    /// </summary>
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

        // Huella del JSON: el webhook no trae id de evento, así que el mismo
        // contenido exacto = el mismo evento reenviado.
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return new CallEventToSave(result, rawPayload, payloadHash);
    }
}
