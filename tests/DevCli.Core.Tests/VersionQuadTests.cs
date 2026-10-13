using DevCli.Core;

namespace DevCli.Core.Tests;

public sealed class VersionQuadTests
{
    [Theory]
    [InlineData("0.7.0", "0.7.0.0")]
    [InlineData("0.7.0-beta", "0.7.0.0")]
    [InlineData("1.0.1", "1.0.1.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("v2.0.0", "2.0.0.0")]
    public void ToMsixQuad_MapsToFourParts(string input, string expected)
    {
        Assert.Equal(expected, VersionQuad.ToMsixQuad(input));
    }

    [Theory]
    [InlineData("1.2.3.65535")]
    [InlineData("1.2.3.99999")]
    [InlineData("1.2.3.4.5")]
    [InlineData("")]
    public void ToMsixQuad_RejectsInvalid(string input)
    {
        Assert.Throws<DecoException>(() => VersionQuad.ToMsixQuad(input));
    }
}
