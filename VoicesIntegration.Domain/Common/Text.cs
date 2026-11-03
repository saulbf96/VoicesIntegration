namespace VoicesIntegration.Domain.Common;

internal static class Text
{
    /// <summary>Quita espacios y recorta a <paramref name="maxLength"/>. Vacío → null.</summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
