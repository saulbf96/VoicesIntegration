using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Webhooks.CallResult;

public static class BlasterValueParser
{
    public static CallStatus ParseStatus(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SENT" => CallStatus.Sent,
        "TO_REDIAL" => CallStatus.ToRedial,
        "FAILED" => CallStatus.Failed,
        null or "" => throw new DomainException("El aviso no trae el estado de la llamada (status)."),
        _ => CallStatus.Other
    };

    // Si no viene call_type se toma como inicial
    public static CallType ParseCallType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "initial" => CallType.Initial,
        "transfer" => CallType.Transfer,
        _ => throw new DomainException($"Tipo de llamada desconocido: '{value}'.")
    };
}
