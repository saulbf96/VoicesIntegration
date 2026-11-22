namespace VoicesIntegration.Application.Common.Exceptions;

public sealed class ExternalServiceException(string service, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Service { get; } = service;
}
