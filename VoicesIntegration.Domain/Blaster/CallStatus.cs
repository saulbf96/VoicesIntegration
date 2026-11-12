namespace VoicesIntegration.Domain.Blaster;

/// <summary>
/// Estado de la llamada que reporta el webhook "Call result" de Blaster.
/// Los valores numéricos se fijan a mano porque se guardan en la BD:
/// nunca cambies uno existente, solo agrega nuevos.
/// </summary>
public enum CallStatus
{
    Sent = 1,       // SENT: la llamada se entregó
    ToRedial = 2,   // TO_REDIAL: se reintentará
    Failed = 3,     // FAILED: falló definitivamente
    Other = 4       // otro estado que manda Voices (ej. QUEUED_PROVIDER): se guarda el texto tal cual, NO se rechaza
}
