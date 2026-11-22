using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VoicesIntegration.Infrastructure.Persistence;

// De [data] solo se guarda el ID del cliente; si no se reconoce ningún campo se guarda el JSON completo
internal static class DatoCliente
{
    private const int MaxLargo = 100;

    private static readonly string[] Prefijos = ["ID", "NUM", "NUMERO", "NO", "NRO", "CVE", "CLAVE"];
    private static readonly string[] Objetos = ["UNICO", "CLIENTE", "CTE", "CLTE", "CUENTA", "CTA"];

    private static readonly HashSet<string> Nombres = CrearNombres();

    private static HashSet<string> CrearNombres()
    {
        var s = new HashSet<string>(StringComparer.Ordinal)
        {
            "CLIENTEUNICO", "UNICOCLIENTE", "CTEUNICO", "CLTEUNICO",
            "IDCLIENTEUNICO", "IDUNICOCLIENTE", "CLIENTEUNICOID", "NUMCLIENTEUNICO",
        };
        foreach (var p in Prefijos)
            foreach (var o in Objetos) { s.Add(p + o); s.Add(o + p); s.Add(p + "DE" + o); s.Add(p + "DEL" + o); }
        return s;
    }

    public static string? Reducir(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return data;
        try
        {
            using var doc = JsonDocument.Parse(data);
            return Reducir(doc.RootElement) ?? data;
        }
        catch (JsonException) { return data; }
    }

    // null = no trae ID reconocible
    public static string? Reducir(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        string? mejor = null;
        var mejorPrioridad = int.MaxValue;
        foreach (var p in data.EnumerateObject())
        {
            var nombre = Normalizar(p.Name);
            if (!Nombres.Contains(nombre)) continue;
            var valor = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()?.Trim(),
                JsonValueKind.Number => p.Value.GetRawText(),
                _ => null,
            };
            if (string.IsNullOrEmpty(valor) || valor.Length > MaxLargo) continue;
            var prioridad = nombre.Contains("UNICO") ? 0 : nombre.Contains("CUENTA") || nombre.Contains("CTA") ? 2 : 1;
            if (prioridad < mejorPrioridad) { mejor = valor; mejorPrioridad = prioridad; }
        }
        return mejor;
    }

    // "Id Cliente" / "id_cliente" / "IDCLIENTE" / "Clave-Cliente" -> "IDCLIENTE"
    internal static string Normalizar(string nombre)
    {
        var sb = new StringBuilder(nombre.Length);
        foreach (var ch in nombre.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }
}
