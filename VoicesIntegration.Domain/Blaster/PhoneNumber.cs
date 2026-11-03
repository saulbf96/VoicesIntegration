using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Domain.Blaster;

/// <summary>
/// Teléfono de México a 10 dígitos (sin +52), como lo pide Blaster.
/// Es un "value object": solo puede existir si es válido.
/// </summary>
public sealed record PhoneNumber
{
    public const int Length = 10;

    public string Value { get; }

    // Constructor privado: la única forma de crear uno es con Create(),
    // así nunca existe un PhoneNumber inválido.
    private PhoneNumber(string value) => Value = value;

    public static PhoneNumber Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("El teléfono es obligatorio.");

        var trimmed = value.Trim();

        if (trimmed.Length != Length || !trimmed.All(char.IsAsciiDigit))
            throw new DomainException($"El teléfono debe tener exactamente {Length} dígitos.");

        return new PhoneNumber(trimmed);
    }

    /// <summary>Versión enmascarada para logs: ******1234.</summary>
    public string Masked => new string('*', Length - 4) + Value[^4..];

    // Por seguridad, si se imprime por accidente (logs) sale enmascarado.
    public override string ToString() => Masked;
}
