using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Domain.Blaster;

namespace VoicesIntegration.Application.Abstractions.Persistence;

public interface IBlasterCallRepository
{
    Task<bool> RegisterCallResultAsync(
        CallResult result,
        string rawPayload,
        byte[] payloadHash,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<bool>> RegisterCallResultsBatchAsync(IReadOnlyList<CallEventToSave> events, CancellationToken cancellationToken);

    Task<CallDetails?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken);
}

public sealed record CallEventToSave(CallResult Result, string RawPayload, byte[] PayloadHash);

// La BD rechazo el lote por un dato: se guarda uno por uno
public sealed class BatchRejectedException(string message, Exception inner) : Exception(message, inner);
