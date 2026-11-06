using Microsoft.AspNetCore.Http.HttpResults;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Abstractions.Blaster;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Application.Features.Calls.GetOutboundRoutes;

namespace VoicesIntegration.Api.Endpoints;

/// <summary>Endpoints de consulta para tu equipo. Todos exigen el header X-Api-Key.</summary>
public static class BlasterEndpoints
{
    public static IEndpointRouteBuilder MapBlasterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/blaster")
            .WithTags("Blaster")
            .RequireInternalApiKey();

        // Consulta en vivo a Blaster: sirve para comprobar que la API key de Voices funciona.
        group.MapGet("/outbound-routes", GetOutboundRoutesAsync).WithName("GetOutboundRoutes");

        // Consulta en NUESTRA BD lo que llegó por webhook.
        group.MapGet("/calls/{messageId}", GetCallAsync).WithName("GetCall");

        // Estado de la bandeja: cuántos avisos esperan pasar a la BD y cuántos se rechazaron.
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
