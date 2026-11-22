using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using VoicesIntegration.Application.Abstractions.Notifications;

namespace VoicesIntegration.Infrastructure.Notificaciones;

public sealed class CorreoOptions
{
    public const string Seccion = "Correo";

    public bool Habilitado { get; init; }
    public string Servidor { get; init; } = string.Empty;
    public int Puerto { get; init; } = 587;

    public string Seguridad { get; init; } = "StartTls";

    public string Usuario { get; init; } = string.Empty;
    public string Contrasena { get; init; } = string.Empty;
    public string De { get; init; } = string.Empty;
    public string[] Para { get; init; } = [];
}

// Si el correo falla solo queda en el log
internal sealed class NotificadorCorreo(IOptions<CorreoOptions> options, ILogger<NotificadorCorreo> logger) : INotificador
{
    public async Task EnviarAsync(string asunto, string cuerpo, CancellationToken cancellationToken)
    {
        if (!options.Value.Habilitado)
        {
            logger.LogInformation("Aviso (correo deshabilitado): {Asunto}", asunto);
            return;
        }

        try
        {
            await EnviarOFallarAsync(asunto, cuerpo, cancellationToken);
            logger.LogInformation("Correo enviado: {Asunto}", asunto);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "No se pudo mandar el correo de aviso: {Asunto}", asunto);
        }
    }

    public async Task<string?> ProbarAsync(CancellationToken cancellationToken)
    {
        var correo = options.Value;
        if (!correo.Habilitado) return "El correo está deshabilitado (Correo:Habilitado = false).";

        try
        {
            await EnviarOFallarAsync("Correo de prueba",
                $"Si lees esto, los avisos de Voices funcionan.\nServidor: {Environment.MachineName}\n" +
                $"Enviado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}\nSMTP: {correo.Servidor}:{correo.Puerto} ({correo.Seguridad})",
                cancellationToken);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Falló el correo de prueba");
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task EnviarOFallarAsync(string asunto, string cuerpo, CancellationToken cancellationToken)
    {
        var correo = options.Value;
        var mensaje = new MimeMessage();
        mensaje.From.Add(MailboxAddress.Parse(correo.De));
        foreach (var para in correo.Para) mensaje.To.Add(MailboxAddress.Parse(para));
        mensaje.Subject = $"[Voices] {asunto}";
        mensaje.Body = new TextPart("plain") { Text = cuerpo };

        using var smtp = new SmtpClient { Timeout = 30_000 };
        var seguridad = correo.Seguridad switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "Ninguna" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await smtp.ConnectAsync(correo.Servidor, correo.Puerto, seguridad, cancellationToken);
        if (!string.IsNullOrEmpty(correo.Usuario))
            await smtp.AuthenticateAsync(correo.Usuario, correo.Contrasena, cancellationToken);
        await smtp.SendAsync(mensaje, cancellationToken);
        await smtp.DisconnectAsync(true, cancellationToken);
    }
}
