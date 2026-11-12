namespace VoicesIntegration.Application.Features.Calls.GetCall;

public sealed record CallDetails
{
    public string MessageId { get; init; } = string.Empty;
    public string? PhoneNumber { get; init; }
    public string? OutboundRoute { get; init; }
    public string? LastStatus { get; init; }
    public int? LastSipCode { get; init; }
    public string? LastSipDescription { get; init; }
    public int? LastDurationMinutes { get; init; }
    public int? LastDurationSeconds { get; init; }
    public bool WasTransferred { get; init; }
    public int EventCount { get; init; }
    public string? Operator { get; init; }
    public bool? IsMobile { get; init; }
    public DateTime? LastEventAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public IReadOnlyList<CallEventItem> Events { get; init; } = [];
}

public sealed record CallEventItem
{
    public long CallEventId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string CallType { get; init; } = string.Empty;
    public int? SipCode { get; init; }
    public string? SipDescription { get; init; }
    public int? DurationMinutes { get; init; }
    public int? DurationSeconds { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
}
