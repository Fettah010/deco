using Spectre.Console;

namespace DevCli.Commands;

/// <summary>
/// One visual voice for deco (v0.1.5): Spectre rendering on TTYs, plain
/// aligned text with --no-ansi / redirected output / NO_COLOR, and JSON
/// handled by callers (never through here). Thin by design — callers keep
/// their format/verbosity branching; this only styles the human path.
/// </summary>
public static class CliConsole
{
    /// <summary>ANSI allowed? Explicit --no-ansi, pipes, and NO_COLOR all disable it.</summary>
    public static bool AnsiEnabled(bool noAnsi) =>
        !noAnsi && !Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static void Rule(string title, bool noAnsi)
    {
        if (AnsiEnabled(noAnsi))
        {
            AnsiConsole.Write(new Rule($"[yellow]{Markup.Escape(title)}[/]").LeftJustified());
        }
        else
        {
            Console.WriteLine($"--- {title} ---");
        }
    }

    public static void Success(string message, bool noAnsi)
    {
        if (AnsiEnabled(noAnsi))
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
        }
        else
        {
            Console.WriteLine($"[ok] {message}");
        }
    }

    public static void Fail(string message, bool noAnsi)
    {
        if (AnsiEnabled(noAnsi))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(message)}");
        }
        else
        {
            Console.WriteLine($"[fail] {message}");
        }
    }

    public static void Warn(string message, bool noAnsi)
    {
        if (AnsiEnabled(noAnsi))
        {
            AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(message)}");
        }
        else
        {
            Console.WriteLine($"[warn] {message}");
        }
    }

    public static void Info(string message, bool noAnsi)
    {
        if (AnsiEnabled(noAnsi))
        {
            AnsiConsole.MarkupLine($"[dim]•[/] {Markup.Escape(message)}");
        }
        else
        {
            Console.WriteLine($"  {message}");
        }
    }

    public static void Error(string message)
    {
        if (!Console.IsErrorRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            var stderr = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
            stderr.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
        }
        else
        {
            Console.Error.WriteLine($"Error: {message}");
        }
    }

    /// <summary>Status word in its conventional color (Published green, failed red, rest yellow).</summary>
    public static string StatusWord(string status, bool noAnsi) =>
        AnsiEnabled(noAnsi)
            ? (IsGood(status) ? $"[green]{Markup.Escape(status)}[/]"
                : IsBad(status) ? $"[red]{Markup.Escape(status)}[/]"
                : $"[yellow]{Markup.Escape(status)}[/]")
            : status;

    /// <summary>Cyan emphasis for ids/paths in composed lines. Raw text when plain.</summary>
    public static string Cyan(string text, bool noAnsi) =>
        AnsiEnabled(noAnsi) ? $"[cyan]{Markup.Escape(text)}[/]" : text;

    /// <summary>
    /// One line with full markup on TTY, exact plain text otherwise. Both
    /// renderings are caller-supplied (no tag parsing, ever).
    /// </summary>
    public static void WriteLine(string ansiMarkup, string plainText, bool noAnsi)
    {
        if (AnsiEnabled(noAnsi))
        {
            AnsiConsole.MarkupLine(ansiMarkup);
        }
        else
        {
            Console.WriteLine(plainText);
        }
    }

    /// <summary>
    /// A state cell like "Published (29m ago)" / "never checked": colored by
    /// its leading word. Raw text when plain. Pure and tested.
    /// </summary>
    public static string StateCell(string cell, bool noAnsi)
    {
        if (!AnsiEnabled(noAnsi))
        {
            return cell;
        }

        string first = cell.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (IsGood(first))
        {
            return $"[green]{Markup.Escape(cell)}[/]";
        }

        if (IsBad(first))
        {
            return $"[red]{Markup.Escape(cell)}[/]";
        }

        return string.IsNullOrWhiteSpace(cell) || cell == "never checked"
            ? $"[dim]{Markup.Escape(cell)}[/]"
            : $"[yellow]{Markup.Escape(cell)}[/]";
    }

    public static bool IsGood(string status) =>
        status.Equals("Published", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Passed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Approved", StringComparison.OrdinalIgnoreCase)
        || status.Equals("READY", StringComparison.OrdinalIgnoreCase)
        || status.Equals("ok", StringComparison.OrdinalIgnoreCase);

    public static bool IsBad(string status) =>
        status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Fail", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Fail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Two-column key/value table. Spectre on TTY (yellow headers, cyan
    /// keys), aligned plain text otherwise.
    /// </summary>
    public static void KeyValues(IReadOnlyList<(string Key, string Value)> rows, bool noAnsi, string[]? headers = null)
    {
        Table(
            [headers is not null && headers.Length > 0 ? headers[0] : "Key",
                headers is not null && headers.Length > 1 ? headers[1] : "Value"],
            rows.Select(r => new[] { r.Key, r.Value }).ToList(),
            noAnsi);
    }

    /// <summary>
    /// N-column table with the same styling. Columns listed in
    /// <paramref name="markupColumns"/> arrive pre-marked (e.g. StateCell)
    /// and are not escaped again. Pure layout via PlainTable when plain.
    /// </summary>
    public static void Table(
        IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, bool noAnsi,
        IReadOnlySet<int>? markupColumns = null)
    {
        if (AnsiEnabled(noAnsi))
        {
            var table = new Spectre.Console.Table().Border(TableBorder.Rounded);
            foreach (string h in headers)
            {
                table.AddColumn($"[yellow bold]{Markup.Escape(h)}[/]");
            }

            foreach (string[] row in rows)
            {
                var cells = new string[headers.Count];
                for (int c = 0; c < headers.Count; c++)
                {
                    string text = c < row.Length ? row[c] ?? string.Empty : string.Empty;
                    if (markupColumns is not null && markupColumns.Contains(c))
                    {
                        cells[c] = text;
                    }
                    else
                    {
                        cells[c] = c == 0 ? $"[cyan]{Markup.Escape(text)}[/]" : Markup.Escape(text);
                    }
                }

                table.AddRow(cells);
            }

            AnsiConsole.Write(table);
            return;
        }

        Console.WriteLine(PlainTable(headers, rows));
    }

    /// <summary>Plain-text table renderer (the --no-ansi shape). Pure and tested.</summary>
    public static string PlainTable(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        int cols = headers.Count;
        var widths = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            widths[c] = headers[c].Length;
        }

        foreach (string[] row in rows)
        {
            for (int c = 0; c < cols && c < row.Length; c++)
            {
                widths[c] = Math.Max(widths[c], (row[c] ?? string.Empty).Length);
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join("  ", headers.Select((h, c) => h.PadRight(widths[c]))).TrimEnd());
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', Math.Max(w, 3)))).TrimEnd());
        foreach (string[] row in rows)
        {
            var cells = new string[cols];
            for (int c = 0; c < cols; c++)
            {
                cells[c] = (c < row.Length ? row[c] ?? string.Empty : string.Empty).PadRight(widths[c]);
            }

            sb.AppendLine(string.Join("  ", cells).TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }
}
