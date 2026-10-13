using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Per-market pricing for Store submissions (v0.1.9): merges market→tier
/// entries into a draft's MarketSpecificPricings (base price untouched
/// unless --base-tier is explicit). Pure merge logic here (tested); the
/// GET/modify/PUT orchestration lives in the `store pricing` handler, which
/// talks to Partner Center exclusively through the msstore CLI's cached
/// auth — deco never holds credentials.
/// </summary>
public static partial class StorePricing
{
    [GeneratedRegex(@"^[A-Za-z]{2}$")]
    private static partial Regex MarketCodeRegex();

    [GeneratedRegex(@"^(Tier\d+|Free|NotAvailable)$", RegexOptions.IgnoreCase)]
    private static partial Regex TierRegex();

    /// <summary>Parses MARKET=TIER assignments. Throws DecoException on shape errors.</summary>
    public static IReadOnlyDictionary<string, string> ParseMarketPrices(
        IReadOnlyList<string> assignments, string optionName = "--market-price")
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string a in assignments)
        {
            int eq = a.IndexOf('=');
            string market = eq < 0 ? string.Empty : a[..eq].Trim().ToUpperInvariant();
            string tier = eq < 0 ? string.Empty : a[(eq + 1)..].Trim();
            if (!MarketCodeRegex().IsMatch(market) || !TierRegex().IsMatch(tier))
            {
                throw new Core.DecoException(
                    $"Bad {optionName} '{a}'. Use MARKET=TIER with a 2-letter market and a tier (e.g. FR=Tier1012, DE=Free).",
                    "pricing-market-shape",
                    Core.ExitCodes.UsageOrAuthNeeded);
            }

            map[market] = tier;
        }

        return map;
    }

    /// <summary>
    /// Merges market entries into a pricing JSON element, returning the
    /// updated pricing JSON string. Base tier changes only when
    /// <paramref name="baseTier"/> is non-empty (validated as a tier).
    /// Pure and tested.
    /// </summary>
    public static string MergePricing(
        JsonElement pricing, IReadOnlyDictionary<string, string> markets, string baseTier = "")
    {
        var marketsOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pricing.ValueKind == JsonValueKind.Object
            && pricing.TryGetProperty("MarketSpecificPricings", out JsonElement existing)
            && existing.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in existing.EnumerateObject())
            {
                marketsOut[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? string.Empty : string.Empty;
            }
        }

        foreach ((string m, string t) in markets)
        {
            marketsOut[m.ToUpperInvariant()] = t;
        }

        string trial = pricing.ValueKind == JsonValueKind.Object
            && pricing.TryGetProperty("TrialPeriod", out JsonElement tp)
            && tp.ValueKind == JsonValueKind.String
            ? tp.GetString() ?? "NoFreeTrial" : "NoFreeTrial";
        string priceId = !string.IsNullOrWhiteSpace(baseTier)
            ? baseTier.Trim()
            : pricing.ValueKind == JsonValueKind.Object
                && pricing.TryGetProperty("PriceId", out JsonElement pid)
                && pid.ValueKind == JsonValueKind.String
                ? pid.GetString() ?? string.Empty : string.Empty;
        if (!string.IsNullOrWhiteSpace(baseTier) && !TierRegex().IsMatch(baseTier.Trim()))
        {
            throw new Core.DecoException(
                $"Bad base tier '{baseTier}'. Use a tier (e.g. Tier1012), Free, or NotAvailable.",
                "pricing-tier-shape",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("TrialPeriod", trial);
            w.WriteStartObject("MarketSpecificPricings");
            foreach ((string m, string t) in marketsOut.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WriteString(m, t);
            }

            w.WriteEndObject();
            w.WriteString("PriceId", priceId);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Replaces the Pricing node of a full submission product JSON.
    /// Pure and tested (System.Text.Json.Nodes round-trip).
    /// </summary>
    public static string ApplyPricing(JsonElement product, string pricingJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(product.GetRawText())
            ?? throw new Core.DecoException("Submission JSON has no product object.", "pricing-product");
        node["Pricing"] = System.Text.Json.Nodes.JsonNode.Parse(pricingJson);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
