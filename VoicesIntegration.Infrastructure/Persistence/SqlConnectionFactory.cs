using Microsoft.Data.SqlClient;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>Crea conexiones a SQL Server. Cada operación abre y cierra la suya (pool de conexiones).</summary>
internal interface ISqlConnectionFactory
{
    SqlConnection Create();
}

internal sealed class SqlConnectionFactory(string connectionString) : ISqlConnectionFactory
{
    public SqlConnection Create() => new(connectionString);
}
