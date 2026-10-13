using System.CommandLine;
using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class CompletionsTests
{
    private static Command SampleTree()
    {
        var root = new Command("deco");
        var store = new Command("store", "Store workflows.");
        var portalAuth = new Command("portal-auth", "Login session.");
        portalAuth.Options.Add(new Option<bool>("--logout") { Description = "Wipe." });
        store.Subcommands.Add(portalAuth);
        root.Subcommands.Add(store);
        root.Subcommands.Add(new Command("info", "Show."));
        return root;
    }

    [Fact]
    public void BuildEntries_FlattensTree_WithOptions()
    {
        var entries = CompletionsService.BuildEntries(SampleTree());
        Assert.Contains(entries, e => e.Path == "store portal-auth"
            && e.Options.Contains("--logout"));
        Assert.Contains(entries, e => e.Path == "info");
        Assert.Contains(entries, e => e.Path == "store");
        Assert.DoesNotContain(entries.SelectMany(e => e.Options), o => o.StartsWith("----"));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    [InlineData("zsh")]
    public void Emit_CoversAllShells_WithCommandPaths(string shell)
    {
        string script = CompletionsService.Emit(shell, "deco", CompletionsService.BuildEntries(SampleTree()));
        // zsh lists bare paths under `#compdef deco`; others prefix the tool name.
        Assert.Contains(shell == "zsh" ? "store portal-auth" : "deco store portal-auth", script);
        Assert.DoesNotContain("__complete", script);
    }

    [Fact]
    public void Emit_RejectsUnknownShell()
    {
        Assert.Throws<Core.DecoException>(() =>
            CompletionsService.Emit("fish", "deco", CompletionsService.BuildEntries(SampleTree())));
    }
}
