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

// ---------- Logs a archivo (uno por día, máx. 50 MB por archivo) ----------
// Carpeta configurable con "Logging:FilePath". Por defecto C:\ProgramData\VoicesIntegration\logs
// Limpieza SEMANAL (LimpiezaLogsWorker): "Logging:LimpiezaDia" / "Logging:LimpiezaHora" (lunes 23:00) borra todo
// menos el log de ese día, así la semana empieza limpia.
// NUNCA se escriben secretos ni el JSON del aviso (trae datos personales): solo ids y estados.
var logDir = builder.Configuration["Logging:FilePath"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoicesIntegration", "logs");

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    // El health check ya responde su estado en /health; no repetirlo en el log en cada consulta.
    .MinimumLevel.Override("Microsoft.Extensions.Diagnostics.HealthChecks", LogEventLevel.Error)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDir, "voices-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: null,   // la limpieza semanal la hace LimpiezaLogsWorker
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// ---------- Servicios ----------
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

// Al apagar (reciclado del IIS), dar tiempo a terminar el lote en curso.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// Límite de peticiones al webhook: protege el servidor si alguien lo bombardea.
// Debe ser MAYOR que el volumen real de Blaster (en ISB se vieron miles por minuto);
// si se queda corto, Voices recibe 429 y esos resultados se pueden perder.
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

// ---------- Pipeline (el orden importa) ----------
app.UseExceptionHandler();

// Una línea por petición SOLO si algo salió mal (4xx/5xx) o tardó más de 2 s:
// con cientos de miles de avisos al día, registrar todas llenaría el disco.
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
    app.MapOpenApi();   // /openapi/v1.json solo en desarrollo
}
else
{
    app.UseHsts();      // obliga HTTPS en el navegador
}

// Redirigir a HTTPS solo si se pide (Security:RedirectToHttps = true). En la PC y en el IIS de pruebas
// la API corre en http: sin esto salía el aviso "Failed to determine the https port for redirect".
if (app.Configuration.GetValue("Security:RedirectToHttps", false))
    app.UseHttpsRedirection();

// Pantalla de recuperación: http://servidor:8093/recuperar/  (archivos en wwwroot/recuperar).
// UseDefaultFiles sirve index.html y agrega solo la "/" final si falta.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

// /health: "Healthy", "Degraded" (avisos atorados o rechazados) o "Unhealthy" (BD caída).
// /health/detalle: lo mismo en JSON con el detalle de cada revisión (sin datos sensibles).
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

// Permite crear pruebas de integración con WebApplicationFactory<Program>.
public partial class Program;
