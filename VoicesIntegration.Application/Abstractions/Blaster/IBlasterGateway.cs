namespace VoicesIntegration.Application.Abstractions.Blaster;

public interface IBlasterGateway
{
    Task<IReadOnlyList<OutboundRoute>> GetOutboundRoutesAsync(CancellationToken cancellationToken);
}

public sealed record OutboundRoute(string Id, string Name, string Type);
