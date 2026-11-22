namespace VoicesIntegration.Api.Endpoints;

// Modo mantenimiento SOLO de /recuperar: si existe el archivo en la carpeta del sitio, nadie puede subir ni insertar.
// Se prende y apaga con MANTENIMIENTO-PRENDER.bat / MANTENIMIENTO-APAGAR.bat en el .14, sin reiniciar la API.
// El webhook sigue igual.
public static class RecoveryMaintenance
{
    public const string Archivo = "MANTENIMIENTO-RECUPERAR.txt";

    public const string MensajePorDefecto = "Estamos actualizando el sistema. Intenta en otro momento.";

    // null = no hay mantenimiento. Si el archivo trae texto, ese es el mensaje.
    public static string? Motivo(IWebHostEnvironment env)
    {
        var ruta = Path.Combine(env.ContentRootPath, Archivo);
        if (!File.Exists(ruta)) return null;
        try
        {
            var texto = File.ReadAllText(ruta).Trim();
            return string.IsNullOrEmpty(texto) ? MensajePorDefecto : texto.Length <= 500 ? texto : texto[..500];
        }
        catch (IOException) { return MensajePorDefecto; }
        catch (UnauthorizedAccessException) { return MensajePorDefecto; }
    }

    public static IResult Rechazo(string motivo) =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recuperación en mantenimiento", detail: motivo);
}
