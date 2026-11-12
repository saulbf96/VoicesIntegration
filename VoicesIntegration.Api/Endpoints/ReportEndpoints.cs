using System.Globalization;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Abstractions.Monitoring;

namespace VoicesIntegration.Api.Endpoints;

// Huecos del dia: horas sin servicio que hay que recuperar
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reportes/huecos", async (string? fecha, IMonitoringRepository repo, TimeProvider time, IConfiguration config, CancellationToken ct) =>
            {
                DateOnly dia;
                if (string.IsNullOrWhiteSpace(fecha))
                    dia = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
                else if (!DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dia))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Fecha inválida", detail: "Usa el formato yyyy-MM-dd.");

                var r = await repo.GetGapReportAsync(dia, ct);
                return Results.Ok(new
                {
                    r.Fecha, r.Horas, r.VentanasSinServicio,
                    HorarioInicio = config["Alertas:HorarioInicio"] ?? "07:00",
                    HorarioFin = config["Alertas:HorarioFin"] ?? "22:00"
                });
            })
            .WithTags("Reportes")
            .WithName("GetGapReport")
            .RequireInternalNetworkOrApiKey();

        return app;
    }
}
