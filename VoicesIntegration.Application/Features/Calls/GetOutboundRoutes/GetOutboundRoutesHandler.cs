using VoicesIntegration.Application.Abstractions.Blaster;

namespace VoicesIntegration.Application.Features.Calls.GetOutboundRoutes;

public sealed class GetOutboundRoutesHandler(IBlasterGateway gateway)
{
    public Task<IReadOnlyList<OutboundRoute>> HandleAsync(CancellationToken cancellationToken)
        => gateway.GetOutboundRoutesAsync(cancellationToken);
}
