using Microsoft.Data.SqlClient;

namespace VoicesIntegration.Infrastructure.Persistence;

internal interface ISqlConnectionFactory
{
    SqlConnection Create();

    SqlConnection CreateForBulk();
}

internal sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("ConnectRetryCount", StringComparison.OrdinalIgnoreCase)) b.ConnectRetryCount = 3;
        if (!connectionString.Contains("ConnectRetryInterval", StringComparison.OrdinalIgnoreCase)) b.ConnectRetryInterval = 5;
        if (!connectionString.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase)) b.ConnectTimeout = 15;
        _connectionString = b.ConnectionString;

        var bulk = new SqlConnectionStringBuilder(_connectionString);
        if (!connectionString.Contains("Packet Size", StringComparison.OrdinalIgnoreCase)) bulk.PacketSize = 32767;
        _bulkConnectionString = bulk.ConnectionString;
    }

    private readonly string _bulkConnectionString;

    public SqlConnection Create() => ConArithAbort(new SqlConnection(_connectionString));

    public SqlConnection CreateForBulk() => ConArithAbort(new SqlConnection(_bulkConnectionString));

    // ARITHABORT ON igual que SSMS para usar el mismo plan
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
