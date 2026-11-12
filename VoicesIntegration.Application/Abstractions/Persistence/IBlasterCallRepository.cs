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

    /// <summary>
    /// Guarda MUCHOS eventos en una sola llamada a la BD (mucho más rápido que uno por uno).
    /// Devuelve, en el mismo orden, true = nuevo / false = ya existía.
    /// Lanza <see cref="BatchRejectedException"/> si la BD rechazó el lote por un DATO (o falta el SP):
    /// entonces hay que guardarlos uno por uno. Cualquier otra excepción = falla técnica (reintentar después).
    /// </summary>
    Task<IReadOnlyList<bool>> RegisterCallResultsBatchAsync(IReadOnlyList<CallEventToSave> events, CancellationToken cancellationToken);

    Task<CallDetails?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken);
}

/// <summary>Un evento listo para guardarse (ya validado) con su JSON y su huella.</summary>
public sealed record CallEventToSave(CallResult Result, string RawPayload, byte[] PayloadHash);

/// <summary>La BD rechazó el lote completo por un dato (no por estar caída): guardar uno por uno.</summary>
public sealed class BatchRejectedException(string message, Exception inner) : Exception(message, inner);
