using DevCli.Commands;
using Spectre.Console;

namespace DevCli.Commands.Tests;

/// <summary>
/// Regression tests for the 2026-10-11 crash ("Could not find color or
/// style '8m'" / "'00:29:38Z'"): dynamic text sat inside [...] and Spectre
/// parsed it as a style. These pin exact markup for bracket-heavy inputs.
/// </summary>
public sealed class CertWatchLinesTests
{
    [Fact]
    public void CachedMarkup_NeverPutsDynamicTextInBrackets()
    {
        string markup = StoreCertWatchLines.CachedMarkup(
            "8m ago", "[cyan]9ABCDEF12345[/]", "[green]Published[/]");
        Assert.Equal(
            "[dim]Last checked 8m ago:[/] [cyan]9ABCDEF12345[/]: [green]Published[/] — checking live...",
            markup);
    }

    [Fact]
    public void PollMarkup_NeverPutsDynamicTextInBrackets()
    {
        string markup = StoreCertWatchLines.PollMarkup(
            "00:29:38Z", "[cyan]9ABCDEF12345[/]", "[yellow]Certification[/]");
        Assert.Equal(
            "[dim]00:29:38Z:[/] [cyan]9ABCDEF12345[/]: [yellow]Certification[/]",
            markup);
    }

    [Fact]
    public void CachedMarkup_EscapesBracketedInput()
    {
        string markup = StoreCertWatchLines.CachedMarkup(
            "weird [x] age", "[cyan]id[/]", "[green]Published[/]");
        Assert.Contains("weird [[x]] age", markup);
    }

    [Fact]
    public void ComposedMarkup_RendersInSpectre_WithoutThrowing()
    {
        var sw = new System.IO.StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(
            new Spectre.Console.AnsiConsoleSettings
            {
                Out = new Spectre.Console.AnsiConsoleOutput(sw),
                Ansi = Spectre.Console.AnsiSupport.Yes,
            });
        var ex = Record.Exception(() =>
        {
            console.MarkupLine(StoreCertWatchLines.CachedMarkup(
                "8m ago", "[cyan]9ABCDEF12345[/]", "[green]Published[/]"));
            console.MarkupLine(StoreCertWatchLines.PollMarkup(
                "00:29:38Z", "[cyan]9ABCDEF12345[/]", "[yellow]Certification[/]"));
            console.MarkupLine("  [dim]HKCU key [[ValueName]] = data[/]");
        });
        Assert.Null(ex);
        string text = sw.ToString() ?? string.Empty;
        Assert.Contains("8m ago", text);
        Assert.Contains("00:29:38Z", text);
        Assert.Contains("[ValueName]", text);
    }
}
