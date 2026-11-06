using System.Data;
using Dapper;
using VoicesIntegration.Application.Abstractions.Monitoring;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>Disponibilidad de la API (minuto a minuto) y reporte de huecos con los SPs 16 y 17. Solo parámetros.</summary>
internal sealed class MonitoringRepository(ISqlConnectionFactory connectionFactory) : IMonitoringRepository
{
    public async Task RegisterHeartbeatAsync(string server, int pending, int rejected, CancellationToken cancellationToken)
    {
        var p = new DynamicParameters();
        p.Add("@Servidor", server, DbType.String, size: 100);
        p.Add("@Pendientes", pending, DbType.Int32);
        p.Add("@Rechazados", rejected, DbType.Int32);

        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition("dbo.USP_REGISTRAR_DISPONIBILIDAD_VOICES", p,
            commandType: CommandType.StoredProcedure, commandTimeout: 15, cancellationToken: cancellationToken));
    }

    public async Task<GapReport> GetGapReportAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var p = new DynamicParameters();
        p.Add("@Fecha", date.ToDateTime(TimeOnly.MinValue), DbType.Date);

        await using var connection = connectionFactory.Create();
        await using var grid = await connection.QueryMultipleAsync(new CommandDefinition("dbo.USP_REPORTE_HUECOS_VOICES", p,
            commandType: CommandType.StoredProcedure, commandTimeout: 60, cancellationToken: cancellationToken));

        var horas = (await grid.ReadAsync<HourCoverage>()).ToList();
        var ventanas = (await grid.ReadAsync<DowntimeWindow>()).ToList();
        return new GapReport(date, horas, ventanas);
    }
}
