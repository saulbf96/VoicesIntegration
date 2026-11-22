using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Notifications;

namespace VoicesIntegration.Infrastructure.Monitoring;

public sealed class LimpiezaLogsOptions
{
    public string FilePath { get; set; } = string.Empty;

    public DayOfWeek LimpiezaDia { get; set; } = DayOfWeek.Monday;

    public string LimpiezaHora { get; set; } = "23:00";
}

// Limpieza semanal de logs
internal sealed partial class LimpiezaLogsWorker(
    IOptions<LimpiezaLogsOptions> options, INotificador notificador, TimeProvider time, ILogger<LimpiezaLogsWorker> logger)
    : BackgroundService
{
    private const string Marca = ".ultima-limpieza";
    private static readonly TimeZoneInfo Mexico = FindMexico();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), time);
        do
        {
            try { await RevisarAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Limpieza de logs: {Error}", ex.Message); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RevisarAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.FilePath) || !Directory.Exists(o.FilePath)) return;

        var ahora = TimeZoneInfo.ConvertTime(time.GetUtcNow(), Mexico).DateTime;
        var programada = UltimaProgramada(ahora, o.LimpiezaDia, Hora(o.LimpiezaHora));
        var marca = Path.Combine(o.FilePath, Marca);
        var ultima = File.Exists(marca) && DateTime.TryParse(File.ReadAllText(marca), CultureInfo.InvariantCulture, DateTimeStyles.None, out var u)
            ? u : DateTime.MinValue;
        if (ultima >= programada) return;

        var conservar = DateOnly.FromDateTime(programada);
        var borrados = new List<string>();
        var noSePudo = new List<string>();
        long bytes = 0;
        foreach (var f in ParaBorrar(Directory.EnumerateFiles(o.FilePath, "voices-*.log"), conservar).Order())
        {
            try { var len = new FileInfo(f).Length; File.Delete(f); borrados.Add(Path.GetFileName(f)); bytes += len; }
            catch (IOException) { noSePudo.Add(Path.GetFileName(f)); }
            catch (UnauthorizedAccessException) { noSePudo.Add(Path.GetFileName(f)); }
        }
        File.WriteAllText(marca, ahora.ToString("s", CultureInfo.InvariantCulture));
        logger.LogInformation("Limpieza semanal de logs: {Count} archivos borrados (se conservan los del {Dia:dd/MM/yyyy})", borrados.Count, conservar);

        var quedan = Directory.EnumerateFiles(o.FilePath, "voices-*.log").Select(Path.GetFileName).Order().ToList();
        var cuerpo =
            $"Se hizo la limpieza semanal del log de la API de Voices en {Environment.MachineName} ({ahora:dd/MM/yyyy HH:mm}).\n" +
            $"Carpeta: {o.FilePath}\n\n" +
            $"Borrados: {borrados.Count} archivo(s), {bytes / 1048576.0:N1} MB (anteriores al {conservar:dd/MM/yyyy}).\n" +
            (borrados.Count > 0 ? "  " + string.Join("\n  ", borrados) + "\n" : "") +
            (noSePudo.Count > 0 ? $"\nNo se pudieron borrar (estaban abiertos, se intenta la próxima semana):\n  {string.Join("\n  ", noSePudo)}\n" : "") +
            $"\nSe conservan: {quedan.Count} archivo(s).\n" + (quedan.Count > 0 ? "  " + string.Join("\n  ", quedan) : "");
        await notificador.EnviarAsync($"Limpieza semanal del log: {borrados.Count} archivo(s) borrado(s)", cuerpo, ct);
    }

    internal static DateTime UltimaProgramada(DateTime ahora, DayOfWeek dia, TimeSpan hora)
    {
        var atras = ((int)ahora.DayOfWeek - (int)dia + 7) % 7;
        var candidata = ahora.Date.AddDays(-atras) + hora;
        return candidata <= ahora ? candidata : candidata.AddDays(-7);
    }

    internal static IEnumerable<string> ParaBorrar(IEnumerable<string> archivos, DateOnly conservar)
    {
        foreach (var f in archivos)
        {
            var m = FechaDelArchivo().Match(Path.GetFileName(f));
            if (!m.Success) continue;
            if (DateOnly.TryParseExact(m.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < conservar)
                yield return f;
        }
    }

    [GeneratedRegex(@"^voices-(\d{8})(_\d+)?\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex FechaDelArchivo();

    private static TimeSpan Hora(string hhmm) => TimeSpan.TryParse(hhmm, CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(23, 0, 0);

    private static TimeZoneInfo FindMexico()
    {
        foreach (var id in new[] { "Central Standard Time (Mexico)", "America/Mexico_City" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.Local;
    }
}
