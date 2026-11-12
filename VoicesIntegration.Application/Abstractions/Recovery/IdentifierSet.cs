namespace VoicesIntegration.Application.Abstractions.Recovery;

// Guarda los identifier como Guid para ocupar menos memoria
public sealed class IdentifierSet
{
    private readonly HashSet<Guid> _guids = [];
    private readonly HashSet<string> _others = new(StringComparer.Ordinal);

    public int Count => _guids.Count + _others.Count;

    public bool Add(string id) => Guid.TryParseExact(id, "D", out var g) ? _guids.Add(g) : _others.Add(id);

    public bool Contains(string id) => Guid.TryParseExact(id, "D", out var g) ? _guids.Contains(g) : _others.Contains(id);

    public bool Remove(string id) => Guid.TryParseExact(id, "D", out var g) ? _guids.Remove(g) : _others.Remove(id);

    public void Clear() { _guids.Clear(); _others.Clear(); }
}
