using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Application.Features.Calls.GetOutboundRoutes;
using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Application.Features.Webhooks.Inbox;

namespace VoicesIntegration.Application;

/// <summary>Registra los casos de uso. Program.cs solo llama AddApplication().</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // TimeProvider en vez de DateTime.UtcNow: en pruebas se puede simular la hora.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Abstractions.Monitoring.ReceptionTracker>();

        services.AddScoped<RegisterCallResultHandler>();
        services.AddScoped<Features.Webhooks.Sms.RegisterSmsEventHandler>();
        services.AddScoped<Features.Recovery.RecoveryService>();
        services.AddScoped<GetCallHandler>();
        services.AddScoped<GetOutboundRoutesHandler>();

        // Un solo procesador para toda la app (crea un scope por aviso).
        services.AddSingleton<WebhookInboxProcessor>();

        return services;
    }
}
