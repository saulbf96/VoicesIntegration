using System.Collections;
using System.Data.Common;
using VoicesIntegration.Application.Abstractions.Recovery;

namespace VoicesIntegration.Infrastructure.Persistence;

internal sealed class SmsRecoveryDataReader(Guid jobId, IAsyncEnumerable<SmsRecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct)
    : DbDataReader
{
    internal static readonly string[] Columns =
        ["JOB_ID", "LOTE", "message_id", "to", "message", "status", "operator", "batch_id", "origin_identifier", "created_at", "sent_at", "failed_at", "status_at"];
    private const int Block = 100_000;

    private readonly IAsyncEnumerator<SmsRecoveryRow> _e = rows.GetAsyncEnumerator(ct);
    private SmsRecoveryRow? _row;
    public int Count { get; private set; }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await _e.MoveNextAsync()) { _row = null; return false; }
        _row = _e.Current;
        if (++Count % Block == 0) await onProgress(Count);
        return true;
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override object GetValue(int ordinal)
    {
        var r = _row!;
        object? v = ordinal switch
        {
            0 => jobId, 1 => (Count - 1) / Block, 2 => r.MessageId, 3 => r.To, 4 => r.Message, 5 => r.Status, 6 => r.Operator,
            7 => r.BatchId, 8 => r.OriginIdentifier, 9 => r.CreatedAt, 10 => r.SentAt, 11 => r.FailedAt, 12 => r.StatusAt,
            _ => throw new IndexOutOfRangeException()
        };
        return v ?? DBNull.Value;
    }

    public override Type GetFieldType(int ordinal) => ordinal switch
    {
        0 => typeof(Guid), 1 => typeof(int), >= 9 and <= 12 => typeof(DateTime), _ => typeof(string)
    };

    public override int FieldCount => Columns.Length;
    public override string GetName(int ordinal) => Columns[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(Columns, name);
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
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override async ValueTask DisposeAsync() { await _e.DisposeAsync(); await base.DisposeAsync(); }
}
