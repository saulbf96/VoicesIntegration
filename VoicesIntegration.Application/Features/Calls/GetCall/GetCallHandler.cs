using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Calls.GetCall;

/// <summary>Caso de uso: consultar una llamada guardada por su message_id.</summary>
public sealed class GetCallHandler(IBlasterCallRepository repository)
{
    public Task<CallDetails?> HandleAsync(string? messageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > CallResult.MaxMessageIdLength)
            throw new DomainException("message_id inválido.");

        return repository.GetByMessageIdAsync(messageId.Trim(), cancellationToken);
    }
}
