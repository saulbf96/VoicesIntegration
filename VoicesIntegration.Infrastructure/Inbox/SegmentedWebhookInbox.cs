using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;

namespace VoicesIntegration.Infrastructure.Inbox;

/// <summary>
/// Bandeja de entrada por SEGMENTOS (2026-11-07). Antes se creaba UN ARCHIVO POR AVISO (~1 millón al día en una
/// carpeta): con cientos de miles acumulados el disco se saturaba, el webhook tardaba segundos en contestar, el
/// túnel rechazaba con 429 y leer/borrar 2,000 archivos llegó a tardar 5 minutos.
///
/// Ahora:
///   inbox\segments\open\   → segmento ABIERTO: un archivo .jsonl, UNA LÍNEA por aviso (se agrega al final).
///   inbox\segments\ready\  → segmento CERRADO (cada <see cref="InboxOptions.SegmentMaxItems"/> avisos o
///                            <see cref="InboxOptions.SegmentMaxSeconds"/> s): listo para pasarse a la BD.
///   inbox\rejected\        → avisos inválidos + .motivo.txt (igual que antes).
///
/// Durabilidad: un solo escritor junta los avisos que llegan al mismo tiempo, los escribe y hace UN fsync por grupo
/// ("group commit"); solo entonces el webhook contesta 200. Así es igual de seguro que antes pero con ~1,000 veces
/// menos operaciones de disco.
///
/// Un segmento se borra solo cuando TODOS sus avisos se guardaron o se rechazaron. Si la API se cae a la mitad,
/// el segmento se vuelve a procesar completo (sin duplicar: la BD ignora lo que ya tiene).
///
/// Los avisos de la bandeja VIEJA (un archivo por aviso en inbox\pending) se procesan PRIMERO, en orden, hasta
/// que se acaben; los nuevos ya solo entran por segmentos.
/// </summary>
internal sealed class SegmentedWebhookInbox : IWebhookInbox, IAsyncDisposable, IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private const string Ext = ".jsonl";

    private readonly FileWebhookInbox _legacy;
    private readonly ILogger<SegmentedWebhookInbox> _logger;
    private readonly int _maxItems;
    private readonly TimeSpan _maxAge;
    private readonly string _open, _ready, _rejected, _root;

    private readonly Channel<Entry> _queue = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _sync = new();
    private readonly List<Segment> _loaded = [];     // segmentos leídos a memoria, en orden
    private long _seq;
    private volatile int _openCount;                  // avisos en el segmento abierto (aún no listos)

    // Bandeja vieja: se cuenta UNA vez en segundo plano y luego se descuenta al completar.
    private int _legacyPending;
    private volatile bool _legacyDone;
    private DateTimeOffset? _legacyOldest;

    public SegmentedWebhookInbox(FileWebhookInbox legacy, IOptions<InboxOptions> options, ILogger<SegmentedWebhookInbox> logger)
    {
        _legacy = legacy;
        _logger = logger;
        var o = options.Value;
        _maxItems = o.SegmentMaxItems;
        _maxAge = TimeSpan.FromSeconds(o.SegmentMaxSeconds);
        _root = o.Path;
        _open = Directory.CreateDirectory(Path.Combine(o.Path, "segments", "open")).FullName;
        _ready = Directory.CreateDirectory(Path.Combine(o.Path, "segments", "ready")).FullName;
        _rejected = Directory.CreateDirectory(Path.Combine(o.Path, "rejected")).FullName;

        // Segmentos que quedaron abiertos porque la API se apagó de golpe: pasan a "listos" (lo escrito vale).
        foreach (var f in Directory.EnumerateFiles(_open, "*" + Ext))
        {
            var lines = CountCompleteLines(f);
            File.Move(f, Path.Combine(_ready, ReadyName(Path.GetFileNameWithoutExtension(f), lines)), overwrite: true);
        }

        _ = Task.Run(CountLegacy);
        _writer = Task.Run(WriterLoopAsync);
    }

    // ===================== Escritura (webhook) =====================

    public async Task SaveAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channel) || !channel.All(char.IsAsciiLetterLower))
            throw new ArgumentException("Canal inválido.", nameof(channel));

        var entry = new Entry(channel, rawPayload, receivedAtUtc, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_queue.Writer.TryWrite(entry))
            throw new InvalidOperationException("La bandeja se está cerrando.");
        await entry.Done.Task.WaitAsync(cancellationToken);   // regresa cuando ya está en disco (fsync)
    }

    public Task SaveRejectedAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, string reason, CancellationToken cancellationToken) =>
        _legacy.SaveRejectedAsync(channel, rawPayload, receivedAtUtc, reason, cancellationToken);

    private async Task WriterLoopAsync()
    {
        FileStream? fs = null;
        string? openPath = null;
        var count = 0;
        var openedAt = DateTime.UtcNow;
        var batch = new List<Entry>(1024);
        var buffer = new MemoryStream();

        void Rotate()
        {
            if (fs is null) return;
            fs.Dispose();
            fs = null;
            File.Move(openPath!, Path.Combine(_ready, ReadyName(Path.GetFileNameWithoutExtension(openPath!), count)));
            count = 0;
            _openCount = 0;
            _signal.Release();   // despierta al procesador
        }

        while (true)
        {
            // Esperar avisos; si hay un segmento abierto, a lo más hasta que le toque cerrarse.
            var hayMas = true;
            if (!_queue.Reader.TryPeek(out _))
            {
                using var cts = new CancellationTokenSource();
                if (fs is not null)
                {
                    var restante = openedAt + _maxAge - DateTime.UtcNow;
                    cts.CancelAfter(restante > TimeSpan.Zero ? restante : TimeSpan.Zero);
                }
                try { hayMas = await _queue.Reader.WaitToReadAsync(cts.Token); }
                catch (OperationCanceledException) { /* tocó cerrar el segmento */ }
            }

            batch.Clear();
            while (batch.Count < 2000 && _queue.Reader.TryRead(out var e)) batch.Add(e);

            if (batch.Count > 0)
            {
                try
                {
                    if (fs is null)
                    {
                        openedAt = DateTime.UtcNow;
                        var name = $"{DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture)}_{Interlocked.Increment(ref _seq):D6}";
                        openPath = Path.Combine(_open, name + Ext);
                        fs = new FileStream(openPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
                    }

                    buffer.SetLength(0);
                    foreach (var e in batch) WriteLine(buffer, e);
                    fs.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                    fs.Flush(flushToDisk: true);          // UN fsync para todo el grupo
                    count += batch.Count;
                    _openCount = count;
                    foreach (var e in batch) e.Done.TrySetResult();
                }
                catch (Exception ex)
                {
                    // No se pudo escribir: esos webhooks reciben error y Voices los reintenta. Lo escrito antes se conserva.
                    foreach (var e in batch) e.Done.TrySetException(ex);
                    _logger.LogError(ex, "No se pudo escribir en la bandeja ({Count} avisos); Voices los reintentará.", batch.Count);
                    try { Rotate(); } catch (Exception ex2) { _logger.LogError(ex2, "No se pudo cerrar el segmento"); fs = null; }
                }
            }

            if (fs is not null && (count >= _maxItems || DateTime.UtcNow - openedAt >= _maxAge))
            {
                try { Rotate(); }
                catch (Exception ex) { _logger.LogError(ex, "No se pudo cerrar el segmento {Path}; se reintenta.", openPath); }
            }

            if (!hayMas && !_queue.Reader.TryPeek(out _))
            {
                try { Rotate(); } catch (Exception ex) { _logger.LogError(ex, "No se pudo cerrar el último segmento"); }
                return;   // se está apagando la API
            }
        }
    }

    private static void WriteLine(MemoryStream ms, Entry e)
    {
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("c", e.Channel);
            w.WriteNumber("t", e.ReceivedAtUtc.UtcTicks);
            w.WriteString("p", e.Raw);   // el JSON original tal cual (la huella se calcula sobre este texto)
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
    }

    // ===================== Lectura (procesador) =====================

    public IReadOnlyList<InboxItem> GetPending(int max)
    {
        // 1) Primero lo que quedó de la bandeja vieja (más antiguo).
        if (!_legacyDone)
        {
            var viejos = _legacy.GetPending(max);
            if (viejos.Count > 0)
            {
                _legacyOldest = viejos[0].ReceivedAtUtc;
                return viejos;
            }
            _legacyDone = true;
            Interlocked.Exchange(ref _legacyPending, 0);
            _logger.LogInformation("La bandeja vieja (un archivo por aviso) quedó vacía; ya todo entra por segmentos.");
        }

        // 2) Segmentos listos, en orden.
        lock (_sync)
        {
            var result = new List<InboxItem>(max);
            foreach (var s in _loaded)
            {
                foreach (var it in s.Pendientes())
                {
                    result.Add(it);
                    if (result.Count >= max) return result;
                }
            }

            var yaCargados = _loaded.Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(_ready, "*" + Ext).Order(StringComparer.Ordinal))
            {
                if (yaCargados.Contains(Path.GetFileName(path))) continue;
                var s = Load(path);
                if (s.Restantes == 0) { TryDelete(path); continue; }
                _loaded.Add(s);
                foreach (var it in s.Pendientes())
                {
                    result.Add(it);
                    if (result.Count >= max) return result;
                }
            }
            return result;
        }
    }

    public Task<string> ReadAsync(InboxItem item, CancellationToken cancellationToken)
    {
        if (!IsSegmentItem(item.Id)) return _legacy.ReadAsync(item, cancellationToken);
        lock (_sync)
        {
            var (s, i) = Find(item.Id);
            return Task.FromResult(s.Raws[i]);
        }
    }

    public void Complete(InboxItem item)
    {
        if (!IsSegmentItem(item.Id))
        {
            _legacy.Complete(item);
            Interlocked.Decrement(ref _legacyPending);
            return;
        }
        lock (_sync) MarkDone(item.Id);
    }

    public void Reject(InboxItem item, string reason)
    {
        if (!IsSegmentItem(item.Id))
        {
            _legacy.Reject(item, reason);
            Interlocked.Decrement(ref _legacyPending);
            return;
        }

        string raw;
        lock (_sync) { var (s, i) = Find(item.Id); raw = s.Raws[i]; }
        var name = $"{item.ReceivedAtUtc.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)}_{SafeChannel(item.Channel)}_{Guid.NewGuid():N}.json";
        File.WriteAllText(Path.Combine(_rejected, name), raw, Utf8);
        File.WriteAllText(Path.Combine(_rejected, name + ".motivo.txt"), reason, Utf8);
        lock (_sync) MarkDone(item.Id);
    }

    public InboxStatus GetStatus()
    {
        int segmentos;
        DateTimeOffset? oldest = null;
        lock (_sync)
        {
            segmentos = _loaded.Sum(s => s.Restantes);
            oldest = _loaded.Select(s => s.Oldest).FirstOrDefault(o => o is not null);
            var yaCargados = _loaded.Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(_ready, "*" + Ext))
            {
                var n = Path.GetFileName(f);
                if (yaCargados.Contains(n)) continue;
                segmentos += CountFromName(n);
                oldest ??= TicksFromName(n);
            }
        }
        segmentos += _openCount;

        var legacy = _legacyDone ? 0 : Math.Max(0, Volatile.Read(ref _legacyPending));
        if (!_legacyDone && _legacyOldest is { } lo) oldest = lo;

        var rejected = Directory.EnumerateFiles(_rejected, "*.json").Count();
        long? freeMb = null;
        try { freeMb = new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace / (1024 * 1024); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new InboxStatus(legacy + segmentos, rejected, oldest, freeMb);
    }

    public int Cleanup(TimeSpan rejectedRetention, TimeSpan tmpMaxAge) => _legacy.Cleanup(rejectedRetention, tmpMaxAge);

    public async Task WaitForItemsAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(maxWait, cancellationToken);
        while (_signal.CurrentCount > 0) _signal.Wait(0);
    }

    // ===================== Apagado =====================

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception ex) { _logger.LogWarning(ex, "La bandeja no cerró a tiempo"); }
        _signal.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // ===================== Ayudantes =====================

    private sealed record Entry(string Channel, string Raw, DateTimeOffset ReceivedAtUtc, TaskCompletionSource Done);

    private sealed class Segment(string path, List<InboxItem> items, List<string> raws)
    {
        public string Path { get; } = path;
        public string FileName { get; } = System.IO.Path.GetFileName(path);
        public List<InboxItem> Items { get; } = items;
        public List<string> Raws { get; } = raws;
        public bool[] Done { get; } = new bool[items.Count];
        public int Restantes { get; set; } = items.Count;
        public DateTimeOffset? Oldest => Items.Where((_, i) => !Done[i]).Select(x => (DateTimeOffset?)x.ReceivedAtUtc).FirstOrDefault();
        public IEnumerable<InboxItem> Pendientes() => Items.Where((_, i) => !Done[i]);
    }

    private static bool IsSegmentItem(string id) => id.Contains('#');

    private (Segment s, int i) Find(string id)
    {
        var p = id.LastIndexOf('#');
        var file = id[..p];
        var i = int.Parse(id.AsSpan(p + 1), CultureInfo.InvariantCulture);
        var s = _loaded.FirstOrDefault(x => x.FileName == file) ?? throw new IOException($"El segmento {file} ya no está cargado.");
        return (s, i);
    }

    private void MarkDone(string id)
    {
        var (s, i) = Find(id);
        if (s.Done[i]) return;
        s.Done[i] = true;
        s.Restantes--;
        if (s.Restantes == 0)
        {
            _loaded.Remove(s);
            TryDelete(s.Path);
        }
    }

    private Segment Load(string path)
    {
        var fileName = System.IO.Path.GetFileName(path);
        var items = new List<InboxItem>();
        var raws = new List<string>();
        var lines = File.ReadAllLines(path, Utf8);
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var channel = r.GetProperty("c").GetString()!;
                var ticks = r.GetProperty("t").GetInt64();
                var raw = r.GetProperty("p").GetString()!;
                items.Add(new InboxItem($"{fileName}#{items.Count}", channel, new DateTimeOffset(ticks, TimeSpan.Zero)));
                raws.Add(raw);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Última línea a medias = se cortó la luz mientras se escribía: ese webhook NO recibió 200 y Voices lo reintenta.
                if (n == lines.Length - 1) { _logger.LogWarning("Segmento {File}: última línea incompleta (se ignora).", fileName); continue; }
                // Línea dañada en medio: se manda a rechazados para revisarla (canal "corrupto" → se rechaza al procesar).
                items.Add(new InboxItem($"{fileName}#{items.Count}", "corrupto", DateTimeOffset.UtcNow));
                raws.Add(line);
            }
        }
        return new Segment(path, items, raws);
    }

    private void CountLegacy()
    {
        try
        {
            var pending = Path.Combine(_root, "pending");
            if (!Directory.Exists(pending)) { _legacyDone = true; return; }
            var n = 0;
            foreach (var _ in Directory.EnumerateFiles(pending, "*.json"))
            {
                n++;
                if (n % 10_000 == 0) Interlocked.Add(ref _legacyPending, 10_000);
            }
            Interlocked.Add(ref _legacyPending, n % 10_000);
            _logger.LogInformation("Bandeja vieja: {Count} avisos por pasar a la BD (se procesan antes que los nuevos).", n);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo contar la bandeja vieja");
        }
    }

    private static int CountCompleteLines(string path)
    {
        var n = 0;
        foreach (var l in File.ReadLines(path, Utf8)) if (l.Length > 0) n++;
        return n;
    }

    private static string ReadyName(string baseName, int count) => $"{baseName}_{count}{Ext}";

    private static int CountFromName(string fileName)
    {
        var b = Path.GetFileNameWithoutExtension(fileName);
        var p = b.LastIndexOf('_');
        return p > 0 && int.TryParse(b.AsSpan(p + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0;
    }

    private static DateTimeOffset? TicksFromName(string fileName) =>
        long.TryParse(fileName.AsSpan(0, Math.Min(19, fileName.Length)), NumberStyles.None, CultureInfo.InvariantCulture, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    private static string SafeChannel(string c) => c.All(char.IsAsciiLetterLower) && c.Length > 0 ? c : "otro";

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo borrar el segmento {Path}; se reintenta después.", path);
        }
    }
}
