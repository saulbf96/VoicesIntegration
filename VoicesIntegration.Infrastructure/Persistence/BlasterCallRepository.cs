using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Domain.Blaster;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>
/// Repositorio con Dapper que SOLO ejecuta stored procedures
/// (scripts en VOICES\SCRIPTS). Todos los valores van como parámetros:
/// nunca se concatena texto en SQL → sin riesgo de SQL injection.
/// </summary>
internal sealed class BlasterCallRepository(ISqlConnectionFactory connectionFactory) : IBlasterCallRepository
{
    private const string RegisterEventProcedure = "dbo.USP_GUARDAR_LLAMADA_VOICES";
    private const string GetByMessageIdProcedure = "dbo.USP_CONSULTAR_LLAMADA_VOICES";

    // Si la BD no responde en 30 s, se corta y el aviso se reintenta desde la bandeja.
    private const int CommandTimeoutSeconds = 30;

    public async Task<bool> RegisterCallResultAsync(
        CallResult result,
        string rawPayload,
        byte[] payloadHash,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@Identifier", result.MessageId, DbType.AnsiString, size: CallResult.MaxMessageIdLength);
        parameters.Add("@TipoLlamada", result.CallType == CallType.Transfer ? "TRANSFER" : "INITIAL", DbType.AnsiString, size: 10);
        parameters.Add("@Status", result.StatusText, DbType.AnsiString, size: 20);   // tal cual lo mandó Voices
        parameters.Add("@SipCode", result.SipCode, DbType.Int32);
        parameters.Add("@SipDescription", result.SipDescription, DbType.String, size: CallResult.MaxSipDescriptionLength);
        parameters.Add("@DurationMinutes", result.DurationMinutes, DbType.Int32);
        parameters.Add("@DurationSeconds", result.DurationSeconds, DbType.Int32);
        // Teléfono, operador, ruta y demás columnas el SP los lee del JSON del aviso (@RawPayload, que NO se guarda).
        parameters.Add("@PayloadHash", payloadHash, DbType.Binary, size: 32);
        parameters.Add("@RawPayload", rawPayload, DbType.String, size: -1);
        parameters.Add("@ReceivedAtUtc", result.ReceivedAtUtc.UtcDateTime, DbType.DateTime2);
        parameters.Add("@IsDuplicate", dbType: DbType.Boolean, direction: ParameterDirection.Output);

        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            RegisterEventProcedure, parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken));

        return !parameters.Get<bool>("@IsDuplicate");
    }



    private const string RegisterBatchProcedure = "dbo.USP_GUARDAR_LLAMADAS_LOTE_VOICES";
    private const string BatchTableType = "dbo.TT_AVISOS_LLAMADA_VOICES";
    private const int BatchTimeoutSeconds = 120;

    // Errores por DATO (o porque el SP/tipo aún no existe en la BD): el lote se guarda uno por uno.
    // Cualquier otro error (timeout, conexión, bloqueo...) es falla técnica: los avisos esperan en la bandeja.
    private static readonly HashSet<int> DataErrors =
    [
        2812,  // no existe el procedimiento (falta correr el script 28)
        2715,  // no existe el tipo de tabla
        8152, 2628,             // texto demasiado largo
        245, 241, 8114, 8115,   // conversión de tipo
        515, 547, 2601, 2627,   // NULL, restricción, llave repetida
        13609,                  // JSON inválido
    ];

    public async Task<IReadOnlyList<bool>> RegisterCallResultsBatchAsync(IReadOnlyList<CallEventToSave> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return [];

        var t = new DataTable();
        t.Columns.Add("FILA", typeof(int));
        t.Columns.Add("IDENTIFIER", typeof(string));
        t.Columns.Add("TIPO_LLAMADA", typeof(string));
        t.Columns.Add("STATUS", typeof(string));
        t.Columns.Add("SIP_CODE", typeof(int));
        t.Columns.Add("SIP_DESCRIPTION", typeof(string));
        t.Columns.Add("DURATION_MINUTES", typeof(int));
        t.Columns.Add("DURATION_SECONDS", typeof(int));
        t.Columns.Add("PAYLOAD_HASH", typeof(byte[]));
        t.Columns.Add("RAW_PAYLOAD", typeof(string));
        t.Columns.Add("RECEIVED_AT_UTC", typeof(DateTime));
        for (var i = 0; i < events.Count; i++)
        {
            var (r, raw, hash) = (events[i].Result, events[i].RawPayload, events[i].PayloadHash);
            t.Rows.Add(i, r.MessageId, r.CallType == CallType.Transfer ? "TRANSFER" : "INITIAL", r.StatusText,
                (object?)r.SipCode ?? DBNull.Value, (object?)r.SipDescription ?? DBNull.Value,
                (object?)r.DurationMinutes ?? DBNull.Value, (object?)r.DurationSeconds ?? DBNull.Value,
                hash, raw, r.ReceivedAtUtc.UtcDateTime);
        }

        try
        {
            await using var connection = connectionFactory.Create();
            var rows = await connection.QueryAsync<(int Fila, bool EsDuplicado)>(new CommandDefinition(
                RegisterBatchProcedure, new { Avisos = t.AsTableValuedParameter(BatchTableType) },
                commandType: CommandType.StoredProcedure,
                commandTimeout: BatchTimeoutSeconds,
                cancellationToken: cancellationToken));

            var isNew = new bool[events.Count];
            foreach (var (fila, esDuplicado) in rows) isNew[fila] = !esDuplicado;
            return isNew;
        }
        catch (SqlException ex) when (DataErrors.Contains(ex.Number))
        {
            throw new BatchRejectedException($"La BD rechazó el lote ({ex.Number}): {ex.Message}", ex);
        }
    }

    public async Task<CallDetails?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@Identifier", messageId, DbType.AnsiString, size: CallResult.MaxMessageIdLength);

        await using var connection = connectionFactory.Create();
        await using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            GetByMessageIdProcedure, parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken));

        var call = await grid.ReadSingleOrDefaultAsync<CallDetails>();
        if (call is null)
            return null;


        var events = (await grid.ReadAsync<CallEventItem>()).ToList();
        return call with { Events = events };
    }
}
