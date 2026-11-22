using System.Text.Json;

namespace VoicesIntegration.Infrastructure.Persistence;

// Lee campos del JSON igual que JSON_VALUE de SQL (modo lax)
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
            _ => null,
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
                if (p.NameEquals(campo)) { valor = p.Value; return true; }
            }
        }
        valor = default;
        return false;
    }
}
