using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Abstractions.Monitoring;
using VoicesIntegration.Application.Abstractions.Notifications;

namespace VoicesIntegration.Api.Endpoints;

public static class DiagnosticoEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/diagnostico/correo-prueba", async (INotificador notificador, CancellationToken ct) =>
            {
                var error = await notificador.ProbarAsync(ct);
                return error is null
                    ? Results.Ok(new { enviado = true, mensaje = "Correo de prueba enviado. Revisa la bandeja (y spam)." })
                    : Results.Ok(new { enviado = false, mensaje = error });
            })
            .RequireInternalNetworkOrApiKey();

        // Mide red vs servidor de BD (tarda ~1 min)
        app.MapGet("/api/diagnostico/bd", async (IDiagnosticoBd diag, CancellationToken ct) => Results.Ok(await diag.MedirAsync(ct)))
            .RequireInternalNetworkOrApiKey();

        return app;
    }
}
