using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Infrastructure.Blaster;

/// <summary>
/// Configuración de Blaster (sección "Blaster" del appsettings).
/// La ApiKey NUNCA va en appsettings.json: va en user-secrets (local)
/// o en variables de entorno / bóveda de secretos (servidor).
/// </summary>
public sealed class BlasterOptions
{
    public const string SectionName = "Blaster";

    [Required, Url]
    public string BaseUrl { get; init; } = "https://connect.chatbridge.mx";

    [Required, MinLength(20)]
    public string ApiKey { get; init; } = string.Empty;

    [Range(5, 120)]
    public int TimeoutSeconds { get; init; } = 30;
}
