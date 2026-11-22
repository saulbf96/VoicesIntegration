using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Blaster;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Monitoring;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Infrastructure.Recovery;
using VoicesIntegration.Infrastructure.Monitoring;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Application.Abstractions.Sms;
using VoicesIntegration.Infrastructure.Blaster;
using VoicesIntegration.Infrastructure.Sms;
using VoicesIntegration.Infrastructure.Inbox;
using VoicesIntegration.Infrastructure.Persistence;

namespace VoicesIntegration.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "VoicesIntegration";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BlasterOptions>()
            .Bind(configuration.GetSection(BlasterOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IBlasterGateway, BlasterHttpGateway>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BlasterOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
        });

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Falta la cadena de conexión 'ConnectionStrings:{ConnectionStringName}'.");

        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
        services.AddScoped<IBlasterCallRepository, BlasterCallRepository>();
        services.AddScoped<ISmsRepository, SmsRepository>();

        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(new SmsGatewayState(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 50_000 })));
        services.AddHttpClient<ISmsGateway, SmsHttpGateway>((serviceProvider, client) =>
        {
            var o = serviceProvider.GetRequiredService<IOptions<SmsOptions>>().Value;
            client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(o.ApiKey))
                client.DefaultRequestHeaders.Add("x-api-key", o.ApiKey);
        });

        services.AddOptions<InboxOptions>()
            .Bind(configuration.GetSection(InboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<FileWebhookInbox>();
        services.AddSingleton<SegmentedWebhookInbox>();
        services.AddSingleton<IWebhookInbox>(sp => sp.GetRequiredService<SegmentedWebhookInbox>());
        services.AddHostedService<WebhookInboxWorker>();

        services.AddOptions<RecoveryOptions>()
            .Bind(configuration.GetSection(RecoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<IRecoveryRepository, RecoveryRepository>();
        services.AddSingleton<IRecoveryFileReader, CsvRecoveryFileReader>();
        services.AddSingleton<RecoveryQueue>();
        services.AddSingleton<RecoveryCancellations>();
        services.AddHostedService<RecoveryWorker>();

        services.AddScoped<IMonitoringRepository, MonitoringRepository>();
        services.AddScoped<IDiagnosticoBd, DiagnosticoBd>();
        services.AddHostedService<HeartbeatWorker>();

        services.AddOptions<Notificaciones.CorreoOptions>().Bind(configuration.GetSection(Notificaciones.CorreoOptions.Seccion));
        services.AddSingleton<Application.Abstractions.Notifications.INotificador, Notificaciones.NotificadorCorreo>();
        services.AddOptions<AlertasOptions>().Bind(configuration.GetSection(AlertasOptions.Seccion));
        services.AddHostedService<AlertasWorker>();

        services.AddOptions<LimpiezaLogsOptions>().Bind(configuration.GetSection("Logging"));
        services.AddHostedService<LimpiezaLogsWorker>();

        services.AddHealthChecks()
            .AddCheck<SqlServerHealthCheck>("database")
            .AddCheck<InboxHealthCheck>("inbox");

        return services;
    }
}
