using VoicesIntegration.Application.Features.Webhooks.CallResult;
using VoicesIntegration.Domain.Blaster;
using VoicesIntegration.Domain.Common;

namespace VoicesIntegration.Tests.Application;

public class BlasterValueParserTests
{
    [Theory]
    [InlineData("SENT", CallStatus.Sent)]
    [InlineData("to_redial", CallStatus.ToRedial)]
    [InlineData(" FAILED ", CallStatus.Failed)]
    public void ParseStatus_ValoresConocidos(string input, CallStatus expected)
        => Assert.Equal(expected, BlasterValueParser.ParseStatus(input));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ParseStatus_SinEstado_LanzaDomainException(string? input)
        => Assert.Throws<DomainException>(() => BlasterValueParser.ParseStatus(input));

    [Theory]
    [InlineData("QUEUED_PROVIDER")]
    [InlineData("ANSWERED")]
    public void ParseStatus_EstadoNuevo_NoSeRechaza(string input)
        => Assert.Equal(CallStatus.Other, BlasterValueParser.ParseStatus(input));

    [Theory]
    [InlineData(null, CallType.Initial)]
    [InlineData("initial", CallType.Initial)]
    [InlineData("TRANSFER", CallType.Transfer)]
    public void ParseCallType_ValoresConocidos(string? input, CallType expected)
        => Assert.Equal(expected, BlasterValueParser.ParseCallType(input));
}
