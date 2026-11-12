using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Tests.Domain;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("5544332211")]
    [InlineData("  5544332211  ")]
    public void Create_ConDiezDigitos_EsValido(string input)
    {
        var phone = PhoneNumber.Create(input);

        Assert.Equal("5544332211", phone.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("554433221")]       // 9 dígitos
    [InlineData("55443322110")]     // 11 dígitos
    [InlineData("55443322aa")]      // letras
    [InlineData("+525544332211")]   // con lada internacional
    public void Create_Invalido_LanzaDomainException(string? input)
    {
        Assert.Throws<DomainException>(() => PhoneNumber.Create(input));
    }

    [Fact]
    public void ToString_DevuelveNumeroEnmascarado()
    {
        var phone = PhoneNumber.Create("5544332211");

        Assert.Equal("******2211", phone.ToString());
    }
}
