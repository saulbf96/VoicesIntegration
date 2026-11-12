namespace VoicesIntegration.Application.Abstractions.Blaster;

/// <summary>
/// Contrato para consultar la API de Blaster (solo lectura: NO lanzamos llamadas).
/// Application solo conoce esta interfaz; el "cómo" (HttpClient, URL, llave)
/// vive en Infrastructure.
/// </summary>
public interface IBlasterGateway
{
    /// <summary>Rutas de salida de la cuenta. Sirve también para comprobar que la API key funciona.</summary>
    Task<IReadOnlyList<OutboundRoute>> GetOutboundRoutesAsync(CancellationToken cancellationToken);
}

public sealed record OutboundRoute(string Id, string Name, string Type);
