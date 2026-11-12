using Microsoft.AspNetCore.Http.HttpResults;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Abstractions.Blaster;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Application.Features.Calls.GetOutboundRoutes;

namespace VoicesIntegration.Api.Endpoints;

// Consultas internas (requieren X-Api-Key)
public static class BlasterEndpoints
{
    public static IEndpointRouteBuilder MapBlasterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/blaster")
            .WithTags("Blaster")
            .RequireInternalApiKey();

        // Prueba que la API key de Voices funcione
        group.MapGet("/outbound-routes", GetOutboundRoutesAsync).WithName("GetOutboundRoutes");

        group.MapGet("/calls/{messageId}", GetCallAsync).WithName("GetCall");

        // Pendientes y rechazados de la bandeja
        group.MapGet("/inbox", (IWebhookInbox inbox) => TypedResults.Ok(inbox.GetStatus())).WithName("GetInboxStatus");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<OutboundRoute>>> GetOutboundRoutesAsync(
        GetOutboundRoutesHandler handler, CancellationToken cancellationToken)
        => TypedResults.Ok(await handler.HandleAsync(cancellationToken));

    private static async Task<Results<Ok<CallDetails>, NotFound>> GetCallAsync(
        string messageId, GetCallHandler handler, CancellationToken cancellationToken)
        => await handler.HandleAsync(messageId, cancellationToken) is { } call
            ? TypedResults.Ok(call)
            : TypedResults.NotFound();
}
