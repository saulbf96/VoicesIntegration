using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VoicesIntegration.Application.Abstractions.Blaster;
using VoicesIntegration.Application.Abstractions.Persistence;
using VoicesIntegration.Infrastructure.Blaster;
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

        services.AddHealthChecks()
            .AddCheck<SqlServerHealthCheck>("database");

        return services;
    }
}
