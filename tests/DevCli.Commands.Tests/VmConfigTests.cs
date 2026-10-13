using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class VmConfigTests
{
    [Theory]
    [InlineData("--vm-flag", "manifest-vm", "--vm-flag")]
    [InlineData("", "manifest-vm", "manifest-vm")]
    [InlineData("  ", "manifest-vm", "manifest-vm")]
    [InlineData(null, "manifest-vm", "manifest-vm")]
    [InlineData("", "", "Formixa-Capture")]
    [InlineData(null, null, "Formixa-Capture")]
    [InlineData("  spaced  ", null, "spaced")]
    public void ResolveName_FlagBeatsManifestBeatsLegacy(string? flag, string? manifest, string expected)
    {
        Assert.Equal(expected, VmConfig.ResolveName(flag, manifest));
    }

    [Theory]
    [InlineData("--user-flag", "manifest-user", "--user-flag")]
    [InlineData(null, "manifest-user", "manifest-user")]
    [InlineData(null, null, "cap")]
    public void ResolveUser_FlagBeatsManifestBeatsLegacy(string? flag, string? manifest, string expected)
    {
        Assert.Equal(expected, VmConfig.ResolveUser(flag, manifest));
    }

    [Theory]
    [InlineData("flag", null, false)]
    [InlineData(null, "manifest", false)]
    [InlineData(null, null, true)]
    [InlineData("", "  ", true)]
    public void UsedLegacyFallback_OnlyWhenNothingPinned(string? flag, string? manifest, bool expected)
    {
        Assert.Equal(expected, VmConfig.UsedLegacyFallback(flag, manifest));
    }
}
