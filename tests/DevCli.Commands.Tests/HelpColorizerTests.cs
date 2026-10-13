using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class HelpColorizerTests
{
    private const string Sample =
        "Description:\n" +
        "  Partner Center login session.\n" +
        "\n" +
        "Usage:\n" +
        "  deco store portal-auth [options]\n" +
        "\n" +
        "Options:\n" +
        "  --profile-dir <profile-dir>  Browser profile dir.\n" +
        "  --logout                     Wipe the profile.\n" +
        "    continued wrapped line\n" +
        "\n" +
        "Commands:\n" +
        "  info  Show context.\n";

    [Fact]
    public void Colorize_PlainPassthrough_WhenNoAnsi()
    {
        Assert.Equal(Sample, HelpColorizer.Colorize(Sample, noAnsi: true));
        Assert.Equal(string.Empty, HelpColorizer.Colorize(string.Empty, noAnsi: false));
    }

    [Fact]
    public void ColorizeCore_TintsSections()
    {
        string colored = HelpColorizer.ColorizeCore(Sample, ansiOn: true);
        Assert.Contains("\u001b[33mDescription:", colored);
        Assert.Contains("\u001b[90m  Partner Center login session.", colored);
        Assert.Contains("\u001b[36m  deco store portal-auth [options]", colored);
        Assert.Contains("\u001b[36m  --profile-dir <profile-dir>", colored);
        Assert.Contains("\u001b[90mBrowser profile dir.", colored);
        Assert.Contains("\u001b[90m    continued wrapped line", colored);
        Assert.Contains("\u001b[36m  info", colored);
        Assert.Contains("\u001b[0m", colored);
    }

    [Fact]
    public void ColorizeCore_PlainPassthrough_WhenOff()
    {
        Assert.Equal(Sample, HelpColorizer.ColorizeCore(Sample, ansiOn: false));
    }
}
