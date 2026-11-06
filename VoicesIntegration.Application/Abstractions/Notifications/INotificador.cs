namespace VoicesIntegration.Application.Abstractions.Notifications;

/// <summary>
/// Manda avisos (por correo) al equipo: inicio de recepción, huecos, fallas y resumen del día.
/// Si el envío falla NUNCA tumba la API: solo queda en el log.
/// </summary>
public interface INotificador
{
    Task EnviarAsync(string asunto, string cuerpo, CancellationToken cancellationToken);

    /// <summary>Manda un correo de prueba. null = salió bien; si no, el motivo (sin contraseñas).</summary>
    Task<string?> ProbarAsync(CancellationToken cancellationToken);
}
