using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Sms;
using VoicesIntegration.Application.Common.Exceptions;

namespace VoicesIntegration.Infrastructure.Sms;

// Cache del detalle por message_id para no saturar la API de SMS
internal sealed class SmsGatewayState(IMemoryCache cache)
{
    private long _pausedUntilTicks;
    public IMemoryCache Cache { get; } = cache;
    public bool IsPaused(DateTimeOffset now) => now.UtcTicks < Interlocked.Read(ref _pausedUntilTicks);
    public void PauseUntil(DateTimeOffset until) => Interlocked.Exchange(ref _pausedUntilTicks, until.UtcTicks);
}

internal sealed class SmsHttpGateway(
    HttpClient httpClient,
    IOptions<SmsOptions> options,
    SmsGatewayState state,
    TimeProvider time,
    ILogger<SmsHttpGateway> logger) : ISmsGateway
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PauseOn429 = TimeSpan.FromSeconds(60);

    public async Task<SmsMessageDetail?> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ApiKey))
            return null;

        var key = "sms:" + messageId;
        if (state.Cache.TryGetValue(key, out SmsMessageDetail? cached))
            return cached;

        if (state.IsPaused(time.GetUtcNow()))
            return null;

        using var response = await httpClient.GetAsync($"v1/sms/messages/{Uri.EscapeDataString(messageId)}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            state.PauseUntil(time.GetUtcNow() + PauseOn429);
            logger.LogWarning("Voices respondió 429 (límite de la App-Key de SMS): se pausan las consultas de detalle {Seconds} s", (int)PauseOn429.TotalSeconds);
            return null;
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            logger.LogDebug("SMS {MessageId} no encontrado en Voices ({Status})", messageId, (int)response.StatusCode);
            return null;
        }

        if (!response.IsSuccessStatusCode)
            throw new ExternalServiceException("SMS", $"Voices respondió {(int)response.StatusCode} al consultar el SMS.");

        var r = await response.Content.ReadFromJsonAsync<SmsMessageResponse>(cancellationToken);
        var detail = r is null ? null : new SmsMessageDetail(r.To, r.Message, r.Operator, r.CreatedAt, r.SentAt, r.FailedAt);
        if (detail is not null)
            state.Cache.Set(key, detail, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheFor, Size = 1 });
        return detail;
    }

    private sealed record SmsMessageResponse(
        [property: JsonPropertyName("to")] string? To,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("operator")] string? Operator,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("sent_at")] DateTimeOffset? SentAt,
        [property: JsonPropertyName("failed_at")] DateTimeOffset? FailedAt);
}
