using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;

namespace VoicesIntegration.Infrastructure.Inbox;

/// <summary>
/// Bandeja de entrada en DISCO. Un archivo por aviso:
///   inbox\tmp\        → se escribe aquí primero
///   inbox\pending\    → se MUEVE aquí cuando ya está completo (mover es atómico: nunca hay archivos a medias)
///   inbox\rejected\   → avisos inválidos + archivo .motivo.txt
/// Nombre: {ticks UTC de llegada}_{canal}_{guid}.json → ordenar por nombre = orden de llegada.
/// Sobrevive reinicios del IIS: al arrancar, lo pendiente se procesa.
/// </summary>
internal sealed class FileWebhookInbox : IWebhookInbox, IDisposable
{
    private readonly string _tmp, _pending, _rejected;
    private readonly SemaphoreSlim _signal = new(0);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public FileWebhookInbox(IOptions<InboxOptions> options)
    {
        var root = options.Value.Path;
        _tmp = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
        _pending = Directory.CreateDirectory(Path.Combine(root, "pending")).FullName;
        _rejected = Directory.CreateDirectory(Path.Combine(root, "rejected")).FullName;
    }

    public async Task SaveAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        var name = NewName(channel, receivedAtUtc);
        var tmpPath = Path.Combine(_tmp, name);

        // WriteThrough: el sistema operativo escribe a disco de inmediato (no se queda en caché).
        await using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await fs.WriteAsync(Utf8.GetBytes(rawPayload), cancellationToken);
            await fs.FlushAsync(cancellationToken);
        }

        File.Move(tmpPath, Path.Combine(_pending, name));
        _signal.Release();   // despierta al procesador
    }

    public async Task SaveRejectedAsync(string channel, string rawPayload, DateTimeOffset receivedAtUtc, string reason, CancellationToken cancellationToken)
    {
        var name = NewName(channel, receivedAtUtc);
        await File.WriteAllTextAsync(Path.Combine(_rejected, name), rawPayload, Utf8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_rejected, name + ".motivo.txt"), reason, Utf8, cancellationToken);
    }


    //Ireandoly list 
    // NO ordenar toda la carpeta: con cientos de miles de archivos eso tarda muchísimo en CADA lote
    // y la bandeja se atora (pasó el 2026-11-06 con ~356 mil). NTFS ya entrega los nombres en orden
    // y el nombre empieza con los ticks de llegada → los primeros que salen son los más viejos.
    // Se toman 'max' y solo esos se ordenan (por si el disco no fuera NTFS).
    public IReadOnlyList<InboxItem> GetPending(int max) =>
        Directory.EnumerateFiles(_pending, "*.json")
            .Select(Path.GetFileName)
            .Take(max)
            .Order(StringComparer.Ordinal)
            .Select(n => new InboxItem(n!, ParseChannel(n!), ParseReceivedAt(n!)))
            .ToList();

    public Task<string> ReadAsync(InboxItem item, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Path.Combine(_pending, item.Id), Utf8, cancellationToken);

    public void Complete(InboxItem item) => File.Delete(Path.Combine(_pending, item.Id));

    public void Reject(InboxItem item, string reason)
    {
        File.Move(Path.Combine(_pending, item.Id), Path.Combine(_rejected, item.Id), overwrite: true);
        File.WriteAllText(Path.Combine(_rejected, item.Id + ".motivo.txt"), reason, Utf8);
    }

    public InboxStatus GetStatus()
    {
        // Solo CONTAR (sin guardar ni ordenar la lista): se llama cada minuto (latido, alertas, /health).
        // El primero que entrega NTFS es el más viejo (nombre = ticks de llegada).
        var pendingCount = 0;
        string? oldest = null;
        foreach (var f in Directory.EnumerateFiles(_pending, "*.json"))
        {
            if (pendingCount++ == 0) oldest = Path.GetFileName(f);
        }
        var rejected = Directory.EnumerateFiles(_rejected, "*.json").Count();
        long? freeMb = null;
        try { freeMb = new DriveInfo(Path.GetPathRoot(_pending)!).AvailableFreeSpace / (1024 * 1024); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return new InboxStatus(pendingCount, rejected, oldest is null ? null : ParseReceivedAt(oldest), freeMb);
    }

    /// <summary>
    /// Limpieza para que el disco NO se llene:
    /// - "rejected": borra los avisos (y su .motivo.txt) más viejos que <paramref name="rejectedRetention"/>.
    /// - "tmp": borra archivos abandonados (se cortó la luz a media escritura) más viejos que <paramref name="tmpMaxAge"/>.
    /// "pending" NUNCA se limpia aquí: cada aviso se borra solo en cuanto se guarda en la BD.
    /// </summary>
    public int Cleanup(TimeSpan rejectedRetention, TimeSpan tmpMaxAge)
    {
        var now = DateTime.UtcNow;
        var borrados = 0;

        foreach (var f in Directory.EnumerateFiles(_rejected))
            if (now - File.GetLastWriteTimeUtc(f) > rejectedRetention && TryDelete(f)) borrados++;

        foreach (var f in Directory.EnumerateFiles(_tmp))
            if (now - File.GetLastWriteTimeUtc(f) > tmpMaxAge && TryDelete(f)) borrados++;

        return borrados;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }          // en uso: se intenta en la siguiente limpieza
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task WaitForItemsAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(maxWait, cancellationToken);
        while (_signal.CurrentCount > 0) _signal.Wait(0);   // junta varias señales en una sola vuelta
    }

    public void Dispose() => _signal.Dispose();

    private static string NewName(string channel, DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(channel) || !channel.All(char.IsAsciiLetterLower))
            throw new ArgumentException("Canal inválido.", nameof(channel));
        return $"{receivedAtUtc.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)}_{channel}_{Guid.NewGuid():N}.json";
    }

    /// <summary>Archivos viejos (sin canal en el nombre) eran todos de Blaster.</summary>
    private static string ParseChannel(string fileName)
    {
        var parts = fileName.Split('_');
        return parts.Length >= 3 ? parts[1] : WebhookChannels.Blaster;
    }

    private static DateTimeOffset ParseReceivedAt(string fileName) =>
        long.TryParse(fileName.AsSpan(0, Math.Min(19, fileName.Length)), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : File.GetCreationTimeUtc(fileName);
}
