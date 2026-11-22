using System.Runtime.CompilerServices;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Application.Features.Recovery;

// Archivos de los menus de Voices (hasta 7 dias): omite lo de hoy y rechaza si trae mas de 7 dias
public sealed class RecoveryDateFilter(DateTime corte)
{
    public const int MaxDias = 7;

    private static readonly TimeZoneInfo Mexico = FindMexico();

    private DateTime? _min, _max;

    // Desde esta hora (Mexico) las filas se omiten: el webhook todavia las esta guardando
    public DateTime Corte { get; } = corte;

    public int Omitidas { get; private set; }

    // Motivo del rechazo (el lector puede llegar envuelto en otra excepcion por SqlBulkCopy)
    public string? Rechazo { get; private set; }

    // Hoy 00:00 Mexico; si la bandeja tiene pendientes de un dia anterior, ese dia 00:00
    public static DateTime CalcularCorte(DateTimeOffset utcNow, InboxStatus inbox)
    {
        var hoy = TimeZoneInfo.ConvertTime(utcNow, Mexico).Date;
        if (inbox.Pending > 0 && inbox.OldestPendingUtc is { } viejo)
        {
            var dia = TimeZoneInfo.ConvertTime(viejo, Mexico).Date;
            if (dia < hoy) return dia;
        }
        return hoy;
    }

    public static DateTime? FechaLlamada(RecoveryRow r) => r.DialedAt ?? r.FailedAt;

    public static DateTime? FechaSms(SmsRecoveryRow r) => r.StatusAt ?? r.SentAt ?? r.FailedAt ?? r.CreatedAt;

    // true = se carga. Sin fecha se carga (la comparacion decide).
    public bool Incluir(DateTime? fecha)
    {
        if (fecha is not { } f) return true;
        if (f >= Corte) { Omitidas++; return false; }

        if (_min is null || f < _min) _min = f;
        if (_max is null || f > _max) _max = f;
        if ((_max.Value.Date - _min.Value.Date).Days + 1 > MaxDias)
        {
            Rechazo = $"El archivo trae más de {MaxDias} días (del {_min:dd/MM/yyyy} al {_max:dd/MM/yyyy}). " +
                      $"Descarga de Voices un rango de máximo {MaxDias} días y vuelve a subirlo.";
            throw new DomainException(Rechazo);
        }
        return true;
    }

    public async IAsyncEnumerable<T> Filtrar<T>(IAsyncEnumerable<T> rows, Func<T, DateTime?> fecha, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in rows.WithCancellation(ct))
            if (Incluir(fecha(row))) yield return row;
    }

    public string MotivoCorte(DateTimeOffset utcNow) =>
        Corte >= TimeZoneInfo.ConvertTime(utcNow, Mexico).Date
            ? "de hoy"
            : $"del {Corte:dd/MM/yyyy} en adelante (el webhook todavía tiene avisos sin guardar de ese día)";

    private static TimeZoneInfo FindMexico()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"); }
    }
}
