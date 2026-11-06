using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Infrastructure.Sms;

/// <summary>
/// Configuración de SMS (sección "Sms"). La ApiKey es la "API heredada" de la App-Key de SMS
/// (distinta a la de Blaster). Va en user-secrets / appsettings del servidor, NUNCA en Git.
/// Si ApiKey está vacía, los SMS se guardan igual pero SIN número ni texto.
/// </summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    [Required, Url]
    public string BaseUrl { get; init; } = "https://connect.chatbridge.mx";

    public string ApiKey { get; init; } = string.Empty;

    [Range(2, 60)]
    public int TimeoutSeconds { get; init; } = 10;
}
