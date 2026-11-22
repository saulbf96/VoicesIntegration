using System.Text.Json.Serialization;

namespace VoicesIntegration.Application.Features.Webhooks.CallResult;

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

    [JsonIgnore]
    public string? EffectiveMessageId => string.IsNullOrWhiteSpace(MessageId) ? Identifier : MessageId;
}
