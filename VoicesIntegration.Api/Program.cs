using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Serilog.Events;
using VoicesIntegration.Api.Endpoints;
using VoicesIntegration.Api.Errors;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application;
using VoicesIntegration.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var logDir = builder.Configuration["Logging:FilePath"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoicesIntegration", "logs");

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Diagnostics.HealthChecks", LogEventLevel.Error)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDir, "voices-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: null,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.PostConfigure<VoicesIntegration.Infrastructure.Monitoring.LimpiezaLogsOptions>(o => o.FilePath = logDir);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var webhookPermitsPerMinute = builder.Configuration.GetValue("Webhook:PermitsPerMinute", 30000);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(WebhookEndpoints.RateLimitPolicy, limiter =>
    {
        limiter.PermitLimit = webhookPermitsPerMinute;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseExceptionHandler();

app.UseSerilogRequestLogging(o =>
{
    o.GetLevel = (http, elapsedMs, ex) =>
        ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Response.StatusCode >= 400 ? LogEventLevel.Warning
        : elapsedMs > 2000 ? LogEventLevel.Warning
        : LogEventLevel.Debug;
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHsts();
}

if (app.Configuration.GetValue("Security:RedirectToHttps", false))
    app.UseHttpsRedirection();

// La direccion principal abre la pantalla de recuperacion.
// Comparacion exacta: "/recuperar/" (con diagonal) no entra aqui, la sirve UseDefaultFiles.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/recuperar", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/recuperar/");
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

// Healthy / Degraded (bandeja) / Unhealthy (BD)
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/detalle", new HealthCheckOptions
{
    ResponseWriter = (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            estado = report.Status.ToString(),
            revisiones = report.Entries.ToDictionary(e => e.Key, e => new
            {
                estado = e.Value.Status.ToString(),
                mensaje = e.Value.Description,
                datos = e.Value.Data
            })
        }));
    }
});

app.MapWebhookEndpoints();
app.MapBlasterEndpoints();
app.MapReportEndpoints();
app.MapRecoveryEndpoints();
app.MapDiagnosticoEndpoints();

try
{
    Log.Information("VoicesIntegration.Api iniciando (logs en {LogDir})", logDir);
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "La API se detuvo por un error al arrancar");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Para pruebas con WebApplicationFactory
public partial class Program;
