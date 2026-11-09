using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;

namespace VoicesIntegration.Infrastructure.Inbox;

/// <summary>
/// /health: "Degraded" si hay avisos atorados (más viejos que MaxPendingAgeMinutes), avisos rechazados
/// por revisar o poco espacio en disco. "Unhealthy" si no se puede leer la carpeta.
/// </summary>
internal sealed class InboxHealthCheck(IWebhookInbox inbox, IOptions<InboxOptions> options, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var s = inbox.GetStatus();
            var o = options.Value;
            var data = new Dictionary<string, object> { ["pendientes"] = s.Pending, ["rechazados"] = s.Rejected };
            if (s.FreeDiskMb is { } free) data["disco_libre_mb"] = free;
            // Cuándo llegó el aviso pendiente más viejo (UTC): /recuperar lo usa para saber si el reporte de UN DÍA
            // está incompleto (solo si hay pendientes de ese día o antes; los de hoy no afectan a ayer).
            if (s.OldestPendingUtc is { } masViejo) data["pendiente_mas_viejo_utc"] = masViejo.UtcDateTime.ToString("o");

            if (s.FreeDiskMb is { } libre && libre < o.MinFreeDiskMb)
                return Task.FromResult(HealthCheckResult.Degraded($"Poco espacio en disco ({libre} MB libres).", data: data));

            if (s.OldestPendingUtc is { } oldest && time.GetUtcNow() - oldest > TimeSpan.FromMinutes(o.MaxPendingAgeMinutes))
                return Task.FromResult(HealthCheckResult.Degraded("Hay avisos atorados en la bandeja.", data: data));

            if (s.Rejected > 0)
                return Task.FromResult(HealthCheckResult.Degraded("Hay avisos rechazados por revisar.", data: data));

            return Task.FromResult(HealthCheckResult.Healthy(data: data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("No se puede leer la bandeja.", ex));
        }
    }
}
