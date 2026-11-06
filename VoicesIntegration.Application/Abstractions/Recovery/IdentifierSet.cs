namespace VoicesIntegration.Application.Abstractions.Recovery;

/// <summary>
/// Conjunto compacto de identifiers. Los de Voices son GUID (36 caracteres): se guardan como Guid (16 bytes)
/// en vez de texto (~100 bytes), así millones de llaves caben en poca memoria. Los que no sean GUID van como texto.
/// </summary>
public sealed class IdentifierSet
{
    private readonly HashSet<Guid> _guids = [];
    private readonly HashSet<string> _others = new(StringComparer.Ordinal);

    public int Count => _guids.Count + _others.Count;

    public bool Add(string id) => Guid.TryParseExact(id, "D", out var g) ? _guids.Add(g) : _others.Add(id);

    public bool Contains(string id) => Guid.TryParseExact(id, "D", out var g) ? _guids.Contains(g) : _others.Contains(id);

    /// <summary>Quita la llave; true si estaba (sirve para tomar cada faltante UNA sola vez aunque el archivo la repita).</summary>
    public bool Remove(string id) => Guid.TryParseExact(id, "D", out var g) ? _guids.Remove(g) : _others.Remove(id);

    public void Clear() { _guids.Clear(); _others.Clear(); }
}
