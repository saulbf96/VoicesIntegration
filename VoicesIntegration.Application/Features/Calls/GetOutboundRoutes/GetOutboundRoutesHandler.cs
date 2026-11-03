using VoicesIntegration.Application.Abstractions.Blaster;

namespace VoicesIntegration.Application.Features.Calls.GetOutboundRoutes;

/// <summary>Caso de uso: listar las rutas de salida de la cuenta de Blaster.</summary>
public sealed class GetOutboundRoutesHandler(IBlasterGateway gateway)
{
    public Task<IReadOnlyList<OutboundRoute>> HandleAsync(CancellationToken cancellationToken)
        => gateway.GetOutboundRoutesAsync(cancellationToken);
}
