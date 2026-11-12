using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Monitoring;
using VoicesIntegration.Application.Abstractions.Notifications;
using VoicesIntegration.Infrastructure.Inbox;

namespace VoicesIntegration.Infrastructure.Monitoring;

public sealed class AlertasOptions
{
    public const string Seccion = "Alertas";

    public string HorarioInicio { get; init; } = "07:00";
    public string HorarioFin { get; init; } = "22:00";

    public string HoraSinInicio { get; init; } = "08:30";

    public int MinutosSinDatos { get; init; } = 30;

    public int MinutosBdSinResponder { get; init; } = 5;

    public int MinutosCorteApi { get; init; } = 3;

    public string HoraResumen { get; init; } = "22:15";

    public string UrlRecuperar { get; init; } = string.Empty;
}

// Revisa cada minuto y manda correo: inicio, cortes, atrasos y resumen
internal sealed class AlertasWorker(
    IServiceScopeFactory scopeFactory,
    IWebhookInbox inbox,
    ReceptionTracker tracker,
    INotificador notificador,
    IOptions<AlertasOptions> options,
    IOptions<InboxOptions> inboxOptions,
    TimeProvider time,
    ILogger<AlertasWorker> logger) : BackgroundService
{
    private static readonly TimeZoneInfo Mexico = FindMexico();

    private DateOnly _dia;
    private bool _inicioAvisado, _resumenEnviado, _discoAvisado, _sinInicioAvisado;
    private DateTime? _sinDatosDesde;
    private readonly List<(DateTime Desde, DateTime Hasta)> _huecosSinDatos = [];
    private DateOnly? _resumenDe;
    private List<(DateTime Desde, DateTime Hasta)> _huecosResumen = [];
    private DateTime? _bdDesde;
    private int _rechazadosAvisados;
    private DateTime _ultimoAvisoRechazo = DateTime.MinValue;
    private DateTime _arranqueMx;
    private readonly Queue<(DateTime Mx, long Guardados, long Recibidos)> _muestras = new();
    private DateTime _ultimoAvisoBandeja = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _arranqueMx = NowMx();
        _dia = DateOnly.FromDateTime(_arranqueMx);
        _rechazadosAvisados = SafeStatus()?.Rejected ?? 0;
        _resumenEnviado = _arranqueMx.TimeOfDay >= Hora(options.Value.HoraResumen);

        try { await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken); } catch (OperationCanceledException) { return; }
        await RevisarArranqueAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try { await RevisarAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Vigilante de alertas: {Error}", ex.Message); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RevisarArranqueAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMonitoringRepository>();
            var r = await repo.GetGapReportAsync(_dia, ct);
            _inicioAvisado = r.Horas.Sum(h => h.Avisos_Blaster + h.Avisos_Sms) > 0;

            var corte = r.VentanasSinServicio
                .Where(v => v.Tipo != DowntimeWindow.TipoNoLlegaron)
                .Where(v => v.Hasta >= _arranqueMx.AddMinutes(-10) && v.Desde <= _arranqueMx)
                .Select(v => Recortar(v.Desde, v.Hasta))
                .FirstOrDefault(v => v is not null);
            if (corte is { } c && (c.Hasta - c.Desde).TotalMinutes >= options.Value.MinutosCorteApi)
            {
                await notificador.EnviarAsync($"La API estuvo APAGADA de {c.Desde:HH:mm} a {c.Hasta:HH:mm}",
                    $"La API de Voices volvió a arrancar en {Environment.MachineName} a las {_arranqueMx:HH:mm}.\n\n" +
                    $"Estuvo sin servicio de {c.Desde:HH:mm} a {c.Hasta:HH:mm} ({(int)(c.Hasta - c.Desde).TotalMinutes} min) del {_dia:dd/MM/yyyy}.\n" +
                    "Los avisos que Voices mandó en ese rango NO llegaron.\n\n" + Accion(c.Desde, c.Hasta), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("No se pudo revisar el corte al arrancar: {Error}", ex.Message);
        }
    }

    private async Task RevisarAsync(CancellationToken ct)
    {
        var o = options.Value;
        var ahora = NowMx();
        var hoy = DateOnly.FromDateTime(ahora);
        if (hoy != _dia) NuevoDia(hoy);

        var dentroHorario = ahora.TimeOfDay >= Hora(o.HorarioInicio) && ahora.TimeOfDay <= Hora(o.HorarioFin);
        var ultimo = tracker.LastUtc is { } u ? ToMx(u) : (DateTime?)null;
        var ultimoHoy = ultimo is { } x && DateOnly.FromDateTime(x) == hoy ? ultimo : null;

        if (!_inicioAvisado && ultimoHoy is not null)
        {
            _inicioAvisado = true;
            var primero = tracker.FirstUtc is { } f ? ToMx(f) : ultimoHoy.Value;
            if (DateOnly.FromDateTime(primero) != hoy) primero = ultimoHoy.Value;
            await notificador.EnviarAsync($"Empezó a recibir datos ({primero:HH:mm})",
                $"La API de Voices ({Environment.MachineName}) recibió el primer aviso del {hoy:dd/MM/yyyy} a las {primero:HH:mm:ss}.\n" +
                "Todo en orden: los resultados de las llamadas se están guardando.", ct);
        }

        if (!_inicioAvisado && !_sinInicioAvisado && dentroHorario && ahora.TimeOfDay >= Hora(o.HoraSinInicio))
        {
            _sinInicioAvisado = true;
            await notificador.EnviarAsync($"Son las {ahora:HH:mm} y NO ha llegado ningún dato hoy",
                $"La API de Voices ({Environment.MachineName}) está funcionando, pero hoy {hoy:dd/MM/yyyy} no ha recibido ningún aviso de Voices.\n\n" +
                "Revisa:\n" +
                "1) En el backoffice de Voices → Webhooks: que el webhook de Blaster esté ACTIVO y apunte a la URL correcta.\n" +
                "2) Que haya campañas corriendo hoy.\n" +
                "3) Que la URL pública / túnel esté encendido.\n\n" +
                "En cuanto llegue el primer aviso te mando \"Empezó a recibir datos\".", ct);
        }

        var referencia = ultimoHoy ?? (_inicioAvisado ? _arranqueMx : (DateTime?)null);
        if (_sinDatosDesde is null && dentroHorario && referencia is { } refMx && _inicioAvisado
            && (ahora - refMx).TotalMinutes >= o.MinutosSinDatos)
        {
            _sinDatosDesde = refMx;
            await notificador.EnviarAsync($"No llegan datos desde las {refMx:HH:mm}",
                $"No ha llegado ningún aviso de Voices desde las {refMx:HH:mm} ({(int)(ahora - refMx).TotalMinutes} min).\n\n" +
                "Puede ser normal (terminaron o se pausaron las campañas) o una falla (Voices no puede llegar a la API: túnel, red, IIS).\n" +
                "Revisa en el backoffice de Voices si hay campañas corriendo. Te aviso cuando vuelvan a llegar.", ct);
        }
        else if (_sinDatosDesde is { } desde && ultimoHoy is { } volvio && volvio > desde)
        {
            _huecosSinDatos.Add((desde, volvio));
            _sinDatosDesde = null;
            await notificador.EnviarAsync($"Volvieron a llegar datos ({volvio:HH:mm}) — hueco de {desde:HH:mm} a {volvio:HH:mm}",
                $"Volvieron a llegar avisos de Voices a las {volvio:HH:mm}.\n" +
                $"No llegó nada de {desde:HH:mm} a {volvio:HH:mm} ({(int)(volvio - desde).TotalMinutes} min).\n\n" +
                "Si en ese rango SÍ había campañas corriendo, faltan llamadas:\n" + Accion(desde, volvio), ct);
        }

        var st = SafeStatus();
        if (st is not null)
        {
            _muestras.Enqueue((ahora, tracker.SavedTotal, tracker.Count));
            while (_muestras.Count > 11) _muestras.Dequeue();
            var (guardanMin, lleganMin, minutos) = Ritmo();

            var viejo = st.OldestPendingUtc is { } op ? (time.GetUtcNow() - op).TotalMinutes : 0;
            var ultimoGuardado = tracker.LastSavedUtc is { } ls ? ToMx(ls) : (DateTime?)null;
            var sinGuardar = (ahora - (ultimoGuardado ?? _arranqueMx)).TotalMinutes;
            var bdParada = st.Pending > 0 && sinGuardar >= o.MinutosBdSinResponder;
            var abrir = _bdDesde is null && st.Pending > 0 && viejo >= o.MinutosBdSinResponder;
            var recordar = _bdDesde is not null && st.Pending > 0 && (ahora - _ultimoAvisoBandeja).TotalMinutes >= 60;
            if (abrir || recordar)
            {
                _bdDesde ??= ahora.AddMinutes(-viejo);
                _ultimoAvisoBandeja = ahora;

                var sb = new StringBuilder();
                sb.AppendLine($"Pendientes en la bandeja: {st.Pending:N0}");
                if (st.OldestPendingUtc is { } op2) sb.AppendLine($"El más viejo llegó a las {ToMx(op2):HH:mm} (hace {(int)viejo} min).");
                if (minutos > 0) sb.AppendLine($"Últimos {minutos} min: llegan {lleganMin:N0} por minuto y se guardan {guardanMin:N0} por minuto.");
                sb.AppendLine();
                if (bdParada)
                    sb.AppendLine(ultimoGuardado is { } ug
                        ? $"La base de datos no guarda nada desde las {ug:HH:mm} ({(int)sinGuardar} min). Revisa el SQL Server (10.80.5.205)."
                        : "La base de datos no ha guardado nada desde que arrancó la API. Revisa el SQL Server (10.80.5.205).");
                else if (minutos > 0 && guardanMin > lleganMin)
                    sb.AppendLine($"La BD sí guarda. A este ritmo se pone al día en unos {Math.Ceiling(st.Pending / (double)(guardanMin - lleganMin)):N0} min.");
                else if (minutos > 0)
                    sb.AppendLine("La BD sí guarda, pero llegan más avisos de los que se guardan: la bandeja sigue creciendo.");
                if (tracker.LastError is { } err && tracker.LastErrorUtc is { } eu && (time.GetUtcNow() - eu).TotalMinutes <= 60)
                    sb.AppendLine($"Último error de la BD ({ToMx(eu):HH:mm}): {Corto(err)}");
                sb.AppendLine();
                sb.AppendLine("No se pierde nada: los avisos están en el disco del servidor y se guardan solos.");
                sb.AppendLine(recordar ? "Recordatorio: el problema sigue. Te aviso cada hora hasta que se resuelva." : "Te aviso cuando se resuelva.");

                await notificador.EnviarAsync(bdParada
                        ? $"La BD no guarda desde las {(ultimoGuardado ?? _arranqueMx):HH:mm}: {st.Pending:N0} pendientes"
                        : minutos > 0
                            ? $"Avisos atrasados: {st.Pending:N0} pendientes (se guardan {guardanMin:N0} por minuto)"
                            : $"Avisos atrasados: {st.Pending:N0} pendientes", sb.ToString(), ct);
            }
            else if (_bdDesde is { } bd && st.Pending == 0)
            {
                _bdDesde = null;
                await notificador.EnviarAsync("Bandeja al día: ya se guardó todo",
                    $"Los avisos en espera desde las {bd:HH:mm} ya se guardaron en la base de datos. No se perdió nada y no hay que recuperar nada.", ct);
            }

            // Si se reprocesaron o borraron rechazados, se cuenta desde lo que hay ahora
            if (st.Rejected < _rechazadosAvisados) _rechazadosAvisados = st.Rejected;
            if (st.Rejected > _rechazadosAvisados && (ahora - _ultimoAvisoRechazo).TotalMinutes >= 60)
            {
                var nuevos = st.Rejected - _rechazadosAvisados;
                _rechazadosAvisados = st.Rejected;
                _ultimoAvisoRechazo = ahora;
                var carpeta = Path.Combine(inboxOptions.Value.Path, "rejected");
                await notificador.EnviarAsync($"{nuevos:N0} avisos rechazados",
                    $"Llegaron {nuevos:N0} avisos que no se pudieron guardar. Total por revisar: {st.Rejected:N0}.\n\n" +
                    MotivosRechazo(carpeta) +
                    $"\nEstán en {carpeta}, cada uno con su archivo .motivo.txt.", ct);
            }

            if (!_discoAvisado && st.FreeDiskMb is { } mb && mb < inboxOptions.Value.MinFreeDiskMb)
            {
                _discoAvisado = true;
                await notificador.EnviarAsync("Poco espacio en disco",
                    $"Quedan {mb:N0} MB libres en el disco de la bandeja ({inboxOptions.Value.Path}) en {Environment.MachineName}. " +
                    "Si se llena, la API ya no podrá guardar avisos. Libera espacio.", ct);
            }
        }

        // Despues de HoraResumen se espera a que la bandeja quede en 0 (limite: inicio del horario del dia siguiente)
        if (!_resumenEnviado && ahora.TimeOfDay >= Hora(o.HoraResumen))
        {
            _resumenEnviado = true;
            _resumenDe = hoy;
            _huecosResumen = _huecosSinDatos.ToList();
            if (_sinDatosDesde is { } abierto) _huecosResumen.Add((abierto, ahora));
        }
        if (_resumenDe is { } dr)
        {
            var limite = dr.AddDays(1).ToDateTime(TimeOnly.FromTimeSpan(Hora(o.HorarioInicio)));
            if (st is not null && (st.Pending == 0 || ahora >= limite))
            {
                _resumenDe = null;
                await EnviarResumenAsync(dr, ahora, _huecosResumen, st.Pending, ct);
            }
        }
    }

    private async Task EnviarResumenAsync(DateOnly dia, DateTime ahora, List<(DateTime Desde, DateTime Hasta)> huecos, int pendientes, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMonitoringRepository>();
        var r = await repo.GetGapReportAsync(dia, ct);
        var blaster = r.Horas.Sum(h => h.Avisos_Blaster);
        var sms = r.Horas.Sum(h => h.Avisos_Sms);
        var conAvisos = r.Horas.Where(h => h.Avisos_Blaster + h.Avisos_Sms > 0).Select(h => h.Hora).ToList();

        var cortes = r.VentanasSinServicio.Select(v => Recortar(v.Desde, v.Hasta)).Where(v => v is not null).Select(v => v!.Value)
            .Where(v => (v.Hasta - v.Desde).TotalMinutes >= options.Value.MinutosCorteApi).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Resumen del {dia:dd/MM/yyyy} — API de Voices en {Environment.MachineName} (corte a las {ahora:HH:mm}).");
        sb.AppendLine(pendientes == 0
            ? "Se mandó al terminar de guardar todo lo de la bandeja."
            : $"Quedaron {pendientes:N0} avisos sin guardar: este resumen está incompleto.");
        sb.AppendLine();
        sb.AppendLine($"Avisos recibidos: Blaster {blaster:N0} · SMS {sms:N0}");
        if (conAvisos.Count > 0)
            sb.AppendLine($"Llegaron datos entre las {conAvisos.First().Split(' ')[0]} y las {conAvisos.Last().Split(' ')[^1]}.");
        var st = SafeStatus();
        if (st is not null) sb.AppendLine($"Bandeja: {st.Pending:N0} en espera · {st.Rejected:N0} rechazados");
        sb.AppendLine();

        if (cortes.Count == 0 && huecos.Count == 0)
        {
            sb.AppendLine("Sin cortes: la API estuvo viva todo el horario y no hubo huecos de datos. No hay que recuperar nada.");
        }
        else
        {
            if (cortes.Count > 0)
            {
                sb.AppendLine("La API estuvo APAGADA (seguro faltan llamadas de estos rangos):");
                foreach (var c in cortes) sb.AppendLine($"   - de {c.Desde:HH:mm} a {c.Hasta:HH:mm} ({(int)(c.Hasta - c.Desde).TotalMinutes} min)");
            }
            if (huecos.Count > 0)
            {
                sb.AppendLine("No llegaron datos (revisar si había campañas corriendo):");
                foreach (var h in huecos) sb.AppendLine($"   - de {h.Desde:HH:mm} a {h.Hasta:HH:mm} ({(int)(h.Hasta - h.Desde).TotalMinutes} min)");
            }
            sb.AppendLine();
            sb.AppendLine(Accion(null, null));
        }

        await notificador.EnviarAsync(pendientes > 0
            ? $"Resumen del día {dia:dd/MM}: INCOMPLETO ({pendientes:N0} avisos sin guardar)"
            : cortes.Count == 0 && huecos.Count == 0
            ? $"Resumen del día {dia:dd/MM}: sin cortes"
            : $"Resumen del día {dia:dd/MM} — HAY QUE RECUPERAR {cortes.Count + huecos.Count} rango(s)", sb.ToString(), ct);
    }

    private string Accion(DateTime? desde, DateTime? hasta)
    {
        var url = string.IsNullOrWhiteSpace(options.Value.UrlRecuperar) ? "la pantalla /recuperar/ de la API" : options.Value.UrlRecuperar;
        var dia = desde is null ? "de ese día" : $"del día {desde:dd/MM/yyyy}";
        var rango = desde is null ? "esos rangos" : $"{desde:HH:mm} a {hasta:HH:mm}";
        return "QUÉ HACER:\n" +
               $"1) En Voices descarga el reporte de campañas {dia} (el día completo).\n" +
               $"2) Súbelo en {url} → Subir y revisar → Insertar faltantes.\n" +
               $"   Solo se insertan las llamadas que faltan (las de {rango}); lo que ya está no se duplica.";
    }

    private void NuevoDia(DateOnly hoy)
    {
        _dia = hoy;
        _inicioAvisado = _resumenEnviado = _discoAvisado = _sinInicioAvisado = false;
        _sinDatosDesde = null;
        _huecosSinDatos.Clear();
    }

    // Recorta el corte al horario de campanas
    private (DateTime Desde, DateTime Hasta)? Recortar(DateTime desde, DateTime hasta)
    {
        var ini = desde.Date + Hora(options.Value.HorarioInicio);
        var fin = desde.Date + Hora(options.Value.HorarioFin);
        var d = desde < ini ? ini : desde;
        var h = hasta > fin ? fin : hasta;
        return h > d ? (d, h) : null;
    }

    // Promedio por minuto en las ultimas muestras (hasta 10 min)
    private (long Guardan, long Llegan, int Minutos) Ritmo()
    {
        if (_muestras.Count < 2) return (0, 0, 0);
        var a = _muestras.Peek();
        var b = _muestras.Last();
        var min = (b.Mx - a.Mx).TotalMinutes;
        if (min < 1) return (0, 0, 0);
        return ((long)((b.Guardados - a.Guardados) / min), (long)((b.Recibidos - a.Recibidos) / min), (int)Math.Round(min));
    }

    // Motivos de los rechazados agrupados (los 5 mas comunes)
    private static string MotivosRechazo(string carpeta)
    {
        try
        {
            var grupos = Directory.EnumerateFiles(carpeta, "*.motivo.txt")
                .Select(f => Corto(File.ReadAllText(f)))
                .GroupBy(m => m)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .ToList();
            if (grupos.Count == 0) return string.Empty;
            var sb = new StringBuilder("Motivos:\n");
            foreach (var g in grupos) sb.AppendLine($"   - {g.Count():N0} x {g.Key}");
            return sb.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Corto(string texto)
    {
        var linea = texto.Trim().Split('\n')[0].Trim();
        return linea.Length > 200 ? linea[..200] + "..." : linea;
    }

    private InboxStatus? SafeStatus()
    {
        try { return inbox.GetStatus(); } catch (Exception) { return null; }
    }

    private static TimeSpan Hora(string hhmm) => TimeSpan.TryParse(hhmm, out var t) ? t : TimeSpan.Zero;
    private DateTime NowMx() => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Mexico).DateTime;
    private static DateTime ToMx(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Mexico).DateTime;

    private static TimeZoneInfo FindMexico()
    {
        foreach (var id in new[] { "Central Standard Time (Mexico)", "America/Mexico_City" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.CreateCustomTimeZone("Mexico", TimeSpan.FromHours(-6), "Mexico", "Mexico");
    }
}
