using DevCli.Commands;
using DevCli.Core;

namespace DevCli.Commands.Tests;

public sealed class ListingGeneratorTests
{
    [Fact]
    public void ParseReadme_ExtractsTitleBlurbBullets_SkipsFences()
    {
        string md = "# Acme Desk\n\nFast local-first desk.\n\nNo cloud needed.\n\nRuns offline always.\n\n```\n# not a title\n- not a bullet\n```\n\n- Syncs offline\n- Dark mode\n";
        var facts = ListingGenerator.ParseReadme(md);
        Assert.Equal("Acme Desk", facts.Title);
        Assert.Equal(3, facts.Blurb.Count);
        Assert.Equal(["Syncs offline", "Dark mode"], facts.Bullets);
    }

    [Fact]
    public void ParseReadme_FallsBackToMyApp()
    {
        var facts = ListingGenerator.ParseReadme("no headings here\n");
        Assert.Equal("My App", facts.Title);
    }

    [Fact]
    public void BuildDescription_ContainsLimitNote()
    {
        var facts = new ListingGenerator.ReadmeFacts("T", ["b1"], ["f1"]);
        string desc = ListingGenerator.BuildDescription(facts);
        Assert.Contains("T", desc);
        Assert.Contains("- f1", desc);
        Assert.Contains("10000 characters", desc);
    }
}

public sealed class ProtocolPlanTests
{
    [Theory]
    [InlineData("formixa://", "formixa")]
    [InlineData("  acme:// ", "acme")]
    [InlineData("bare", "bare")]
    public void NormalizeScheme_TrimsSeparators(string input, string expected)
    {
        Assert.Equal(expected, ProtocolPlan.NormalizeScheme(input));
    }

    [Fact]
    public void NormalizeScheme_RejectsEmpty()
    {
        Assert.Throws<DecoException>(() => ProtocolPlan.NormalizeScheme("://"));
    }

    [Fact]
    public void Plan_Register_HasFourActions()
    {
        var actions = ProtocolPlan.Plan("formixa://", @"C:\a\Formixa.exe", unregister: false);
        Assert.Equal(4, actions.Count);
        Assert.Contains(actions, a => a.Key.EndsWith(@"\shell\open\command"));
    }

    [Fact]
    public void Plan_Unregister_HasDeleteMarker()
    {
        var actions = ProtocolPlan.Plan("formixa://", string.Empty, unregister: true);
        Assert.Single(actions);
        Assert.Equal("<DELETE KEY>", actions[0].Value);
    }

    [Fact]
    public void Plan_Register_RequiresExe()
    {
        Assert.Throws<DecoException>(() => ProtocolPlan.Plan("x://", string.Empty, unregister: false));
    }
}
