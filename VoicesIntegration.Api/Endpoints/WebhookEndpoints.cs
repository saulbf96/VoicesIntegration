using System.Text;
using System.Text.Json;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Api.Endpoints;

/// <summary>
/// Endpoint que llama Voices (Blaster) cada vez que termina una llamada.
/// Esta es la "descarga" de la información: Voices nos empuja los resultados.
/// </summary>
public static class WebhookEndpoints
{
    public const string RateLimitPolicy = "webhook";

    // Un webhook de Blaster pesa < 1 KB; 64 KB evita que nos manden cuerpos gigantes.
    private const int MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/blaster/call-result", ReceiveCallResultAsync)
            .WithTags("Webhooks")
            .WithName("BlasterCallResultWebhook")
            .Accepts<CallResultPayload>("application/json")
            .RequireWebhookSecret()
            .RequireRateLimiting(RateLimitPolicy);

        return app;
    }

    private static async Task<IResult> ReceiveCallResultAsync(
        HttpRequest request,
        RegisterCallResultHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Cuerpo demasiado grande");

        // Se lee el JSON crudo porque además de procesarlo se guarda tal cual (auditoría).
        var rawPayload = await ReadBodyAsync(request, cancellationToken);
        if (rawPayload is null)
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Cuerpo demasiado grande");

        if (string.IsNullOrWhiteSpace(rawPayload))
            throw new DomainException("El cuerpo del webhook está vacío.");

        var payload = JsonSerializer.Deserialize<CallResultPayload>(rawPayload)
            ?? throw new DomainException("El cuerpo del webhook está vacío.");

        var outcome = await handler.HandleAsync(payload, rawPayload, cancellationToken);

        // Siempre 2xx (también en duplicados) para que Voices no siga reintentando.
        return TypedResults.Ok(new { received = true, duplicate = outcome == RegisterCallResultOutcome.Duplicate });
    }

    /// <summary>Lee el cuerpo con tope de tamaño (también si no trae Content-Length). Null = excedido.</summary>
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
