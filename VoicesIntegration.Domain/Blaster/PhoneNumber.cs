using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Domain.Blaster;

// Telefono a 10 digitos sin +52
public sealed record PhoneNumber
{
    public const int Length = 10;

    public string Value { get; }

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

    public string Masked => new string('*', Length - 4) + Value[^4..];

    // Enmascarado para que no salga completo en logs
    public override string ToString() => Masked;
}
