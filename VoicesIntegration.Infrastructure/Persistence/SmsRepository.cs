using System.Data;
using Dapper;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Abstractions.Sms;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>Guarda los avisos SMS con el SP dbo.USP_GUARDAR_SMS_VOICES (script 14). Solo parámetros.</summary>
internal sealed class SmsRepository(ISqlConnectionFactory connectionFactory) : ISmsRepository
{
    private const string Procedure = "dbo.USP_GUARDAR_SMS_VOICES";
    private const int CommandTimeoutSeconds = 30;

    public async Task<bool> RegisterAsync(string rawPayload, byte[] payloadHash, DateTimeOffset receivedAtUtc,
        SmsMessageDetail? detail, CancellationToken cancellationToken)
    {
        var p = new DynamicParameters();
        p.Add("@RawPayload", rawPayload, DbType.String, size: -1);
        p.Add("@PayloadHash", payloadHash, DbType.Binary, size: 32);
        p.Add("@ReceivedAtUtc", receivedAtUtc.UtcDateTime, DbType.DateTime2);
        p.Add("@To", Truncate(detail?.To, 20), DbType.AnsiString, size: 20);
        p.Add("@Message", detail?.Message, DbType.String, size: -1);
        p.Add("@Operator", Truncate(detail?.Operator, 20), DbType.AnsiString, size: 20);
        p.Add("@CreatedAtUtc", detail?.CreatedAt?.UtcDateTime, DbType.DateTime2);
        p.Add("@SentAtUtc", detail?.SentAt?.UtcDateTime, DbType.DateTime2);
        p.Add("@FailedAtUtc", detail?.FailedAt?.UtcDateTime, DbType.DateTime2);
        p.Add("@DetalleObtenido", detail is not null, DbType.Boolean);
        p.Add("@IsDuplicate", dbType: DbType.Boolean, direction: ParameterDirection.Output);


        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(Procedure, p,
            commandType: CommandType.StoredProcedure, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken));

