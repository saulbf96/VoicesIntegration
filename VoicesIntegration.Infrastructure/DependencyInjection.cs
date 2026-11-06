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

/// <summary>
/// Registra las implementaciones técnicas. Program.cs solo llama AddInfrastructure().
/// Aquí se decide QUÉ implementación concreta recibe cada interfaz de Application.
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "VoicesIntegration";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Blaster: opciones validadas al arrancar (si falta la ApiKey la app NO inicia) ----
        services.AddOptions<BlasterOptions>()
            .Bind(configuration.GetSection(BlasterOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // IHttpClientFactory administra las conexiones (evita agotar sockets).
        services.AddHttpClient<IBlasterGateway, BlasterHttpGateway>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BlasterOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
        });

        // ---- SQL Server ----
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Falta la cadena de conexión 'ConnectionStrings:{ConnectionStringName}'.");

        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
        services.AddScoped<IBlasterCallRepository, BlasterCallRepository>();
        services.AddScoped<ISmsRepository, SmsRepository>();

        // ---- SMS: consulta por id para completar número y texto (ApiKey opcional) ----
        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        // Memoria propia y acotada (máx. 50 mil detalles) para no consultar dos veces el mismo SMS.
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

        // ---- Bandeja de entrada durable (los avisos se guardan en disco ANTES de la BD) ----
        services.AddOptions<InboxOptions>()
            .Bind(configuration.GetSection(InboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<FileWebhookInbox>();
        services.AddSingleton<IWebhookInbox>(sp => sp.GetRequiredService<FileWebhookInbox>());
        services.AddHostedService<WebhookInboxWorker>();

        // ---- Recuperación: el usuario sube el reporte de Voices y se insertan solo las faltantes ----
        services.AddOptions<RecoveryOptions>()
            .Bind(configuration.GetSection(RecoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<IRecoveryRepository, RecoveryRepository>();
        services.AddSingleton<IRecoveryFileReader, CsvRecoveryFileReader>();
        services.AddSingleton<RecoveryQueue>();
        services.AddHostedService<RecoveryWorker>();

        // ---- Monitoreo: registro de disponibilidad cada minuto + resumen diario de huecos en el log ----
        services.AddScoped<IMonitoringRepository, MonitoringRepository>();
        services.AddHostedService<HeartbeatWorker>();

        // Avisos por correo (igual que CCC): inicio de recepción, huecos, fallas y resumen del día.
        services.AddOptions<Notificaciones.CorreoOptions>().Bind(configuration.GetSection(Notificaciones.CorreoOptions.Seccion));
        services.AddSingleton<Application.Abstractions.Notifications.INotificador, Notificaciones.NotificadorCorreo>();
        services.AddOptions<AlertasOptions>().Bind(configuration.GetSection(AlertasOptions.Seccion));
        services.AddHostedService<AlertasWorker>();

        // Limpieza semanal del log (lunes 23:00 por defecto: se queda solo el log de ese día).
        services.AddOptions<LimpiezaLogsOptions>().Bind(configuration.GetSection("Logging"));
        services.AddHostedService<LimpiezaLogsWorker>();

        services.AddHealthChecks()
            .AddCheck<SqlServerHealthCheck>("database")
            .AddCheck<InboxHealthCheck>("inbox");

        return services;
    }
}
