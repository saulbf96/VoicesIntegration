using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Api.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    [Required, MinLength(32)]
    public string InternalApiKey { get; init; } = string.Empty;

    [Required, MinLength(32)]
    public string WebhookSecret { get; init; } = string.Empty;

    public string[] InternalNetworks { get; init; } = ["127.0.0.0/8", "::1/128", "10.80.0.0/16"];

    // Solo para probar en local que se pida la API Key (perfil "http-pedir-key")
    public bool SimularFueraDeRed { get; init; }
}
