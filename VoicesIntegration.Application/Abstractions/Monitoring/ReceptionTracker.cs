namespace VoicesIntegration.Application.Abstractions.Monitoring;

/// <summary>
/// Lleva en memoria cuándo llegó el último aviso de Voices (Blaster o SMS) desde que arrancó la API.
/// Lo usa el vigilante de alertas para avisar "empezó a recibir" y "no llegan datos desde las HH:mm".
/// Es muy barato: se marca en cada aviso recibido.
/// </summary>
public sealed class ReceptionTracker
{
    private long _lastTicks;    // UTC
    private long _firstTicks;   // UTC, primer aviso desde que arrancó la API
    private long _count;
    private long _saved;        // avisos guardados en BD desde que arrancó (para saber si la bandeja avanza)

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

    /// <summary>Lo llama la bandeja al guardar un lote en la BD.</summary>
    public void MarkSaved(int count) { if (count > 0) Interlocked.Add(ref _saved, count); }

    public long SavedTotal => Interlocked.Read(ref _saved);

    private static DateTimeOffset? Read(ref long ticks)
    {
        var t = Interlocked.Read(ref ticks);
        return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
    }
}
