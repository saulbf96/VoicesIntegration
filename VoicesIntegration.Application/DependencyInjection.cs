using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Application.Features.Calls.GetOutboundRoutes;
using VoicesIntegration.Application.Features.Webhooks.CallResult;

namespace VoicesIntegration.Application;

/// <summary>Registra los casos de uso. Program.cs solo llama AddApplication().</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // TimeProvider en vez de DateTime.UtcNow: en pruebas se puede simular la hora.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<RegisterCallResultHandler>();
        services.AddScoped<GetCallHandler>();
        services.AddScoped<GetOutboundRoutesHandler>();

        return services;
    }
}
