using System.Text;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Help coloring (v0.1.6): System.CommandLine owns the help layout; this
/// only tints the captured text — yellow section headers, cyan names/usage,
/// gray explanations. Plain mode returns the text untouched, so pipes, CI
/// logs, and --no-ansi never see an escape code. Pure and tested.
/// </summary>
public static partial class HelpColorizer
{
    private const string Gray = "\u001b[90m";
    private const string Cyan = "\u001b[36m";
    private const string Yellow = "\u001b[33m";
    private const string Reset = "\u001b[0m";

    [GeneratedRegex(@"^(  \S.*?)( {2,})(.*)$")]
    private static partial Regex RowSplit();

    [GeneratedRegex(@"^ {3,}\S")]
    private static partial Regex Continuation();

    /// <summary>Tints captured --help text. Returns input unchanged when plain.</summary>
    public static string Colorize(string helpText, bool noAnsi) =>
        ColorizeCore(helpText, CliConsole.AnsiEnabled(noAnsi));

    /// <summary>Testable core: tint when <paramref name="ansiOn"/>, else echo.</summary>
    public static string ColorizeCore(string helpText, bool ansiOn)
    {
        if (string.IsNullOrEmpty(helpText) || !ansiOn)
        {
            return helpText;
        }

        var sb = new StringBuilder(helpText.Length + 64);
        string section = string.Empty;
        bool first = true;
        foreach (string rawLine in helpText.Replace("\r", "").Split('\n'))
        {
            string line = rawLine;
            if (!first)
            {
                sb.Append('\n');
            }

            first = false;
            string trimmed = line.Trim();
            if (trimmed is "Description:" or "Usage:" or "Options:" or "Commands:" or "Arguments:")
            {
                section = trimmed.TrimEnd(':');
                sb.Append(Yellow).Append(line).Append(Reset);
            }
            else if (section == "Description")
            {
                sb.Append(string.IsNullOrWhiteSpace(line) ? line : Gray + line + Reset);
            }
            else if (section == "Usage")
            {
                sb.Append(string.IsNullOrWhiteSpace(line) ? line : Cyan + line + Reset);
            }
            else if (section is "Options" or "Commands" or "Arguments")
            {
                sb.Append(ColorizeRow(line));
            }
            else
            {
                sb.Append(line);
            }
        }

        return sb.ToString();
    }

    private static string ColorizeRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        Match m = RowSplit().Match(line);
        if (m.Success)
        {
            return $"{Cyan}{m.Groups[1].Value}{Reset}{m.Groups[2].Value}{Gray}{m.Groups[3].Value}{Reset}";
        }

        if (Continuation().IsMatch(line))
        {
            return $"{Gray}{line}{Reset}";
        }

        return $"{Gray}{line}{Reset}";
    }
}
