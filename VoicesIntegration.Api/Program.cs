using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using VoicesIntegration.Api.Endpoints;
using VoicesIntegration.Api.Errors;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application;
using VoicesIntegration.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ---------- Servicios ----------
builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

// Límite de peticiones al webhook: protege la BD si alguien lo bombardea.
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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();   // /openapi/v1.json solo en desarrollo
}
else
{
    app.UseHsts();      // obliga HTTPS en el navegador
}

app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapWebhookEndpoints();
app.MapBlasterEndpoints();

app.Run();

// Permite crear pruebas de integración con WebApplicationFactory<Program>.
public partial class Program;
