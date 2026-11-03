using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using VoicesIntegration.Application.Common.Exceptions;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Api.Errors;

/// <summary>
/// Un solo lugar que convierte excepciones en respuestas HTTP (ProblemDetails).
/// Así los endpoints no llevan try/catch y nunca se filtra un stack trace al cliente.
/// </summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            DomainException ex => (StatusCodes.Status400BadRequest, "Datos inválidos", ex.Message),
            JsonException => (StatusCodes.Status400BadRequest, "JSON inválido", "El cuerpo no es un JSON válido."),
            BadHttpRequestException ex => (ex.StatusCode, "Solicitud inválida", "La solicitud no es válida."),
            ExternalServiceException ex => (StatusCodes.Status502BadGateway, "Error en servicio externo", ex.Message),
            _ => (StatusCodes.Status500InternalServerError, "Error interno", "Ocurrió un error inesperado.")
        };

        if (status >= StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Error no controlado en {Path}", httpContext.Request.Path);
        else
            logger.LogWarning("Solicitud rechazada en {Path}: {Detail}", httpContext.Request.Path, detail);

        httpContext.Response.StatusCode = status;




        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = detail }
        });
    }
}
