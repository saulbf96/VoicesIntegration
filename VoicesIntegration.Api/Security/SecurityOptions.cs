using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Api.Security;

/// <summary>
/// Secretos de NUESTRA API (sección "Security"). Van en user-secrets / variables de entorno.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Llave para que tu equipo consulte nuestra API (header X-Api-Key).</summary>
    [Required, MinLength(32)]
    public string InternalApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Secreto que Voices manda en cada webhook (header X-Webhook-Secret).
    /// Se le pide a Voices que lo configure como header fijo en la App-Key.
    /// </summary>
    [Required, MinLength(32)]
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>
    /// Redes desde donde la pantalla /recuperar/ funciona SIN pedir API Key (formato CIDR).
    /// Por defecto: la misma PC y la red interna 10.80.x.x. Lo que llegue por el túnel o de internet sí la pide.
    /// </summary>
    public string[] InternalNetworks { get; init; } = ["127.0.0.0/8", "::1/128", "10.80.0.0/16"];
}
