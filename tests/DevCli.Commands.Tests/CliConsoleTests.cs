using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class CliConsoleTests
{
    [Fact]
    public void AnsiEnabled_RespectsNoAnsi_AndNoColor()
    {
        string? saved = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            // Redirected in test runners: only assert the explicit flag path here.
            Assert.False(CliConsole.AnsiEnabled(noAnsi: true));
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Assert.False(CliConsole.AnsiEnabled(noAnsi: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", saved);
        }
    }

    [Fact]
    public void PlainTable_AlignsColumns()
    {
        string text = CliConsole.PlainTable(
            ["App", "State"],
            [["9AAA", "Published"], ["9BBB", "Certification"]]);
        string[] lines = text.Replace("\r", "").Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("App", lines[0]);
        Assert.Contains("State", lines[0]);
        Assert.True(lines[1].Trim().Length > 0);
        Assert.Contains("9AAA", lines[2]);
        Assert.Contains("Certification", lines[3]);
    }

    [Theory]
    [InlineData("Published", true, false)]
    [InlineData("Failed", false, true)]
    [InlineData("Certification", false, false)]
    public void Status_Classification(string status, bool good, bool bad)
    {
        Assert.Equal(good, CliConsole.IsGood(status));
        Assert.Equal(bad, CliConsole.IsBad(status));
        // Plain mode echoes the raw word (machine-safe).
        Assert.Equal(status, CliConsole.StatusWord(status, noAnsi: true));
    }

    [Theory]
    [InlineData("Published (2m ago)")]
    [InlineData("never checked")]
    [InlineData("Certification")]
    public void StateCell_PlainEchoesRaw(string cell)
    {
        Assert.Equal(cell, CliConsole.StateCell(cell, noAnsi: true));
        Assert.Equal(cell, CliConsole.Cyan(cell, noAnsi: true));
    }
}
