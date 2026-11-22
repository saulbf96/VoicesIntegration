using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Recovery;

namespace VoicesIntegration.Application.Features.Recovery;

// No se recupera el mismo dia: el webhook todavia esta guardando
public static class RecoveryDayRule
{
    private static readonly TimeZoneInfo Mexico = FindMexico();

    public static string? WhyNot(RecoveryJob job, InboxStatus inbox, DateTimeOffset utcNow)
    {
        if (job.Hasta is not { } hasta) return null;

        var hoyMx = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, Mexico).DateTime);
        var diaArchivo = DateOnly.FromDateTime(hasta);

        if (diaArchivo >= hoyMx)
            return $"No se puede recuperar el MISMO día ({hoyMx:dd/MM/yyyy}): el archivo trae llamadas de hoy " +
                   $"(hasta {hasta:dd/MM/yyyy HH:mm}) y el webhook las sigue guardando. Súbelo a partir de mañana.";

        if (inbox.Pending > 0 && inbox.OldestPendingUtc is { } oldestUtc)
        {
            var diaPendiente = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(oldestUtc, Mexico).DateTime);
            if (diaPendiente <= diaArchivo)
                return $"Todavía hay {inbox.Pending:N0} avisos del webhook sin guardar recibidos el {diaPendiente:dd/MM/yyyy} " +
                       $"(el archivo es del {diaArchivo:dd/MM/yyyy}). Espera a que la bandeja termine ese día y vuelve a intentar.";
        }

        return null;
    }

    private static TimeZoneInfo FindMexico()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"); }
    }
}
