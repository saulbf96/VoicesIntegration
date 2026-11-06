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

/// <summary>Sección "Alertas": cuándo avisar por correo.</summary>
public sealed class AlertasOptions
{
    public const string Seccion = "Alertas";

    /// <summary>Horario en que normalmente corren campañas (hora de México). Fuera de él no se avisa "no llegan datos".</summary>
    public string HorarioInicio { get; init; } = "07:00";
    public string HorarioFin { get; init; } = "22:00";

    /// <summary>Si a esta hora (México) no ha llegado NINGÚN aviso en el día, avisar (¿webhook apagado? ¿sin campañas?).</summary>
    public string HoraSinInicio { get; init; } = "08:30";

    /// <summary>Minutos sin avisos (dentro del horario y después del primero del día) para avisar "no llegan datos".</summary>
    public int MinutosSinDatos { get; init; } = 30;

    /// <summary>Minutos con avisos esperando en la bandeja (BD sin responder) para avisar.</summary>
    public int MinutosBdSinResponder { get; init; } = 5;

    /// <summary>Corte de la API (sin registro de disponibilidad) que se avisa al volver a arrancar, en minutos, dentro del horario.</summary>
    public int MinutosCorteApi { get; init; } = 3;

    /// <summary>Hora del resumen diario (hora de México).</summary>
    public string HoraResumen { get; init; } = "22:15";

    /// <summary>Dirección de la pantalla de recuperación para ponerla en los correos (opcional).</summary>
    public string UrlRecuperar { get; init; } = string.Empty;
}

