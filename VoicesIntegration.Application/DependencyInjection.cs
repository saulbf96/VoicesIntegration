using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoicesIntegration.Application.Features.Calls.GetCall;
using VoicesIntegration.Application.Features.Calls.GetOutboundRoutes;
using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Application.Features.Webhooks.Inbox;

namespace VoicesIntegration.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Abstractions.Monitoring.ReceptionTracker>();
        services.AddScoped<RegisterCallResultHandler>();
        services.AddScoped<Features.Webhooks.Sms.RegisterSmsEventHandler>();
        services.AddScoped<Features.Recovery.RecoveryService>();
        services.AddScoped<GetCallHandler>();
        services.AddScoped<GetOutboundRoutesHandler>();

        services.AddSingleton<WebhookInboxProcessor>();

        return services;
    }
}
