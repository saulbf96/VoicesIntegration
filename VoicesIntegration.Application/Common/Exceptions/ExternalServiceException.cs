namespace VoicesIntegration.Application.Common.Exceptions;

/// <summary>
/// Falla de un servicio externo (por ejemplo Blaster caído o respondiendo error).
/// La API la traduce a HTTP 502 sin exponer detalles internos.
/// </summary>
public sealed class ExternalServiceException(string service, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Service { get; } = service;
}
