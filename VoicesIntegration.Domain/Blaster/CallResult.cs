using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Domain.Blaster;

/// <summary>
/// Resultado de una llamada reportado por el webhook "Call result" de Blaster.
/// Cada webhook es un evento: una llamada puede tener varios (reintentos, transferencia).
/// </summary>
public sealed class CallResult
{
    public const int MaxMessageIdLength = 100;
    public const int MaxShortTextLength = 50;
    public const int MaxSipDescriptionLength = 200;
    public const int MaxPhoneLength = 20;

    public string MessageId { get; }
    public CallStatus Status { get; }

    /// <summary>El estado tal como lo mandó Voices (SENT, FAILED, QUEUED_PROVIDER...). Es lo que se guarda en la BD.</summary>
    public string StatusText { get; }
    public CallType CallType { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public int? SipCode { get; }
    public string? SipDescription { get; }
    public int? DurationMinutes { get; }
    public int? DurationSeconds { get; }
    public string? Operator { get; }
    public bool? IsMobile { get; }

    

    
    // como PhoneNumber: si Voices cambia el formato NO queremos perder el evento.
    public string? PhoneNumber { get; }
    public string? OutboundRoute { get; }

    private CallResult(
        string messageId, CallStatus status, string statusText, CallType callType, DateTimeOffset receivedAtUtc,
        int? sipCode, string? sipDescription, int? durationMinutes, int? durationSeconds,
        string? @operator, bool? isMobile, string? phoneNumber, string? outboundRoute)
    {
        MessageId = messageId;
        Status = status;
        StatusText = statusText;
        CallType = callType;
        ReceivedAtUtc = receivedAtUtc;
        SipCode = sipCode;
        SipDescription = sipDescription;
        DurationMinutes = durationMinutes;
        DurationSeconds = durationSeconds;
        Operator = @operator;
        IsMobile = isMobile;
        PhoneNumber = phoneNumber;
        OutboundRoute = outboundRoute;
    }


    public static CallResult Create(
        string? messageId,
        CallStatus status,
        CallType callType,
        DateTimeOffset receivedAtUtc,
        int? sipCode = null,
        string? sipDescription = null,
        int? durationMinutes = null,
        int? durationSeconds = null,
        string? @operator = null,
        bool? isMobile = null,
        string? phoneNumber = null,
        string? outboundRoute = null,
        string? statusText = null)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new DomainException("El message_id es obligatorio.");

        messageId = messageId.Trim();
        if (messageId.Length > MaxMessageIdLength)
            throw new DomainException($"El message_id no puede exceder {MaxMessageIdLength} caracteres.");

        if (!Enum.IsDefined(status))
            throw new DomainException("Estado de llamada no válido.");

        if (!Enum.IsDefined(callType))
            throw new DomainException("Tipo de llamada no válido.");

        if (durationMinutes < 0 || durationSeconds < 0)
            throw new DomainException("La duración no puede ser negativa.");

        
        // el payload original completo se guarda aparte (RawPayload).
        // Texto del estado: el que mandó Voices; si no viene, el del enum (SENT / TO_REDIAL / FAILED).
        var texto = string.IsNullOrWhiteSpace(statusText)
            ? status switch { CallStatus.Sent => "SENT", CallStatus.ToRedial => "TO_REDIAL", CallStatus.Failed => "FAILED", _ => "OTHER" }
            : statusText.Trim().ToUpperInvariant();
        if (texto.Length > 20) texto = texto[..20];

        return new CallResult(
            messageId, status, texto, callType, receivedAtUtc.ToUniversalTime(),
            sipCode,
            Text.Truncate(sipDescription, MaxSipDescriptionLength),
            durationMinutes, durationSeconds,
            Text.Truncate(@operator, MaxShortTextLength),
            isMobile,
            Text.Truncate(phoneNumber, MaxPhoneLength),
            Text.Truncate(outboundRoute, MaxShortTextLength));


    }
}
