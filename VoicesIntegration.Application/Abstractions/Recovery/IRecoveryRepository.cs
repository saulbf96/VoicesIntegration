namespace VoicesIntegration.Application.Abstractions.Recovery;

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

public static class RecoveryChannels
{
    public const string Blaster = "BLASTER";
    public const string Sms = "SMS";
    public static bool IsValid(string? c) => c is Blaster or Sms;
}

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
    DateTime? DialedAt,
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

public interface IRecoveryRepository
{
    Task CreateAsync(Guid jobId, string fileName, long sizeBytes, string? user, string canal, CancellationToken ct);

    Task<int> BulkLoadSmsAsync(Guid jobId, IAsyncEnumerable<SmsRecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct);
    Task CompareSmsAsync(Guid jobId, CancellationToken ct);
    Task InsertSmsAsync(Guid jobId, CancellationToken ct);
    Task UpdateAsync(Guid jobId, string? state, int? rowsRead, string? message, CancellationToken ct);
    Task<RecoveryJob?> GetAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<RecoveryJob>> ListAsync(CancellationToken ct);

    Task<int> BulkLoadLightAsync(Guid jobId, IAsyncEnumerable<RecoveryRow> rows, Func<int, Task> onProgress, CancellationToken ct);

    Task<IdentifierSet> GetMissingIdsAsync(Guid jobId, CancellationToken ct);

    Task BulkLoadBatchAsync(Guid jobId, int lote, IReadOnlyList<RecoveryRow> rows, CancellationToken ct);

    Task<bool> BulkInsertFinalAsync(Guid jobId, IReadOnlyList<RecoveryRow> rows, CancellationToken ct);

    Task AddInsertedAsync(Guid jobId, int count, CancellationToken ct);

    Task InsertBatchAsync(Guid jobId, int lote, CancellationToken ct);

    Task CompareAsync(Guid jobId, CancellationToken ct);
    Task InsertMissingAsync(Guid jobId, CancellationToken ct);
    Task CancelAsync(Guid jobId, CancellationToken ct);
}

public interface IRecoveryFileReader
{
    IAsyncEnumerable<RecoveryRow> ReadAsync(string path, bool light, Func<string, bool>? include, CancellationToken ct);

    IAsyncEnumerable<SmsRecoveryRow> ReadSmsAsync(string path, CancellationToken ct);
}
