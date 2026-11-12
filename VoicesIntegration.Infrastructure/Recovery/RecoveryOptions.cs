using System.ComponentModel.DataAnnotations;

namespace VoicesIntegration.Infrastructure.Recovery;

/// <summary>Configuración de la recuperación (sección "Recovery").</summary>
public sealed class RecoveryOptions
{
    public const string SectionName = "Recovery";

    /// <summary>Carpeta donde se guarda el archivo subido mientras se procesa (luego se borra).</summary>
    public string Path { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoicesIntegration", "recuperacion");

    /// <summary>Tamaño máximo del archivo (GB). El reporte de un día de ISB pesa ~4 GB en CSV (~0.4 GB en .zip).</summary>
    [Range(1, 20)]
    public int MaxFileGb { get; init; } = 6;

    /// <summary>Horas que una revisión puede quedarse sin insertar antes de cancelarse sola (libera la tabla temporal).</summary>
    [Range(1, 720)]
    public int ReviewedExpireHours { get; init; } = 48;
}
