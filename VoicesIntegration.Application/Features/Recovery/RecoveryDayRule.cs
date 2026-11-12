using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Recovery;

namespace VoicesIntegration.Application.Features.Recovery;

/// <summary>
/// REGLA DEL USUARIO (08/11/2026): JAMÁS se recupera el MISMO día. Ese día el webhook sigue guardando (se está
/// "consumiendo") y recuperar encima satura el 205. Solo se insertan archivos de días ANTERIORES y, además,
/// solo cuando la bandeja del webhook ya no tiene avisos pendientes de ese día o de antes.
/// Revisar (comparar) sí se permite siempre: lo que se bloquea es "Insertar faltantes".
/// </summary>
public static class RecoveryDayRule
{
    private static readonly TimeZoneInfo Mexico = FindMexico();

    /// <summary>Devuelve null si se puede insertar; si no, el motivo para mostrarle al usuario.</summary>
    public static string? WhyNot(RecoveryJob job, InboxStatus inbox, DateTimeOffset utcNow)
    {
        // Desde/Hasta vienen del archivo, en hora de México. Sin fechas = no hay nada marcado por Voices.
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
