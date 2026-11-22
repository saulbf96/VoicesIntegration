using VoicesIntegration.Application.Abstractions.Monitoring;
using System.Text;
using System.Text.Json;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Features.Webhooks.CallResult;

namespace VoicesIntegration.Api.Endpoints;

// Webhooks de Voices: se guarda en la bandeja y se responde 200 rapido
public static class WebhookEndpoints
{
    public const string RateLimitPolicy = "webhook";

    private const int MaxBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/blaster/call-result",
                (HttpRequest r, IWebhookInbox i, ReceptionTracker k, TimeProvider t, ILoggerFactory l, CancellationToken c) => ReceiveAsync(WebhookChannels.Blaster, r, i, k, t, l, c))
            .WithTags("Webhooks")
            .WithName("BlasterCallResultWebhook")
            .Accepts<CallResultPayload>("application/json")
            .RequireWebhookSecret()
            .RequireRateLimiting(RateLimitPolicy);

        // Llegan Message status, Batch status y Reply al mismo endpoint
        app.MapPost("/webhooks/sms/message-status",
                (HttpRequest r, IWebhookInbox i, ReceptionTracker k, TimeProvider t, ILoggerFactory l, CancellationToken c) => ReceiveAsync(WebhookChannels.Sms, r, i, k, t, l, c))
            .WithTags("Webhooks")
            .WithName("SmsStatusWebhook")
            .RequireWebhookSecret()
            .RequireRateLimiting(RateLimitPolicy);

        return app;
    }

    private static async Task<IResult> ReceiveAsync(
        string channel,
        HttpRequest request,
        IWebhookInbox inbox,
        ReceptionTracker tracker,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var receivedAt = time.GetUtcNow();

        if (request.ContentLength > MaxBodyBytes)
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Cuerpo demasiado grande");

        var rawPayload = await ReadBodyAsync(request, cancellationToken);
        if (rawPayload is null)
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Cuerpo demasiado grande");

        if (!IsJsonObject(rawPayload))
        {
            await inbox.SaveRejectedAsync(channel, rawPayload, receivedAt, "No es un objeto JSON válido.", CancellationToken.None);
            loggerFactory.CreateLogger("Webhook").LogWarning("Webhook {Channel} rechazado: no es un objeto JSON válido ({Bytes} bytes)", channel, rawPayload.Length);
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "JSON inválido",
                detail: "El cuerpo debe ser un objeto JSON.");
        }

        await inbox.SaveAsync(channel, rawPayload, receivedAt, CancellationToken.None);
        tracker.Mark(receivedAt);

        return TypedResults.Ok(new { received = true });
    }

    private static bool IsJsonObject(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Lee el body con limite de tamano; null si se pasa
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
