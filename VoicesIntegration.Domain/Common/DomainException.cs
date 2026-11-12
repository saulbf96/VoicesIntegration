namespace VoicesIntegration.Domain.Common;

/// <summary>
/// Error de reglas de negocio (dato inválido), distinto de un fallo técnico.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
