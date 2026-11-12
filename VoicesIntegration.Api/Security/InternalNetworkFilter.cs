using System.Net;
using Microsoft.Extensions.Options;

namespace VoicesIntegration.Api.Security;

/// <summary>
/// Para la pantalla /recuperar/: si la petición viene de la RED INTERNA (o de la misma PC), entra sin llave.
/// Si viene de fuera (internet, o a través del túnel), exige X-Api-Key como siempre.
///
/// OJO con el túnel: dev tunnels entrega las peticiones desde el propio servidor (127.0.0.1), así que
/// "localhost" NO basta para confiar: si la petición trae encabezados de reenvío (X-Forwarded-*),
/// se trata como EXTERNA.
/// </summary>
internal sealed class InternalNetworkFilter(SecretHeaderFilter apiKeyFilter) : IEndpointFilter
{
    private static readonly string[] ForwardHeaders = ["X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded", "X-Original-For"];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;

        if (IsInternal(http, options.InternalNetworks))
            return await next(context);

        return await apiKeyFilter.InvokeAsync(context, next);   // de fuera: API Key obligatoria
    }

    internal static bool IsInternal(HttpContext http, IReadOnlyList<string> networks)
    {
        if (ForwardHeaders.Any(h => http.Request.Headers.ContainsKey(h)))
            return false;   // pasó por un proxy/túnel: puede venir de internet

        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        foreach (var n in networks)
            if (IPNetwork.TryParse(n, out var net) && net.Contains(ip))
                return true;
        return false;
    }
}
