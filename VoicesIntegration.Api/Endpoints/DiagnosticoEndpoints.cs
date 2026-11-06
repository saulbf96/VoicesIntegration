using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Abstractions.Notifications;

namespace VoicesIntegration.Api.Endpoints;

/// <summary>
/// Diagnóstico. POST /api/diagnostico/correo-prueba manda un correo de prueba con la configuración "Correo"
/// y responde si salió o el motivo exacto del error (sin contraseñas). Solo red interna (o X-Api-Key).
/// </summary>
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

        return app;
    }
}
