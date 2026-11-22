using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Infrastructure.Sms;

public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    [Required, Url]
    public string BaseUrl { get; init; } = "https://connect.chatbridge.mx";

    public string ApiKey { get; init; } = string.Empty;

    [Range(2, 60)]
    public int TimeoutSeconds { get; init; } = 10;
}
