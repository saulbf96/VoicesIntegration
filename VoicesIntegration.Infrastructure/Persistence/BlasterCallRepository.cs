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
    private const string RegisterEventProcedure = "blaster.usp_CallEvent_Register";
    private const string GetByMessageIdProcedure = "blaster.usp_Call_GetByMessageId";

    public async Task<bool> RegisterCallResultAsync(
        CallResult result,
        string rawPayload,
        byte[] payloadHash,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@MessageId", result.MessageId, DbType.AnsiString, size: CallResult.MaxMessageIdLength);
        parameters.Add("@StatusId", (byte)result.Status, DbType.Byte);
        parameters.Add("@CallTypeId", (byte)result.CallType, DbType.Byte);
        parameters.Add("@SipCode", result.SipCode, DbType.Int32);
        parameters.Add("@SipDescription", result.SipDescription, DbType.String, size: CallResult.MaxSipDescriptionLength);
        parameters.Add("@DurationMinutes", result.DurationMinutes, DbType.Int32);
        parameters.Add("@DurationSeconds", result.DurationSeconds, DbType.Int32);
        parameters.Add("@Operator", result.Operator, DbType.AnsiString, size: CallResult.MaxShortTextLength);
        parameters.Add("@IsMobile", result.IsMobile, DbType.Boolean);
        parameters.Add("@PhoneNumber", result.PhoneNumber, DbType.AnsiString, size: CallResult.MaxPhoneLength);
        parameters.Add("@OutboundRoute", result.OutboundRoute, DbType.AnsiString, size: CallResult.MaxShortTextLength);
        parameters.Add("@PayloadHash", payloadHash, DbType.Binary, size: 32);
        parameters.Add("@RawPayload", rawPayload, DbType.String, size: -1);
        parameters.Add("@ReceivedAtUtc", result.ReceivedAtUtc.UtcDateTime, DbType.DateTime2);
        parameters.Add("@IsDuplicate", dbType: DbType.Boolean, direction: ParameterDirection.Output);

        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            RegisterEventProcedure, parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return !parameters.Get<bool>("@IsDuplicate");
    }

    public async Task<CallDetails?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@MessageId", messageId, DbType.AnsiString, size: CallResult.MaxMessageIdLength);

        await using var connection = connectionFactory.Create();
        await using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            GetByMessageIdProcedure, parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        var call = await grid.ReadSingleOrDefaultAsync<CallDetails>();
        if (call is null)
            return null;

        var events = (await grid.ReadAsync<CallEventItem>()).ToList();
        return call with { Events = events };
    }
}
