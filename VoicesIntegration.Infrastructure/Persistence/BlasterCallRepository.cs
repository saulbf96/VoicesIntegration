using System.Data;
using Dapper;
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
