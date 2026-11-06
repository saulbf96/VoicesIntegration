using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using CsvHelper;
using CsvHelper.Configuration;
using VoicesIntegration.Application.Abstractions.Recovery;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Infrastructure.Recovery;

/// <summary>
/// Lee el "Reporte de campañas" de Voices (CSV UTF-8, 22 columnas) en STREAMING: no se carga
/// completo a memoria (pesa ~4 GB por día). Acepta .zip con el .csv adentro (sube 10 veces más rápido).
/// El campo "data" es JSON con comas y comillas: CsvHelper lo respeta (formato CSV estándar).
/// </summary>
internal sealed class CsvRecoveryFileReader : IRecoveryFileReader
{
    /// <summary>Columnas mínimas para poder comparar e insertar.</summary>
    public static readonly string[] Required =
        ["identifier", "campaign_id", "campaign_name", "phone_number", "dialed_at", "failed_at", "sip_code", "sip_description"];

    private static readonly string[] DateFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "dd/MM/yyyy HH:mm:ss"];

    /// <summary>
    /// SMS: nombres posibles de cada columna (el formato exacto del reporte de SMS aún no se confirma con un archivo real).
    /// Se acepta cualquiera de los alias; los encabezados se comparan en minúsculas y con "_" en lugar de espacios.
    /// </summary>
    private static readonly Dictionary<string, string[]> SmsAliases = new()
    {
        ["message_id"] = ["message_id", "identifier", "id_mensaje", "sms_id", "id"],
        ["to"] = ["to", "phone_number", "phone", "telefono", "teléfono", "numero", "número", "destinatario"],
        ["message"] = ["message", "mensaje", "text", "texto", "body", "content"],
        ["status"] = ["status", "estado", "estatus"],
        ["operator"] = ["operator", "operador", "carrier"],
        ["batch_id"] = ["batch_id", "batch", "lote", "campaign_id"],
        ["origin_identifier"] = ["origin_identifier"],
        ["created_at"] = ["created_at", "fecha_creacion", "fecha_creación", "created"],
        ["sent_at"] = ["sent_at", "fecha_envio", "fecha_envío", "sent", "dialed_at"],
        ["failed_at"] = ["failed_at", "fecha_fallo", "failed"],
        ["status_at"] = ["status_at", "datetime", "updated_at", "fecha_estado", "delivered_at"],
    };

    public async IAsyncEnumerable<SmsRecoveryRow> ReadSmsAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        ZipArchive? zip = null;
        Stream input = file;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            zip = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException("El .zip no contiene ningún archivo .csv.");
            input = entry.Open();
        }

        try
        {
            using var sr = new StreamReader(input, detectEncodingFromByteOrderMarks: true);
            using var csv = new CsvReader(sr, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true, BadDataFound = null, MissingFieldFound = null
            });

            if (!await csv.ReadAsync()) yield break;
            csv.ReadHeader();
            var header = (csv.HeaderRecord ?? []).Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToList();
            int Col(string name) => SmsAliases[name].Select(a => header.IndexOf(a)).FirstOrDefault(i => i >= 0, -1);

            int iId = Col("message_id"), iTo = Col("to"), iMsg = Col("message"), iStatus = Col("status"), iOp = Col("operator"),
                iBatch = Col("batch_id"), iOrigin = Col("origin_identifier"), iCreated = Col("created_at"), iSent = Col("sent_at"),
                iFailed = Col("failed_at"), iStatusAt = Col("status_at");

            var faltan = new List<string>();
            if (iId < 0) faltan.Add("message_id");
            if (iStatus < 0) faltan.Add("status");
            // Un reporte de llamadas trae sip_code: avisar que se eligió el tipo equivocado.
            if (header.Contains("sip_code") || faltan.Count > 0)
                throw new DomainException(header.Contains("sip_code")
                    ? "Este archivo es un reporte de LLAMADAS (Blaster), no de mensajes. Cambia el tipo a \"Llamadas\"."
                    : "El archivo no parece el reporte de MENSAJES (SMS) de Voices. ¿Elegiste el tipo correcto? Faltan columnas: " +
                      string.Join(", ", faltan) + ". Columnas del archivo: " + string.Join(", ", header));

            string? F(int i) => i < 0 ? null : csv.GetField(i);

            while (await csv.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                var id = Empty(F(iId));
                if (id is null) continue;
                yield return new SmsRecoveryRow(
                    MessageId: Cut(id, 100)!,
                    To: Cut(F(iTo), 20),
                    Message: Empty(F(iMsg)),
                    Status: Cut(F(iStatus), 30)?.ToUpperInvariant(),
                    Operator: Cut(F(iOp), 20),
                    BatchId: Cut(F(iBatch), 100),
                    OriginIdentifier: Cut(F(iOrigin), 100),
                    CreatedAt: AnyDate(F(iCreated)),
                    SentAt: AnyDate(F(iSent)),
                    FailedAt: AnyDate(F(iFailed)),
                    StatusAt: AnyDate(F(iStatusAt)));
            }
        }
        finally
        {
            if (!ReferenceEquals(input, file)) await input.DisposeAsync();
            zip?.Dispose();
        }
    }

    private static readonly TimeZoneInfo Mexico = FindMexico();

    /// <summary>Fecha "tal cual" (hora México) o, si trae zona (ej. 2026-11-05T18:00:00Z), convertida a hora México.</summary>
    private static DateTime? AnyDate(string? v)
    {
        if (Date(v) is { } d) return d;
        return DateTimeOffset.TryParse(Empty(v), CultureInfo.InvariantCulture, DateTimeStyles.None, out var o)
            ? TimeZoneInfo.ConvertTime(o, Mexico).DateTime : null;
    }

    private static TimeZoneInfo FindMexico()
    {
        foreach (var id in new[] { "Central Standard Time (Mexico)", "America/Mexico_City" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.Utc;
    }

    public async IAsyncEnumerable<RecoveryRow> ReadAsync(string path, bool light, Func<string, bool>? include, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        ZipArchive? zip = null;
        Stream input = file;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            zip = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException("El .zip no contiene ningún archivo .csv.");
            input = entry.Open();
        }

        try
        {
            using var sr = new StreamReader(input, detectEncodingFromByteOrderMarks: true);
            var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                BadDataFound = null,          // comillas raras dentro de un campo: no detener la carga
                MissingFieldFound = null,     // filas con menos columnas: campos faltantes = vacío
                // Sin TrimOptions: recortar cada campo (incluidos los textos largos) es caro; Empty() recorta solo lo que se usa.
                PrepareHeaderForMatch = a => a.Header.Trim().ToLowerInvariant()
            };
            using var csv = new CsvReader(sr, cfg);

            if (!await csv.ReadAsync()) yield break;
            csv.ReadHeader();
            var header = (csv.HeaderRecord ?? []).Select(h => h.Trim().ToLowerInvariant()).ToList();
            var missing = Required.Where(r => !header.Contains(r)).ToList();
            if (missing.Count > 0)
                throw new DomainException("El archivo no parece el reporte de LLAMADAS (Blaster) de Voices. ¿Elegiste el tipo correcto? Faltan columnas: " + string.Join(", ", missing));

            // Posición de cada columna (se busca UNA vez, no en cada fila). -1 = no viene en el archivo.
            int I(string name) => header.IndexOf(name);
            int iId = I("identifier"), iCampId = I("campaign_id"), iCampName = I("campaign_name"), iChannel = I("channel"),
                iPhone = I("phone_number"), iType = I("type"), iData = I("data"), iMsg = I("message"), iAttempts = I("call_attempts"),
                iDurMin = I("duration_minutes"), iDurSec = I("duration_seconds"), iTrMin = I("duration_transfer_minutes"),
                iTrSec = I("duration_transfer_seconds"), iCredits = I("credits"), iDialed = I("dialed_at"), iFailed = I("failed_at"),
                iSip = I("sip_code"), iSipDesc = I("sip_description"), iTransfer = I("transfer"), iSipTr = I("sip_code_transfer"),
                iSipTrDesc = I("sip_description_transfer"), iANum = I("a_num");
            string? F(int i) => i < 0 ? null : csv.GetField(i);

            while (await csv.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                var id = F(iId);
                if (string.IsNullOrWhiteSpace(id)) continue;   // fila sin llave: no se puede comparar
                id = id.Trim();
                if (include is not null && !include(id)) continue;   // no es de las que se buscan

                if (light)
                {
                    // Solo lo necesario para comparar (rápido): llave, resultado, fechas y a_num.
                    yield return new RecoveryRow(Cut(id, 100)!, null, null, null, null, null, null, null, null, null, null, null, null, null,
                        DialedAt: Date(F(iDialed)), FailedAt: Date(F(iFailed)), SipCode: Int(F(iSip)), null, null, null, null,
                        ANum: Cut(F(iANum), 20));
                    continue;
                }

                yield return new RecoveryRow(
                    Identifier: Cut(id, 100)!,
                    CampaignId: Cut(F(iCampId), 50),
                    CampaignName: Cut(F(iCampName), 300),
                    Channel: Cut(F(iChannel), 20),
                    PhoneNumber: Cut(F(iPhone), 20),
                    Type: Cut(F(iType), 20),
                    Data: Empty(F(iData)),
                    Message: Empty(F(iMsg)),
                    CallAttempts: Int(F(iAttempts)),
                    DurationMinutes: Int(F(iDurMin)),
                    DurationSeconds: Int(F(iDurSec)),
                    DurationTransferMinutes: Int(F(iTrMin)),
                    DurationTransferSeconds: Int(F(iTrSec)),
                    Credits: Dec(F(iCredits)),
                    DialedAt: Date(F(iDialed)),
                    FailedAt: Date(F(iFailed)),
                    SipCode: Int(F(iSip)),
                    SipDescription: Cut(F(iSipDesc), 200),
                    Transfer: Bool(F(iTransfer)),
                    SipCodeTransfer: Int(F(iSipTr)),
                    SipDescriptionTransfer: Cut(F(iSipTrDesc), 200),
                    ANum: Cut(F(iANum), 20));
            }
        }
        finally
        {
            if (!ReferenceEquals(input, file)) await input.DisposeAsync();
            zip?.Dispose();
        }
    }

    private static string? Empty(string? v) => string.IsNullOrWhiteSpace(v) || v == "-" ? null : v.Trim();
    private static string? Cut(string? v, int max) { v = Empty(v); return v is null || v.Length <= max ? v : v[..max]; }
    private static int? Int(string? v) => int.TryParse(Empty(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    private static decimal? Dec(string? v) => decimal.TryParse(Empty(v), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static DateTime? Date(string? v) =>
        DateTime.TryParseExact(Empty(v), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    // "Sí" / "Si" / "S" / "true" / "1" = transferida. (El reporte trae "Sí" o "No".)
    private static bool? Bool(string? v) => Empty(v) is not { } s ? null
        : s.StartsWith('S') || s.StartsWith('s') || s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
}
