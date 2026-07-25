using SmartSwitch.Core.Models;

namespace SmartSwitch.Core.Tests;

public sealed class PairingCodeTests
{
    [Theory]
    [InlineData("12345678", "1234-5678")]
    [InlineData("1234-5678", "1234-5678")]
    [InlineData("1234 5678", "1234-5678")]
    public void TryParseAcceptsSupportedFormatting(string input, string expectedDisplay)
    {
        var parsed = PairingCode.TryParse(input, out var code);

        Assert.True(parsed);
        Assert.Equal(expectedDisplay, code.DisplayValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("abcdefgh")]
    public void TryParseRejectsInvalidCodes(string input)
    {
        Assert.False(PairingCode.TryParse(input, out _));
    }

    [Fact]
    public void GenerateReturnsEightDigits()
    {
        var code = PairingCode.Generate();

        Assert.Equal(PairingCode.DigitCount, code.Value.Length);
        Assert.All(code.Value, character => Assert.True(char.IsAsciiDigit(character)));
    }
}
