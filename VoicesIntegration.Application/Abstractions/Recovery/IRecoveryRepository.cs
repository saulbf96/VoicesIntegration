namespace VoicesIntegration.Application.Abstractions.Recovery;

/// <summary>Estados de una recuperación (igual que en dbo.TBL_RECUPERACIONES_VOICES).</summary>
public static class RecoveryStates
{
    public const string Subido = "SUBIDO";
    public const string Leyendo = "LEYENDO";
    public const string Revisado = "REVISADO";
    public const string PorInsertar = "POR_INSERTAR";
    public const string Insertando = "INSERTANDO";
    public const string Terminado = "TERMINADO";
    public const string Error = "ERROR";
    public const string Cancelado = "CANCELADO";
}

/// <summary>Tipo de archivo que se recupera.</summary>
public static class RecoveryChannels
{
    public const string Blaster = "BLASTER";
    public const string Sms = "SMS";
    public static bool IsValid(string? c) => c is Blaster or Sms;
}

/// <summary>Una fila del reporte de SMS de Voices, ya convertida a tipos (fechas en hora de México).</summary>
public sealed record SmsRecoveryRow(
    string MessageId,
    string? To,
    string? Message,
    string? Status,
    string? Operator,
    string? BatchId,
    string? OriginIdentifier,
    DateTime? CreatedAt,
    DateTime? SentAt,
    DateTime? FailedAt,
    DateTime? StatusAt);

/// <summary>Una fila del reporte de Voices ("Reporte de campañas" de Blaster), ya convertida a tipos.</summary>
public sealed record RecoveryRow(
    string Identifier,
    string? CampaignId,
    string? CampaignName,
    string? Channel,
    string? PhoneNumber,
    string? Type,
    string? Data,
    string? Message,
    int? CallAttempts,
    int? DurationMinutes,
    int? DurationSeconds,
    int? DurationTransferMinutes,
    int? DurationTransferSeconds,
    decimal? Credits,
    DateTime? DialedAt,      // hora México, tal cual el reporte
    DateTime? FailedAt,
    int? SipCode,
    string? SipDescription,
    bool? Transfer,
    int? SipCodeTransfer,
    string? SipDescriptionTransfer,
    string? ANum);

public sealed record RecoveryJob
{
    public Guid JobId { get; init; }
    public string Archivo { get; init; } = string.Empty;
    public long TamanoBytes { get; init; }
    public string? Usuario { get; init; }
    public string Estado { get; init; } = string.Empty;
    public int FilasLeidas { get; init; }
    public int? FilasSinMarcar { get; init; }
    public int? YaExistian { get; init; }
    public int? Faltantes { get; init; }
    public int? Insertadas { get; init; }
    public int? ANumActualizados { get; init; }
    public DateTime? Desde { get; init; }
    public DateTime? Hasta { get; init; }
    public string? Mensaje { get; init; }
    public DateTime FechaSubida { get; init; }
    public DateTime FechaActualizacion { get; init; }
    public string Canal { get; init; } = RecoveryChannels.Blaster;
}

/// <summary>Persistencia de la recuperación (SPs 20-22 y carga masiva a la tabla temporal 19).</summary>
public interface IRecoveryRepository
{
    Task CreateAsync(Guid jobId, string fileName, long sizeBytes, string? user, string canal, CancellationToken ct);

    /// <summary>SMS: carga masiva del reporte a la temporal de SMS (bloques de 100 mil). Devuelve filas leídas.</summary>
    Task<int> BulkLoadSmsAsync(Guid jobId, IAsyncEnumerable<SmsRecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct);
    Task CompareSmsAsync(Guid jobId, CancellationToken ct);
    /// <summary>SMS: inserta los que faltan, completa número/texto de los que llegaron por webhook y limpia lo temporal.</summary>
    Task InsertSmsAsync(Guid jobId, CancellationToken ct);
    Task UpdateAsync(Guid jobId, string? state, int? rowsRead, string? message, CancellationToken ct);
    Task<RecoveryJob?> GetAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<RecoveryJob>> ListAsync(CancellationToken ct);

    /// <summary>Carga masiva LIGERA (llave, sip_code, fechas, a_num) a la temporal, en bloques de 100 mil. Devuelve filas leídas.</summary>
    Task<int> BulkLoadLightAsync(Guid jobId, IAsyncEnumerable<RecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct);

    /// <summary>identifiers del archivo que faltan en la tabla (para cargar completas SOLO esas).</summary>
    Task<IdentifierSet> GetMissingIdsAsync(Guid jobId, CancellationToken ct);

    /// <summary>Carga un lote de filas COMPLETAS (las que faltan) a la tabla de carga.</summary>
    Task BulkLoadBatchAsync(Guid jobId, int lote, IReadOnlyList<RecoveryRow> rows, CancellationToken ct);

    /// <summary>
    /// Bulk DIRECTO a la tabla final (las que ya se sabe que faltan). Todo el lote o nada.
    /// Devuelve false si chocó con una llamada que ya existe (un webhook llegó en ese instante): mandar el lote por el camino seguro.
    /// </summary>
    Task<bool> BulkInsertFinalAsync(IReadOnlyList<RecoveryRow> rows, CancellationToken ct);

    /// <summary>Suma al contador de insertadas de la bitácora.</summary>
    Task AddInsertedAsync(Guid jobId, int count, CancellationToken ct);

    /// <summary>Inserta en la tabla final un lote ya cargado (sin duplicar) y lo libera.</summary>
    Task InsertBatchAsync(Guid jobId, int lote, CancellationToken ct);

    Task CompareAsync(Guid jobId, CancellationToken ct);
    /// <summary>Completa a_num de las que ya estaban, vacía lo temporal y cierra la bitácora.</summary>
    Task InsertMissingAsync(Guid jobId, CancellationToken ct);
    Task CancelAsync(Guid jobId, CancellationToken ct);
}

/// <summary>Lee el archivo del reporte de Voices (.csv o .zip con un .csv adentro).</summary>
public interface IRecoveryFileReader
{
    /// <summary>
    /// Valida encabezados (DomainException si faltan columnas) y devuelve las filas una por una (streaming).
    /// <paramref name="light"/> = true: no lee "data" ni "message" (los textos largos) → mucho más rápido para comparar.
    /// <paramref name="include"/>: si viene, solo devuelve las filas cuyo identifier cumpla (ej. solo las faltantes).
    /// </summary>
    IAsyncEnumerable<RecoveryRow> ReadAsync(string path, bool light, Func<string, bool>? include, CancellationToken ct);

    /// <summary>Reporte de SMS (.csv o .zip). Valida encabezados (DomainException si no parece de SMS) y devuelve en streaming.</summary>
    IAsyncEnumerable<SmsRecoveryRow> ReadSmsAsync(string path, CancellationToken ct);
}
