using Microsoft.Data.SqlClient;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>Crea conexiones a SQL Server. Cada operación abre y cierra la suya (pool de conexiones).</summary>
internal interface ISqlConnectionFactory
{
    SqlConnection Create();

    /// <summary>Conexión para carga masiva (SqlBulkCopy): paquete de red de 32 KB.</summary>
    SqlConnection CreateForBulk();
}

internal sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        // Resiliencia ante cortes cortos de red: si la conexión se cae, SqlClient reintenta solo.
        // Solo se ponen si la cadena no los trae (lo que venga en la cadena manda).
        var b = new SqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("ConnectRetryCount", StringComparison.OrdinalIgnoreCase)) b.ConnectRetryCount = 3;
        if (!connectionString.Contains("ConnectRetryInterval", StringComparison.OrdinalIgnoreCase)) b.ConnectRetryInterval = 5;
        if (!connectionString.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase)) b.ConnectTimeout = 15;
        _connectionString = b.ConnectionString;

        // Paquete de red grande SOLO para cargas masivas (recuperación): manda el doble de filas por segundo
        // (medido: 5.3k → 11.8k filas/s con el reporte real). Las demás operaciones siguen con el normal.
        var bulk = new SqlConnectionStringBuilder(_connectionString);
        if (!connectionString.Contains("Packet Size", StringComparison.OrdinalIgnoreCase)) bulk.PacketSize = 32767;
        _bulkConnectionString = bulk.ConnectionString;
    }

    private readonly string _bulkConnectionString;

    public SqlConnection Create() => ConArithAbort(new SqlConnection(_connectionString));

    public SqlConnection CreateForBulk() => ConArithAbort(new SqlConnection(_bulkConnectionString));

    /// <summary>
    /// .NET se conecta con ARITHABORT OFF y SSMS con ARITHABORT ON: SQL Server guarda un plan DISTINTO para cada uno.
    /// Medido en el 205 (09/11/2026), mismo SP y mismos 1,000 avisos: ARITHABORT OFF = 43.5 s, ON = 6–15 s.
    /// Se pone ON cada vez que la conexión se abre (también al reusarla del pool), igual que SSMS.
    /// </summary>
    private static SqlConnection ConArithAbort(SqlConnection c)
    {
        c.StateChange += (sender, e) =>
        {
            if (e.CurrentState != System.Data.ConnectionState.Open || sender is not SqlConnection cn) return;
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SET ARITHABORT ON;";
            cmd.CommandTimeout = 15;
            cmd.ExecuteNonQuery();
        };
        return c;
    }
}
