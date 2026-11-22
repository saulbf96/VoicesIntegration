using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VoicesIntegration.Infrastructure.Persistence;

internal sealed class SqlServerHealthCheck(ISqlConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = connectionFactory.Create();
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Sin conexión a la base de datos.", ex);
        }
    }
}
