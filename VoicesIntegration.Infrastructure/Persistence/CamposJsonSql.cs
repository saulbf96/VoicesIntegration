using System.Text.Json;

namespace VoicesIntegration.Infrastructure.Persistence;

/// <summary>
/// Saca campos del JSON del aviso con las MISMAS reglas que SQL Server (modo lax), para que el SP V2 guarde
/// exactamente lo mismo que el SP 28 cuando leía el JSON en el 205:
/// - <see cref="Valor"/> = JSON_VALUE(json, '$.campo'): texto del valor simple; NULL si no existe, si es null,
///   si es objeto/arreglo o si el texto pasa de 4,000 caracteres. Números tal cual vienen; true/false como texto.
/// - <see cref="Fragmento"/> = JSON_QUERY(json, '$.campo'): el objeto/arreglo tal cual viene; NULL si es valor simple.
/// Si el nombre se repite en el JSON, se toma el PRIMERO (como SQL).
/// </summary>
internal static class CamposJsonSql
{
    private const int MaxJsonValue = 4000;

    public static string? Valor(JsonElement raiz, string campo)
    {
        if (!Buscar(raiz, campo, out var v)) return null;
        string? texto = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,   // null, objeto, arreglo
        };
        return texto is { Length: > MaxJsonValue } ? null : texto;
    }

    public static string? Fragmento(JsonElement raiz, string campo) =>
        Buscar(raiz, campo, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? v.GetRawText() : null;

    private static bool Buscar(JsonElement raiz, string campo, out JsonElement valor)
    {
        if (raiz.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in raiz.EnumerateObject())
            {
                if (p.NameEquals(campo)) { valor = p.Value; return true; }   // sensible a mayúsculas, el primero
            }
        }
        valor = default;
        return false;
    }
}
