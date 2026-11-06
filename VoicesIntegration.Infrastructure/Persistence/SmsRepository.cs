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


}
