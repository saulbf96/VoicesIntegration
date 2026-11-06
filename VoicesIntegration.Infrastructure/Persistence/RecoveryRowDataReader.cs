using System.Collections;
using System.Data.Common;
using VoicesIntegration.Application.Abstractions.Recovery;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>
/// Adaptador "streaming" para SqlBulkCopy: SQL recibe las filas al vuelo, sin armar tablas en memoria.
/// Ligero (temporal de comparación): JOB_ID, LOTE (bloques de 100 mil), identifier, sip_code, fechas y a_num.
/// Completo (tabla de carga): JOB_ID, LOTE y las 22 columnas del reporte.
/// Final (directo a dbo.TBL_LLAMADAS_VOICES): las 22 columnas + status, ORIGEN y fechas en hora de México.
/// </summary>
internal sealed class RecoveryRowDataReader : DbDataReader
{
    internal static readonly string[] LightColumns = ["JOB_ID", "LOTE", "identifier", "sip_code", "dialed_at", "failed_at", "a_num"];
    internal static readonly string[] FullColumns =
    [
        "JOB_ID", "LOTE", "identifier", "campaign_id", "campaign_name", "channel", "phone_number", "type", "data", "message",
        "call_attempts", "duration_minutes", "duration_seconds", "duration_transfer_minutes", "duration_transfer_seconds",
        "credits", "dialed_at", "failed_at", "sip_code", "sip_description", "transfer", "sip_code_transfer",
        "sip_description_transfer", "a_num"
    ];
    internal static readonly string[] FinalColumns =
        [.. FullColumns.Skip(2), "status", "ORIGEN", "FECHA_REGISTRO", "FECHA_ULTIMO_EVENTO", "FECHA_ACTUALIZACION"];
    internal const int LightBlock = 100_000;

    private enum Mode { Light, Full, Final }
    private const int ProgressEvery = 100_000;

    private readonly Guid _jobId;
    private readonly Mode _mode;
    private readonly int _fixedLote;
    private readonly DateTime _nowMx;
    private readonly string[] _columns;
    private readonly IAsyncEnumerator<RecoveryRow> _e;
    private readonly Func<int, Task>? _onProgress;
    private RecoveryRow? _row;

    public int Count { get; private set; }

    /// <summary>Ligero: el LOTE se calcula solo (cada 100 mil filas).</summary>
    public static RecoveryRowDataReader Light(Guid jobId, IAsyncEnumerable<RecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct) =>
        new(jobId, Mode.Light, -1, rows.GetAsyncEnumerator(ct), onProgress);

    /// <summary>Completo: todas las filas del lote indicado.</summary>
    public static RecoveryRowDataReader Full(Guid jobId, int lote, IReadOnlyList<RecoveryRow> rows) =>
        new(jobId, Mode.Full, lote, new ListEnumerator(rows), null);

    /// <summary>Final: filas listas para la tabla de llamadas (ORIGEN = RECUPERACION, fechas en hora de México).</summary>
    public static RecoveryRowDataReader Final(IReadOnlyList<RecoveryRow> rows, DateTime nowMx) =>
        new(Guid.Empty, Mode.Final, -1, new ListEnumerator(rows), null, nowMx);

    private RecoveryRowDataReader(Guid jobId, Mode mode, int lote, IAsyncEnumerator<RecoveryRow> e, Func<int, Task>? onProgress, DateTime nowMx = default)
    {
        _jobId = jobId; _mode = mode; _fixedLote = lote; _e = e; _onProgress = onProgress; _nowMx = nowMx;
        _columns = mode switch { Mode.Light => LightColumns, Mode.Full => FullColumns, _ => FinalColumns };
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await _e.MoveNextAsync()) { _row = null; return false; }
        _row = _e.Current;
        if (++Count % ProgressEvery == 0 && _onProgress is not null) await _onProgress(Count);
        return true;
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override object GetValue(int ordinal)
    {
        var r = _row!;
        if (_mode == Mode.Final)
        {
            object? f = ordinal switch
            {
                22 => r.SipCode == 200 ? "SENT" : "FAILED",   // el reporte no trae status: 200 = SENT (verificado)
                23 => "RECUPERACION",
                24 or 26 => _nowMx,
                25 => r.FailedAt ?? r.DialedAt,
                18 => r.Transfer ?? false,                      // transfer es NOT NULL en la tabla final
                _ => FullValue(r, ordinal + 2)
            };
            return f ?? DBNull.Value;
        }
        if (ordinal == 0) return _jobId;
        if (ordinal == 1) return _mode == Mode.Full ? _fixedLote : (Count - 1) / LightBlock;
        object? v = _mode == Mode.Full
            ? FullValue(r, ordinal)
            : ordinal switch
            {
                2 => r.Identifier, 3 => r.SipCode, 4 => r.DialedAt, 5 => r.FailedAt, 6 => r.ANum,
                _ => throw new IndexOutOfRangeException()
            };
        return v ?? DBNull.Value;
    }

    private static object? FullValue(RecoveryRow r, int ordinal) => ordinal switch
    {
        2 => r.Identifier, 3 => r.CampaignId, 4 => r.CampaignName, 5 => r.Channel, 6 => r.PhoneNumber, 7 => r.Type,
        8 => r.Data, 9 => r.Message, 10 => r.CallAttempts, 11 => r.DurationMinutes, 12 => r.DurationSeconds,
        13 => r.DurationTransferMinutes, 14 => r.DurationTransferSeconds, 15 => r.Credits, 16 => r.DialedAt, 17 => r.FailedAt,
        18 => r.SipCode, 19 => r.SipDescription, 20 => r.Transfer, 21 => r.SipCodeTransfer, 22 => r.SipDescriptionTransfer,
        23 => r.ANum,
        _ => throw new IndexOutOfRangeException()
    };

    public override Type GetFieldType(int ordinal)
    {
        if (_mode == Mode.Final)
            return ordinal switch { 22 or 23 => typeof(string), 24 or 25 or 26 => typeof(DateTime), _ => FullType(ordinal + 2) };
        if (ordinal == 0) return typeof(Guid);
        if (ordinal == 1) return typeof(int);
        if (_mode == Mode.Light) return ordinal switch { 3 => typeof(int), 4 or 5 => typeof(DateTime), _ => typeof(string) };
        return FullType(ordinal);
    }

    private static Type FullType(int ordinal) => ordinal switch
    {
        10 or 11 or 12 or 13 or 14 or 18 or 21 => typeof(int), 15 => typeof(decimal),
        16 or 17 => typeof(DateTime), 20 => typeof(bool), _ => typeof(string)
    };

    public override int FieldCount => _columns.Length;
    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(_columns, name);
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override int GetValues(object[] values) { for (var i = 0; i < FieldCount; i++) values[i] = GetValue(i); return FieldCount; }
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override int Depth => 0;
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override async ValueTask DisposeAsync() { await _e.DisposeAsync(); await base.DisposeAsync(); }

    private sealed class ListEnumerator(IReadOnlyList<RecoveryRow> rows) : IAsyncEnumerator<RecoveryRow>
    {
        private int _i = -1;
        public RecoveryRow Current => rows[_i];
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++_i < rows.Count);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
