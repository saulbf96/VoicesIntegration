using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Infrastructure.Inbox;

/// <summary>Configuración de la bandeja de entrada (sección "Inbox").</summary>
public sealed class InboxOptions
{
    public const string SectionName = "Inbox";

    /// <summary>
    /// Carpeta donde se guardan los avisos. Debe estar en un disco LOCAL del servidor
    /// y el App Pool necesita permiso de escritura. Por defecto: C:\ProgramData\VoicesIntegration\inbox
    /// </summary>
    public string Path { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoicesIntegration", "inbox");

    /// <summary>Cuántos avisos toma por vuelta.</summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 500;

    /// <summary>Cuántos guarda en paralelo (cada uno con su conexión a BD).</summary>
    [Range(1, 32)]
    public int MaxDegreeOfParallelism { get; init; } = 4;

    /// <summary>Si la BD falla, cuánto esperar como máximo entre reintentos.</summary>
    [Range(1, 600)]
    public int MaxRetryDelaySeconds { get; init; } = 60;

    /// <summary>Días que se conservan los avisos rechazados (para revisarlos) antes de borrarlos solos.</summary>
    [Range(1, 365)]
    public int RejectedRetentionDays { get; init; } = 30;

    /// <summary>/health se pone "Degraded" si queda menos de esto libre en el disco de la bandeja.</summary>
    [Range(100, 1_000_000)]
    public int MinFreeDiskMb { get; init; } = 2048;

    /// <summary>/health se pone "Degraded" si el aviso más viejo lleva más de esto sin procesarse.</summary>
    [Range(1, 1440)]
    public int MaxPendingAgeMinutes { get; init; } = 5;
}
