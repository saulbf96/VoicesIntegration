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
        _ => throw new DomainException($"Estado de llamada desconocido: '{value}'.")
    };

    /// <summary>call_type es opcional en la doc: si no viene se asume la llamada inicial.</summary>
    public static CallType ParseCallType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "initial" => CallType.Initial,
        "transfer" => CallType.Transfer,
        _ => throw new DomainException($"Tipo de llamada desconocido: '{value}'.")
    };
}