/// <summary>
/// Vigilante: revisa cada minuto y manda correo cuando:
/// - empieza a recibir datos en el día;
/// - dejan de llegar datos dentro del horario (y cuando vuelven, con el rango a recuperar);
/// - la API estuvo apagada (se avisa al volver a arrancar, con el rango exacto según los registros de disponibilidad);
/// - la BD no responde (avisos acumulándose en la bandeja; no se pierden) y cuando se recupera;
/// - hay avisos rechazados nuevos o poco disco;
/// - resumen del día a la hora configurada, con los rangos a subir en /recuperar.
/// Cada incidente se avisa UNA vez (y otra al resolverse), para no llenar el correo.
/// </summary>
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
    private DateTime? _sinDatosDesde;                 // hora MX del último aviso antes del hueco (incidente abierto)
    private readonly List<(DateTime Desde, DateTime Hasta)> _huecosSinDatos = [];
    private DateTime? _bdDesde;                       // incidente BD abierto
    private int _rechazadosAvisados;
    private DateTime _ultimoAvisoRechazo = DateTime.MinValue;
    private DateTime _arranqueMx;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _arranqueMx = NowMx();
        _dia = DateOnly.FromDateTime(_arranqueMx);
        _rechazadosAvisados = SafeStatus()?.Rejected ?? 0;   // no avisar rechazos viejos
        _resumenEnviado = _arranqueMx.TimeOfDay >= Hora(options.Value.HoraResumen);

        // Dar 2 minutos para que el registro de disponibilidad se registre y el reporte de huecos refleje el corte recién terminado.
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

    /// <summary>Al arrancar: ¿la API estuvo apagada justo antes? Y ¿ya había avisos hoy (para no repetir "empezó")?</summary>
    private async Task RevisarArranqueAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMonitoringRepository>();
            var r = await repo.GetGapReportAsync(_dia, ct);
            _inicioAvisado = r.Horas.Sum(h => h.Avisos_Blaster + h.Avisos_Sms) > 0;

            // El corte que terminó cuando arrancamos (el último que acaba cerca del arranque), recortado al horario.
            var corte = r.VentanasSinServicio
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

        // 1) Empezó a recibir datos hoy.
        if (!_inicioAvisado && ultimoHoy is not null)
        {
            _inicioAvisado = true;
            var primero = tracker.FirstUtc is { } f ? ToMx(f) : ultimoHoy.Value;
            if (DateOnly.FromDateTime(primero) != hoy) primero = ultimoHoy.Value;
            await notificador.EnviarAsync($"Empezó a recibir datos ({primero:HH:mm})",
                $"La API de Voices ({Environment.MachineName}) recibió el primer aviso del {hoy:dd/MM/yyyy} a las {primero:HH:mm:ss}.\n" +
                "Todo en orden: los resultados de las llamadas se están guardando.", ct);
        }

        // 1b) Ya es tarde y en todo el día no ha llegado nada (una vez al día).
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

        // 2) Dejaron de llegar datos (dentro del horario) / volvieron a llegar.
        var referencia = ultimoHoy ?? (_inicioAvisado ? _arranqueMx : (DateTime?)null);   // tras reiniciar, contar desde el arranque
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

        // 3) BD sin responder: avisos esperando en la bandeja (no se pierden).
        var st = SafeStatus();
        if (st is not null)
        {
            var viejo = st.OldestPendingUtc is { } op ? (time.GetUtcNow() - op).TotalMinutes : 0;
            if (_bdDesde is null && st.Pending > 0 && viejo >= o.MinutosBdSinResponder)
            {
                _bdDesde = ahora.AddMinutes(-viejo);
                await notificador.EnviarAsync("La base de datos NO responde — avisos en espera",
                    $"Hay {st.Pending:N0} avisos esperando en la bandeja desde hace {(int)viejo} min (desde las {_bdDesde:HH:mm}).\n" +
                    "NO se pierden: están guardados en disco y se guardan solos cuando la BD regrese.\n" +
                    "Revisa la conexión al SQL Server / la base de datos. Te aviso cuando se recupere.", ct);
            }
            else if (_bdDesde is { } bd && st.Pending == 0)
            {
                _bdDesde = null;
                await notificador.EnviarAsync("La base de datos se recuperó",
                    $"La BD volvió a responder. Los avisos en espera desde las {bd:HH:mm} ya se guardaron. No hay que recuperar nada.", ct);
            }

            // 4) Avisos rechazados nuevos (máximo un correo por hora).
            if (st.Rejected > _rechazadosAvisados && (ahora - _ultimoAvisoRechazo).TotalMinutes >= 60)
            {
                var nuevos = st.Rejected - _rechazadosAvisados;
                _rechazadosAvisados = st.Rejected;
                _ultimoAvisoRechazo = ahora;
                await notificador.EnviarAsync($"{nuevos:N0} avisos rechazados",
                    $"Llegaron {nuevos:N0} avisos que no se pudieron guardar (formato inválido). Total en espera de revisión: {st.Rejected:N0}.\n" +
                    $"Están en {inboxOptions.Value.Path}\\rejected, cada uno con su archivo .motivo.txt.", ct);
            }

            // 5) Poco disco (una vez al día).
            if (!_discoAvisado && st.FreeDiskMb is { } mb && mb < inboxOptions.Value.MinFreeDiskMb)
            {
                _discoAvisado = true;
                await notificador.EnviarAsync("Poco espacio en disco",
                    $"Quedan {mb:N0} MB libres en el disco de la bandeja ({inboxOptions.Value.Path}) en {Environment.MachineName}. " +
                    "Si se llena, la API ya no podrá guardar avisos. Libera espacio.", ct);
            }
        }

        // 6) Resumen del día.
        if (!_resumenEnviado && ahora.TimeOfDay >= Hora(o.HoraResumen))
        {
            _resumenEnviado = true;
            await EnviarResumenAsync(hoy, ahora, ct);
        }
    }

    private async Task EnviarResumenAsync(DateOnly dia, DateTime ahora, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMonitoringRepository>();
        var r = await repo.GetGapReportAsync(dia, ct);
        var blaster = r.Horas.Sum(h => h.Avisos_Blaster);
        var sms = r.Horas.Sum(h => h.Avisos_Sms);
        var conAvisos = r.Horas.Where(h => h.Avisos_Blaster + h.Avisos_Sms > 0).Select(h => h.Hora).ToList();

        var cortes = r.VentanasSinServicio.Select(v => Recortar(v.Desde, v.Hasta)).Where(v => v is not null).Select(v => v!.Value)
            .Where(v => (v.Hasta - v.Desde).TotalMinutes >= options.Value.MinutosCorteApi).ToList();
        var huecos = _huecosSinDatos.ToList();
        if (_sinDatosDesde is { } abierto) huecos.Add((abierto, ahora));

        var sb = new StringBuilder();
        sb.AppendLine($"Resumen del {dia:dd/MM/yyyy} — API de Voices en {Environment.MachineName} (corte a las {ahora:HH:mm}).");
        sb.AppendLine();
        sb.AppendLine($"Avisos recibidos: Blaster {blaster:N0} · SMS {sms:N0}");
        // "Hora" viene como "07:00 - 07:59"
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

        await notificador.EnviarAsync(cortes.Count == 0 && huecos.Count == 0
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

    /// <summary>Recorta un corte al horario de campañas del mismo día; null si queda fuera.</summary>
    private (DateTime Desde, DateTime Hasta)? Recortar(DateTime desde, DateTime hasta)
    {
        var ini = desde.Date + Hora(options.Value.HorarioInicio);
        var fin = desde.Date + Hora(options.Value.HorarioFin);
        var d = desde < ini ? ini : desde;
        var h = hasta > fin ? fin : hasta;
        return h > d ? (d, h) : null;
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
