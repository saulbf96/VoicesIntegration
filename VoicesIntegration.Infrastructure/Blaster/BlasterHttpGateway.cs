using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Blaster;
using VoicesIntegration.Application.Common.Exceptions;

namespace VoicesIntegration.Infrastructure.Blaster;

internal sealed class BlasterHttpGateway(HttpClient httpClient, ILogger<BlasterHttpGateway> logger) : IBlasterGateway
{
    private const string ServiceName = "Blaster";

    public async Task<IReadOnlyList<OutboundRoute>> GetOutboundRoutesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("v1/blaster/outbound-routes", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Blaster respondió {StatusCode} al listar rutas.", (int)response.StatusCode);
                throw new ExternalServiceException(ServiceName, $"Blaster respondió {(int)response.StatusCode} al listar rutas.");
            }

            var routes = await response.Content.ReadFromJsonAsync<List<OutboundRouteResponse>>(cancellationToken) ?? [];
            return routes.Select(r => new OutboundRoute(r.Id ?? string.Empty, r.Name ?? string.Empty, r.Type ?? string.Empty)).ToList();
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException(ServiceName, "No se pudo conectar con Blaster.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException(ServiceName, "Blaster no respondió a tiempo.", ex);
        }
        catch (JsonException ex)
        {
            throw new ExternalServiceException(ServiceName, "Blaster devolvió una respuesta con formato inesperado.", ex);
        }
    }

    private sealed record OutboundRouteResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string? Type);
}
