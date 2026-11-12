using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using VoicesIntegration.Api.Security;
using VoicesIntegration.Application.Abstractions.Inbox;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Application.Features.Recovery;
using VoicesIntegration.Infrastructure.Recovery;

namespace VoicesIntegration.Api.Endpoints;

// Recuperacion desde el reporte de Voices (pantalla /recuperar/)
public static class RecoveryEndpoints
{
    private static readonly string[] Allowed = [".csv", ".zip"];

    public static IEndpointRouteBuilder MapRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/recuperacion").WithTags("Recuperación").RequireInternalNetworkOrApiKey();

        g.MapPost("/", UploadAsync).DisableAntiforgery();
        g.MapGet("/", async (IRecoveryRepository repo, CancellationToken ct) => Results.Ok(await repo.ListAsync(ct)));
        g.MapGet("/{id:guid}", async (Guid id, IRecoveryRepository repo, CancellationToken ct) =>
            await repo.GetAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound());

        g.MapPost("/{id:guid}/insertar", async (Guid id, IRecoveryRepository repo, RecoveryQueue queue, IWebhookInbox inbox,
            TimeProvider time, CancellationToken ct) =>
        {
            var job = await repo.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Estado is not (RecoveryStates.Revisado or RecoveryStates.Error) || job.Faltantes is null)
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No se puede insertar",
                    detail: $"La recuperación está en estado {job.Estado}.");
            if (RecoveryDayRule.WhyNot(job, inbox.GetStatus(), time.GetUtcNow()) is { } motivo)
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No se puede insertar todavía", detail: motivo);
            await repo.UpdateAsync(id, RecoveryStates.PorInsertar, null, "En cola para insertar...", ct);
            await queue.EnqueueAsync(new RecoveryWork(id, true, null));
            return Results.Accepted($"/api/recuperacion/{id}", new { jobId = id });
        });

        g.MapGet("/historial", HistoryAsync);

        // Si esta corriendo se detiene y el worker lo deja en CANCELADO; lo ya insertado se queda
        g.MapPost("/{id:guid}/cancelar", async (Guid id, IRecoveryRepository repo, RecoveryCancellations running,
            IOptions<RecoveryOptions> o, CancellationToken ct) =>
        {
            var job = await repo.GetAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.Estado is RecoveryStates.Terminado or RecoveryStates.Cancelado)
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No se puede cancelar", detail: $"Estado {job.Estado}.");
            if (running.Cancel(id))
                return Results.Accepted($"/api/recuperacion/{id}", job);
            await repo.CancelAsync(id, ct);
            foreach (var f in Directory.EnumerateFiles(o.Value.Path, id.ToString("N") + ".*"))
                Application.Features.Recovery.RecoveryService.TryDelete(f);
            return Results.Ok(await repo.GetAsync(id, ct));
        });

        return app;
    }

    private static readonly int[] PageSizes = [5, 10, 20];

    private static async Task<IResult> HistoryAsync(string? dia, string? tipo, string? q, int? page, int? size,
        IRecoveryRepository repo, CancellationToken ct)
    {
        DateOnly? d = null;
        if (!string.IsNullOrWhiteSpace(dia))
        {
            if (!DateOnly.TryParseExact(dia, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x))
                return Results.Problem(statusCode: 400, title: "Día inválido", detail: "Usa el formato AAAA-MM-DD.");
            d = x;
        }

        string? canal;
        switch (tipo?.Trim().ToLowerInvariant())
        {
            case null or "" or "all": canal = null; break;
            case "blaster": canal = RecoveryChannels.Blaster; break;
            case "sms": canal = RecoveryChannels.Sms; break;
            default: return Results.Problem(statusCode: 400, title: "Tipo inválido", detail: "El tipo debe ser all, blaster o sms.");
        }

        var p = Math.Max(1, page ?? 1);
        var s = size is { } v && PageSizes.Contains(v) ? v : 10;
        var r = await repo.HistoryAsync(d, canal, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), p, s, ct);
        return Results.Ok(new { total = r.Total, items = r.Items });
    }

    // El archivo se guarda directo a disco, puede pesar varios GB
    private static async Task<IResult> UploadAsync(HttpContext ctx, IRecoveryRepository repo, RecoveryQueue queue,
        IOptions<RecoveryOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var o = options.Value;
        var max = (long)o.MaxFileGb * 1024 * 1024 * 1024;

        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = max;

        if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var mediaType) || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: 400, title: "Se esperaba un formulario con el archivo (multipart/form-data).");

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return Results.Problem(statusCode: 400, title: "Formulario inválido.");

        Directory.CreateDirectory(o.Path);
        var reader = new MultipartReader(boundary, ctx.Request.Body);
        string? user = null, fileName = null, savedPath = null;
        string canal = RecoveryChannels.Blaster;
        long size = 0;
        var jobId = Guid.NewGuid();

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)) continue;

                if (cd.IsFormDisposition() && cd.Name.Value == "usuario")
                {
                    using var sr = new StreamReader(section.Body);
                    user = (await sr.ReadToEndAsync(ct)).Trim();
                }
                else if (cd.IsFormDisposition() && cd.Name.Value == "canal")
                {
                    using var sr = new StreamReader(section.Body);
                    canal = (await sr.ReadToEndAsync(ct)).Trim().ToUpperInvariant();
                    if (!RecoveryChannels.IsValid(canal))
                        return Results.Problem(statusCode: 400, title: "Tipo inválido", detail: "El tipo debe ser BLASTER o SMS.");
                }
                else if (cd.IsFileDisposition())
                {
                    fileName = Path.GetFileName(cd.FileName.Value ?? cd.FileNameStar.Value ?? "reporte.csv");
                    var ext = Path.GetExtension(fileName).ToLowerInvariant();
                    if (!Allowed.Contains(ext))
                        return Results.Problem(statusCode: 400, title: "Archivo no permitido", detail: "Solo se aceptan .csv o .zip.");

                    savedPath = Path.Combine(o.Path, jobId.ToString("N") + ext);
                    await using var fs = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
                    await section.Body.CopyToAsync(fs, 1 << 20, ct);
                    size = fs.Length;
                }
            }

            if (savedPath is null || size == 0)
                return Results.Problem(statusCode: 400, title: "No llegó ningún archivo.");

            await repo.CreateAsync(jobId, fileName!, size, user, canal, ct);
            await queue.EnqueueAsync(new RecoveryWork(jobId, false, savedPath));
            logs.CreateLogger("Recuperacion").LogInformation("Recuperación {JobId} ({Canal}): archivo {File} ({Mb} MB)",
                jobId, canal, fileName, size / (1024 * 1024));
            return Results.Accepted($"/api/recuperacion/{jobId}", new { jobId });
        }
        catch
        {
            if (savedPath is not null && File.Exists(savedPath)) File.Delete(savedPath);
            throw;
        }
    }
}
