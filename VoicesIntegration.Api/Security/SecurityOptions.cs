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
}
