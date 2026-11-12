using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Tests.Domain;

public class CallResultTests
{

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ConDatosValidos_GuardaValores()
    {
        var result = CallResult.Create("abc-123", CallStatus.Sent, CallType.Initial, Now,
            sipCode: 200, durationSeconds: 35, phoneNumber: "5544332211");

        Assert.Equal("abc-123", result.MessageId);
        Assert.Equal(CallStatus.Sent, result.Status);
        Assert.Equal(200, result.SipCode);
        Assert.Equal(35, result.DurationSeconds);
    }

    

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Create_SinMessageId_LanzaDomainException(string? messageId)
    {
        Assert.Throws<DomainException>(() => CallResult.Create(messageId, CallStatus.Sent, CallType.Initial, Now));
    }

    [Fact]
    public void Create_DuracionNegativa_LanzaDomainException()
    {
        Assert.Throws<DomainException>(() =>
            CallResult.Create("abc", CallStatus.Sent, CallType.Initial, Now, durationSeconds: -1));
    }

    [Fact]
    public void Create_TextoLargo_SeRecortaEnVezDeFallar()
    {
        var result = CallResult.Create("abc", CallStatus.Failed, CallType.Initial, Now,
            sipDescription: new string('x', 500));

        Assert.Equal(CallResult.MaxSipDescriptionLength, result.SipDescription!.Length);
    }
}
