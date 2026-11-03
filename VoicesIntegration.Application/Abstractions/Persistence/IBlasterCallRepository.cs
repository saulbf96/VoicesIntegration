using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Domain.Blaster;

namespace VoicesIntegration.Application.Abstractions.Persistence;

/// <summary>
/// Contrato de persistencia de llamadas. La implementación (Dapper + stored
/// procedures) está en Infrastructure; aquí no se sabe nada de SQL.
/// </summary>
public interface IBlasterCallRepository
{
    /// <summary>Guarda un evento del webhook. Devuelve false si ya existía (reenvío de Voices).</summary>
    Task<bool> RegisterCallResultAsync(
        CallResult result,
        string rawPayload,
        byte[] payloadHash,
        CancellationToken cancellationToken);

    Task<CallDetails?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken);
}
