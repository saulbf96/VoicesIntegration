using System.Data;
using Dapper;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using VoicesIntegration.Application.Abstractions.Recovery;

namespace VoicesIntegration.Infrastructure.Persistence;

internal sealed class RecoveryRepository(ISqlConnectionFactory connectionFactory, ILogger<RecoveryRepository> logger) : IRecoveryRepository
{
    private const string Estado = "dbo.USP_RECUPERACION_ESTADO_VOICES";
    private const string Detalle = "dbo.TBL_RECUPERACION_DETALLE_VOICES";
    private const string Carga = "dbo.TBL_RECUPERACION_CARGA_VOICES";
    private const string Final = "dbo.TBL_LLAMADAS_RECUPERADAS_VOICES";

    public Task CreateAsync(Guid jobId, string fileName, long sizeBytes, string? user, string canal, CancellationToken ct) =>
        ExecAsync(Estado, new { JobId = jobId, Estado = RecoveryStates.Subido, Archivo = Cut(fileName, 260), TamanoBytes = sizeBytes, Usuario = Cut(user, 100), Canal = canal }, 30, ct);

    public async Task<int> BulkLoadSmsAsync(Guid jobId, IAsyncEnumerable<SmsRecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct)
    {
        await using var c = connectionFactory.CreateForBulk();
        await c.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = "dbo.TBL_RECUPERACION_SMS_VOICES", BulkCopyTimeout = 0, BatchSize = 100_000, EnableStreaming = true
        };
        foreach (var col in SmsRecoveryDataReader.Columns) bulk.ColumnMappings.Add(col, col);
        await using var reader = new SmsRecoveryDataReader(jobId, rows, onProgress, ct);
        await bulk.WriteToServerAsync(reader, ct);
        await onProgress(reader.Count);
        return reader.Count;
    }

    public Task CompareSmsAsync(Guid jobId, CancellationToken ct) =>
        ExecAsync("dbo.USP_RECUPERACION_SMS_COMPARAR_VOICES", new { JobId = jobId }, 0, ct);

    public Task InsertSmsAsync(Guid jobId, CancellationToken ct) =>
        ExecAsync("dbo.USP_RECUPERACION_SMS_INSERTAR_VOICES", new { JobId = jobId, Cancelar = false }, 0, ct);

    public Task UpdateAsync(Guid jobId, string? state, int? rowsRead, string? message, CancellationToken ct) =>
        ExecAsync(Estado, new { JobId = jobId, Estado = state, FilasLeidas = rowsRead, Mensaje = message }, 30, ct);

    public async Task<RecoveryJob?> GetAsync(Guid jobId, CancellationToken ct)
    {
        await using var c = connectionFactory.Create();
        return await c.QueryFirstOrDefaultAsync<RecoveryJob>(new CommandDefinition(Estado, new { JobId = jobId, Consultar = true },
            commandType: CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RecoveryJob>> ListAsync(CancellationToken ct)
    {
        await using var c = connectionFactory.Create();
        return (await c.QueryAsync<RecoveryJob>(new CommandDefinition(Estado, new { JobId = (Guid?)null, Consultar = true },
            commandType: CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct))).ToList();
    }

    public async Task<RecoveryPage> HistoryAsync(DateOnly? dia, string? canal, string? q, int page, int size, CancellationToken ct)
    {
        await using var c = connectionFactory.Create();
        using var r = await c.QueryMultipleAsync(new CommandDefinition("dbo.USP_RECUPERACION_HISTORIAL_VOICES",
            new { Dia = dia?.ToDateTime(TimeOnly.MinValue), Canal = canal, Q = Cut(q, 260), Page = page, Size = size },
            commandType: CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: ct));
        var total = await r.ReadSingleAsync<int>();
        var items = (await r.ReadAsync<RecoveryJob>()).ToList();
        return new RecoveryPage(total, items);
    }

    public async Task<int> BulkLoadLightAsync(Guid jobId, IAsyncEnumerable<RecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct)
    {
        await using var c = connectionFactory.CreateForBulk();
        await c.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = Detalle, BulkCopyTimeout = 0, BatchSize = 100_000, EnableStreaming = true
        };
        foreach (var col in RecoveryRowDataReader.LightColumns) bulk.ColumnMappings.Add(col, col);

        await using var reader = RecoveryRowDataReader.Light(jobId, rows, onProgress, ct);
        await bulk.WriteToServerAsync(reader, ct);
        await onProgress(reader.Count);
        return reader.Count;
    }

    public async Task<IdentifierSet> GetMissingIdsAsync(Guid jobId, CancellationToken ct)
    {
        var set = new IdentifierSet();
        await using var c = connectionFactory.Create();
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand("dbo.USP_RECUPERACION_FALTANTES_VOICES", c) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
        cmd.Parameters.Add(new SqlParameter("@JobId", SqlDbType.UniqueIdentifier) { Value = jobId });
        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await rd.ReadAsync(ct)) set.Add(rd.GetString(0));
        return set;
    }

    public async Task BulkLoadBatchAsync(Guid jobId, int lote, IReadOnlyList<RecoveryRow> rows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await BulkLoadBatchOnceAsync(jobId, lote, rows, ct);
        logger.LogInformation("[RECOVERY CARGA] Lote={Lote} Filas={Filas} {Ms}ms (tabla de paso)", lote, rows.Count, sw.ElapsedMilliseconds);
    }

    private async Task BulkLoadBatchOnceAsync(Guid jobId, int lote, IReadOnlyList<RecoveryRow> rows, CancellationToken ct)
    {
        await using var c = connectionFactory.CreateForBulk();
        await c.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = Carga, BulkCopyTimeout = 0, BatchSize = rows.Count, EnableStreaming = true
        };
        foreach (var col in RecoveryRowDataReader.FullColumns) bulk.ColumnMappings.Add(col, col);
        await using var reader = RecoveryRowDataReader.Full(jobId, lote, rows);
        await bulk.WriteToServerAsync(reader, ct);
    }

    private static readonly TimeZoneInfo Mexico = FindMexico();

    public async Task<bool> BulkInsertFinalAsync(Guid jobId, IReadOnlyList<RecoveryRow> rows, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try { return await BulkInsertFinalOnceAsync(jobId, rows, ct); }
            catch (SqlException ex) when (ex.Number == 1205 && intento < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * intento), ct);
            }
        }
    }

    private async Task<bool> BulkInsertFinalOnceAsync(Guid jobId, IReadOnlyList<RecoveryRow> rows, CancellationToken ct)
    {
        var nowMx = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Mexico);
        var sw = Stopwatch.StartNew();
        await using var c = connectionFactory.CreateForBulk();
        var paquete = c.PacketSize;
        await c.OpenAsync(ct);
        var conexionMs = sw.ElapsedMilliseconds;
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        var inicioTxMs = sw.ElapsedMilliseconds;
        try
        {
            using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.TableLock, tx)
            {
                DestinationTableName = Final, BulkCopyTimeout = 0, BatchSize = rows.Count, EnableStreaming = true
            };
            foreach (var col in RecoveryRowDataReader.FinalColumns) bulk.ColumnMappings.Add(col, col);
            await using var reader = RecoveryRowDataReader.Final(jobId, rows, nowMx);
            var inicioCargaMs = sw.ElapsedMilliseconds;
            await bulk.WriteToServerAsync(reader, ct);
            var finCargaMs = sw.ElapsedMilliseconds;
            await tx.CommitAsync(ct);
            var finCommitMs = sw.ElapsedMilliseconds;

            logger.LogInformation(
                "[RECOVERY BULK] Paquete={Paquete} Filas={Filas} Conexion={Conexion}ms InicioTx={InicioTx}ms BulkCopy={BulkCopy}ms Commit={Commit}ms Total={Total}ms ({FilasPorSeg:N0} filas/s)",
                paquete, rows.Count, conexionMs, inicioTxMs - conexionMs, finCargaMs - inicioCargaMs, finCommitMs - finCargaMs, finCommitMs,
                rows.Count * 1000.0 / Math.Max(1, finCommitMs));
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            logger.LogWarning("[RECOVERY BULK] Filas={Filas} CHOCÓ con llave repetida tras {Total}ms → camino seguro (carga + SP 20)",
                rows.Count, sw.ElapsedMilliseconds);
            await SafeRollbackAsync(tx);
            return false;
        }
        catch
        {
            await SafeRollbackAsync(tx);
            throw;
        }
    }

    private static async Task SafeRollbackAsync(SqlTransaction tx)
    {
        try { await tx.RollbackAsync(); } catch (InvalidOperationException) { } catch (SqlException) { }
    }

    private static TimeZoneInfo FindMexico()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"); }
    }

    public Task AddInsertedAsync(Guid jobId, int count, CancellationToken ct) =>
        ExecAsync(Estado, new { JobId = jobId, SumarInsertadas = count }, 30, ct);

    public async Task InsertBatchAsync(Guid jobId, int lote, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await ExecAsync("dbo.USP_RECUPERACION_INSERTAR_LOTE_VOICES", new { JobId = jobId, Lote = lote }, 0, ct);
        if (lote >= 0) logger.LogInformation("[RECOVERY SP20] Lote={Lote} {Ms}ms", lote, sw.ElapsedMilliseconds);
    }

    public Task CompareAsync(Guid jobId, CancellationToken ct) =>
        ExecAsync("dbo.USP_RECUPERACION_COMPARAR_VOICES", new { JobId = jobId }, 0, ct);

    public Task InsertMissingAsync(Guid jobId, CancellationToken ct) =>
        ExecAsync("dbo.USP_RECUPERACION_INSERTAR_VOICES", new { JobId = jobId, Cancelar = false }, 0, ct);

    public async Task CancelAsync(Guid jobId, CancellationToken ct)
    {
        await ExecAsync("dbo.USP_RECUPERACION_INSERTAR_VOICES", new { JobId = jobId, Cancelar = true }, 0, ct);
        await ExecAsync("dbo.USP_RECUPERACION_SMS_INSERTAR_VOICES", new { JobId = jobId, Cancelar = true }, 0, ct);
    }

    private async Task ExecAsync(string sp, object args, int timeoutSeconds, CancellationToken ct)
    {
        await using var c = connectionFactory.Create();
        await c.ExecuteAsync(new CommandDefinition(sp, args, commandType: CommandType.StoredProcedure,
            commandTimeout: timeoutSeconds, cancellationToken: ct));
    }

    private static string? Cut(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}
