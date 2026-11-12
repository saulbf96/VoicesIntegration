    using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace VoicesIntegration.Api.Security;

// Valida un header secreto, si no coincide 401
internal sealed class SecretHeaderFilter(string headerName, Func<SecurityOptions, string> selectSecret) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;

        var provided = httpContext.Request.Headers[headerName].ToString();

        if (!SecretsAreEqual(provided, selectSecret(options)))
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILogger<SecretHeaderFilter>>();
            logger.LogWarning(
                "Acceso rechazado a {Path} desde {RemoteIp}: header {Header} ausente o inválido.",
                httpContext.Request.Path, httpContext.Connection.RemoteIpAddress, headerName);

            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "No autorizado");
        }

        return await next(context);
    }

    // Comparacion en tiempo constante
    private static bool SecretsAreEqual(string provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
            return false;

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }
}

public static class SecurityEndpointExtensions
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string WebhookSecretHeader = "X-Webhook-Secret";

    public static TBuilder RequireInternalApiKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new SecretHeaderFilter(ApiKeyHeader, o => o.InternalApiKey));

    public static TBuilder RequireWebhookSecret<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new SecretHeaderFilter(WebhookSecretHeader, o => o.WebhookSecret));

    public static TBuilder RequireInternalNetworkOrApiKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new InternalNetworkFilter(new SecretHeaderFilter(ApiKeyHeader, o => o.InternalApiKey)));
}
