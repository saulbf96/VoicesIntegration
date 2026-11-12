namespace VoicesIntegration.Application.Abstractions.Monitoring;

// Ultimo aviso recibido y total guardado (en memoria)
public sealed class ReceptionTracker
{
    private long _lastTicks;
    private long _firstTicks;
    private long _count;
    private long _saved;
    private long _lastSavedTicks;
    private long _lastErrorTicks;
    private volatile string? _lastError;

    public void Mark(DateTimeOffset receivedAtUtc)
    {
        var t = receivedAtUtc.UtcTicks;
        Interlocked.Exchange(ref _lastTicks, t);
        Interlocked.CompareExchange(ref _firstTicks, t, 0);
        Interlocked.Increment(ref _count);
    }

    public DateTimeOffset? LastUtc => Read(ref _lastTicks);
    public DateTimeOffset? FirstUtc => Read(ref _firstTicks);
    public long Count => Interlocked.Read(ref _count);

    public void MarkSaved(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _saved, count);
        Interlocked.Exchange(ref _lastSavedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    // Ultimo error de BD al guardar la bandeja
    public void MarkError(string message)
    {
        _lastError = message;
        Interlocked.Exchange(ref _lastErrorTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public DateTimeOffset? LastSavedUtc => Read(ref _lastSavedTicks);
    public DateTimeOffset? LastErrorUtc => Read(ref _lastErrorTicks);
    public string? LastError => _lastError;

    public long SavedTotal => Interlocked.Read(ref _saved);

    private static DateTimeOffset? Read(ref long ticks)
    {
        var t = Interlocked.Read(ref ticks);
        return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
    }
}
