using System.Text.Json.Serialization;

namespace VoicesIntegration.Application.Features.Webhooks.CallResult;

/// <summary>
/// JSON que manda Blaster en el webhook "Call result".
/// Los nombres van fijos con JsonPropertyName porque son el contrato de Voices (snake_case).
/// OJO: la documentación dice "message_id", pero el webhook REAL (verificado 2026-11-03)
/// manda el id como "identifier". Se aceptan ambos. El JSON completo (campaña, intentos,
/// datos del cliente, etc.) se guarda aparte en RawPayload.
/// </summary>
public sealed record CallResultPayload
{
    [JsonPropertyName("message_id")] public string? MessageId { get; init; }
    [JsonPropertyName("identifier")] public string? Identifier { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("operator")] public string? Operator { get; init; }
    [JsonPropertyName("is_mobile")] public bool? IsMobile { get; init; }
    [JsonPropertyName("outbound_route")] public string? OutboundRoute { get; init; }
    [JsonPropertyName("phone_number")] public string? PhoneNumber { get; init; }
    [JsonPropertyName("sip_code")] public int? SipCode { get; init; }
    [JsonPropertyName("sip_description")] public string? SipDescription { get; init; }
    [JsonPropertyName("call_type")] public string? CallType { get; init; }
    [JsonPropertyName("duration_minutes")] public int? DurationMinutes { get; init; }
    [JsonPropertyName("duration_seconds")] public int? DurationSeconds { get; init; }

    /// <summary>Id de la llamada: "message_id" (doc) o "identifier" (webhook real).</summary>
    [JsonIgnore]
    public string? EffectiveMessageId => string.IsNullOrWhiteSpace(MessageId) ? Identifier : MessageId;
}
