using Spectre.Console;

namespace DevCli.Commands;

/// <summary>
/// cert-watch line builders (v0.1.9): the ONLY place dynamic values meet
/// Spectre markup for these lines. Dynamic text NEVER sits inside [...]
/// (that crashes the parser: "Could not find color or style"). Values that
/// arrive pre-marked (Cyan/StateCell) nest as complete tags. Exact-output
/// tested, including bracket-heavy inputs.
/// </summary>
public static class StoreCertWatchLines
{
    /// <summary>
    /// "Last checked …" line. Plain twin (no markup) is built at the call
    /// site; only the markup shape is centralized here.
    /// </summary>
    public static string CachedMarkup(string ageLabel, string appMarkup, string stateMarkup) =>
        $"[dim]Last checked {Markup.Escape(ageLabel)}:[/] {appMarkup}: {stateMarkup} — checking live...";

    public static string PollMarkup(string stamp, string appMarkup, string statusMarkup) =>
        $"[dim]{Markup.Escape(stamp)}:[/] {appMarkup}: {statusMarkup}";
}