        return !p.Get<bool>("@IsDuplicate");
    }

    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];

    // ---- Respuestas del cliente (scripts 40/41) ----
    private const string ReplyProcedure = "dbo.USP_GUARDAR_RESPUESTA_SMS_VOICES";

    public async Task<bool> RegisterReplyAsync(SmsReplyToSave reply, CancellationToken cancellationToken)
    {
        var p = new DynamicParameters();
        p.Add("@CampaignId", reply.CampaignId, DbType.AnsiString, size: 100);
        p.Add("@MessageId", reply.MessageId, DbType.AnsiString, size: 100);
        p.Add("@Number", reply.Number, DbType.AnsiString, size: 20);
        p.Add("@Text", reply.Text, DbType.String, size: 1000);
        p.Add("@CreatedAtUtc", reply.CreatedAtUtc?.UtcDateTime, DbType.DateTime2);
        p.Add("@PayloadHash", reply.PayloadHash, DbType.Binary, size: 32);
        p.Add("@ReceivedAtUtc", reply.ReceivedAtUtc.UtcDateTime, DbType.DateTime2);
        p.Add("@IsDuplicate", dbType: DbType.Boolean, direction: ParameterDirection.Output);

        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(ReplyProcedure, p,
            commandType: CommandType.StoredProcedure, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken));

        return !p.Get<bool>("@IsDuplicate");
    }

    // ---- Por lotes (script 31): 1 transacción por lote en vez de 1 por SMS ----
    private const string BatchProcedure = "dbo.USP_GUARDAR_SMS_LOTE_VOICES";
    private const string BatchTableType = "dbo.TT_AVISOS_SMS_VOICES";
    private const int BatchTimeoutSeconds = 120;

    // Errores por DATO (o porque el SP/tipo aún no existe): el lote se guarda uno por uno con el SP 9.
    private static readonly HashSet<int> DataErrors =
    [
        2812, 2715,             // falta correr el script 31
        8152, 2628,             // texto demasiado largo
        245, 241, 8114, 8115,   // conversión de tipo
        515, 547, 2601, 2627,   // NULL, restricción, llave repetida
    ];

    public async Task<IReadOnlyList<bool>> RegisterBatchAsync(IReadOnlyList<SmsToSave> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return [];
        var t = BuildBatchTable(items);
        for (var intento = 1; ; intento++)
        {
            try
            {
                await using var connection = connectionFactory.Create();
                var rows = await connection.QueryAsync<(int Fila, bool EsDuplicado)>(new CommandDefinition(
                    BatchProcedure, new { Avisos = t.AsTableValuedParameter(BatchTableType) },
                    commandType: CommandType.StoredProcedure, commandTimeout: BatchTimeoutSeconds, cancellationToken: cancellationToken));
                var isNew = new bool[items.Count];
                foreach (var (fila, esDuplicado) in rows) isNew[fila] = !esDuplicado;
                return isNew;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1205 && intento < 4)
            {
                // Bloqueo mutuo con otra escritura: SQL deshizo TODO el lote; se reintenta (no se duplica nada).
                await Task.Delay(TimeSpan.FromMilliseconds(300 * intento), cancellationToken);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (DataErrors.Contains(ex.Number))
            {
                throw new BatchRejectedException($"La BD rechazó el lote de SMS ({ex.Number}): {ex.Message}", ex);
            }
        }
    }

    /// <summary>Lote para el SP 31 (columnas en el MISMO orden que dbo.TT_AVISOS_SMS_VOICES).</summary>
    internal static DataTable BuildBatchTable(IReadOnlyList<SmsToSave> items)
    {
        var t = new DataTable();
        t.Columns.Add("FILA", typeof(int));
        t.Columns.Add("PAYLOAD_HASH", typeof(byte[]));
        t.Columns.Add("RECEIVED_AT_UTC", typeof(DateTime));
        t.Columns.Add("J_MESSAGE_ID", typeof(string));
        t.Columns.Add("J_BATCH_ID", typeof(string));
        t.Columns.Add("J_STATUS", typeof(string));
        t.Columns.Add("J_DATETIME", typeof(string));
        t.Columns.Add("J_ORIGIN_IDENTIFIER", typeof(string));
        t.Columns.Add("D_TO", typeof(string));
        t.Columns.Add("D_MESSAGE", typeof(string));
        t.Columns.Add("D_OPERATOR", typeof(string));
        t.Columns.Add("D_CREATED_AT_UTC", typeof(DateTime));
        t.Columns.Add("D_SENT_AT_UTC", typeof(DateTime));
        t.Columns.Add("D_FAILED_AT_UTC", typeof(DateTime));
        t.Columns.Add("D_OBTENIDO", typeof(bool));

        for (var i = 0; i < items.Count; i++)
        {
            var x = items[i];
            using var doc = System.Text.Json.JsonDocument.Parse(x.RawPayload);
            var root = doc.RootElement;
            var d = x.Detail;
            t.Rows.Add(i, x.PayloadHash, x.ReceivedAtUtc.UtcDateTime,
                (object?)CamposJsonSql.Valor(root, "message_id") ?? DBNull.Value,
                (object?)CamposJsonSql.Valor(root, "batch_id") ?? DBNull.Value,
                (object?)CamposJsonSql.Valor(root, "status") ?? DBNull.Value,
                (object?)CamposJsonSql.Valor(root, "datetime") ?? DBNull.Value,
                (object?)CamposJsonSql.Valor(root, "origin_identifier") ?? DBNull.Value,
                (object?)Truncate(d?.To, 20) ?? DBNull.Value,
                (object?)d?.Message ?? DBNull.Value,
                (object?)Truncate(d?.Operator, 20) ?? DBNull.Value,
                (object?)d?.CreatedAt?.UtcDateTime ?? DBNull.Value,
                (object?)d?.SentAt?.UtcDateTime ?? DBNull.Value,
                (object?)d?.FailedAt?.UtcDateTime ?? DBNull.Value,
                d is not null);
        }
        return t;
    }
}
