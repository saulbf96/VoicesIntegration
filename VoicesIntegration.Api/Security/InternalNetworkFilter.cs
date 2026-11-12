using System.Net;
using Microsoft.Extensions.Options;

namespace VoicesIntegration.Api.Security;

// Red interna entra sin llave, de fuera pide X-Api-Key
internal sealed class InternalNetworkFilter(SecretHeaderFilter apiKeyFilter) : IEndpointFilter
{
    private static readonly string[] ForwardHeaders = ["X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded", "X-Original-For"];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;

        if (IsInternal(http, options.InternalNetworks))
            return await next(context);

        return await apiKeyFilter.InvokeAsync(context, next);
    }

    internal static bool IsInternal(HttpContext http, IReadOnlyList<string> networks)
    {
        if (ForwardHeaders.Any(h => http.Request.Headers.ContainsKey(h)))
            return false;

        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        foreach (var n in networks)
            if (IPNetwork.TryParse(n, out var net) && net.Contains(ip))
                return true;
        return false;
    }
}
