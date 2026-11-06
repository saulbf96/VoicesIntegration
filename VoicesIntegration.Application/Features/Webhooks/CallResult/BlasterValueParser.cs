using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Webhooks.CallResult;

/// <summary>Traduce los textos de Blaster ("SENT", "transfer"...) a nuestros enums.</summary>
public static class BlasterValueParser
{
    public static CallStatus ParseStatus(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SENT" => CallStatus.Sent,
        "TO_REDIAL" => CallStatus.ToRedial,
        "FAILED" => CallStatus.Failed,
        // Sin estado sí se rechaza; un estado nuevo de Voices (ej. QUEUED_PROVIDER) se guarda tal cual: no se pierde la llamada.
        null or "" => throw new DomainException("El aviso no trae el estado de la llamada (status)."),
        _ => CallStatus.Other
    };

    /// <summary>call_type es opcional en la doc: si no viene se asume la llamada inicial.</summary>
    public static CallType ParseCallType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "initial" => CallType.Initial,
        "transfer" => CallType.Transfer,
        _ => throw new DomainException($"Tipo de llamada desconocido: '{value}'.")
    };
}
