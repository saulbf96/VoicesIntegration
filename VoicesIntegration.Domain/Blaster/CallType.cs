namespace VoicesIntegration.Domain.Blaster;

/// <summary>
/// Tipo de llamada que reporta el webhook: la inicial o la transferencia.
/// </summary>
public enum CallType
{
    Initial = 1,    // initial: llamada original al cliente
    Transfer = 2    // transfer: llamada transferida (transfer_to)
}
