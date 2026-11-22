namespace VoicesIntegration.Domain.Blaster;

// Los valores se guardan en BD, no cambiarlos
public enum CallStatus
{
    Sent = 1,
    ToRedial = 2,
    Failed = 3,
    Other = 4
}
