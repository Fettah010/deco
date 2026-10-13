using System.Text.RegularExpressions;

namespace DevCli.Core;

/// <summary>
/// Pure price/currency option matching for the Partner Center base-price
/// controls. Displayed labels carry non-breaking spaces and locale decimal
/// separators ("€  2,99"), so matching is normalized: exact normalized
/// match first, then currency-symbol + digit-signature match. A wanted
/// price WITH a currency symbol never matches a different symbol ("€ 2.99"
/// must not hit "$2.99"); a symbol-less want only matches exactly. No
/// browser, no network — fully unit tested.
/// </summary>
public static class PortalPriceMatch
{
    /// <summary>nbsp → space, collapse all whitespace runs, trim.</summary>
    public static string Normalize(string? s) =>
        Regex.Replace(s ?? string.Empty, @"[\s ]+", " ").Trim();

    /// <summary>Only the digits ("€  2,99" → "299").</summary>
    public static string DigitSignature(string? s) =>
        new((s ?? string.Empty).Where(char.IsDigit).ToArray());

    /// <summary>Non-digit, non-separator chars ("€ 2.99" → "€", "Free" → "Free").</summary>
    public static string SymbolSignature(string? s) =>
        new((s ?? string.Empty).Where(c => !char.IsDigit(c) && !char.IsWhiteSpace(c) && c != '.' && c != ',').ToArray());

    /// <summary>Exact normalized match, else symbol-subset + digit match. Returns the original option text.</summary>
    public static string? TryMatchPrice(IEnumerable<string> options, string want)
    {
        string wantNorm = Normalize(want);
        var list = options.ToList();
        string? exact = list.FirstOrDefault(o =>
            string.Equals(Normalize(o), wantNorm, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        string wantDigits = DigitSignature(wantNorm);
        string wantSymbols = SymbolSignature(wantNorm);
        if (wantDigits.Length == 0 || wantSymbols.Length == 0)
        {
            return null;
        }

        foreach (string o in list)
        {
            string oNorm = Normalize(o);
            if (!string.Equals(DigitSignature(oNorm), wantDigits, StringComparison.Ordinal))
            {
                continue;
            }

            string oSymbols = SymbolSignature(oNorm);
            if (wantSymbols.All(s => oSymbols.Contains(s, StringComparison.Ordinal)))
            {
                return o;
            }
        }

        return null;
    }

    /// <summary>Exact normalized match (case-insensitive), else contains match. Returns the original option text.</summary>
    public static string? TryMatchCurrency(IEnumerable<string> options, string want)
    {
        string wantNorm = Normalize(want);
        var list = options.ToList();
        string? exact = list.FirstOrDefault(o =>
            string.Equals(Normalize(o), wantNorm, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return list.FirstOrDefault(o =>
            Normalize(o).Contains(wantNorm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Matches a wanted price against raw option texts shown with a separate
    /// symbol prefix (the portal renders "$" + "2.99"). Returns the RAW option
    /// text to click (prefix excluded — the click target shows the raw text).
    /// </summary>
    public static string? TryMatchComposedPrice(IEnumerable<string> rawOptions, string prefix, string want)
    {
        var raws = rawOptions.ToList();
        var composed = raws.Select(r => Normalize(prefix + " " + r)).ToList();
        string wantNorm = Normalize(want);
        for (int i = 0; i < raws.Count; i++)
        {
            if (string.Equals(composed[i], wantNorm, StringComparison.OrdinalIgnoreCase))
            {
                return raws[i];
            }
        }

        string? hit = TryMatchPrice(composed, want);
        if (hit is null)
        {
            return null;
        }

        int at = composed.IndexOf(hit);
        return at >= 0 ? raws[at] : null;
    }

    /// <summary>Swaps dot/comma separators ("2.99" ↔ "2,99") for locale filters.</summary>
    public static string SwapSeparators(string s)
    {
        const char tmp = '\u0001';
        return Normalize(s).Replace('.', tmp).Replace(',', '.').Replace(tmp, ',');
    }

    /// <summary>
    /// Builds a whitespace-tolerant regex pattern for a normalized option
    /// label, so Playwright text matching hits nbsp variants too.
    /// </summary>
    public static string OptionTextPattern(string normalizedText)
    {
        string[] parts = Normalize(normalizedText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(@"\s+", parts.Select(p => Regex.Escape(p)));
    }

    public sealed record TargetValidation(bool Ok, bool FreeShaped, string Error);

    /// <summary>Validates an apply target. Free-shaped prices are flagged (caller gates on --allow-free).</summary>
    public static TargetValidation ValidateTarget(string currency, string price)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return new TargetValidation(false, false, "Currency is required (e.g. --currency \"EUR - France\").");
        }

        if (string.IsNullOrWhiteSpace(price))
        {
            return new TargetValidation(false, false, "Price is required (e.g. --price \"€ 2.99\").");
        }

        string norm = Normalize(price);
        bool free = string.Equals(norm, "Free", StringComparison.OrdinalIgnoreCase);
        if (free)
        {
            return new TargetValidation(true, true, string.Empty);
        }

        if (norm.Any(char.IsDigit) == false)
        {
            return new TargetValidation(false, false, $"Price '{price}' has no digits — refusing to set a non-price.");
        }

        return new TargetValidation(true, false, string.Empty);
    }
}
