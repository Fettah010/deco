using System.Text.Json;
using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class StorePricingTests
{
    [Theory]
    [InlineData("FR=Tier1012")]
    [InlineData("de=Free")]
    [InlineData("US=NotAvailable")]
    public void ParseMarketPrices_AcceptsShapes(string assignment)
    {
        var map = StorePricing.ParseMarketPrices([assignment]);
        Assert.Single(map);
    }

    [Theory]
    [InlineData("France=Tier1012")]
    [InlineData("F=Tier1012")]
    [InlineData("FR=2.99")]
    [InlineData("FR=")]
    [InlineData("FR")]
    public void ParseMarketPrices_RejectsShapes(string assignment)
    {
        Assert.Throws<Core.DecoException>(() => StorePricing.ParseMarketPrices([assignment]));
    }

    [Fact]
    public void MergePricing_OverridesMarkets_KeepsRest()
    {
        using var doc = JsonDocument.Parse(
            "{\"TrialPeriod\":\"NoFreeTrial\"," +
            "\"MarketSpecificPricings\":{\"LB\":\"NotAvailable\",\"FR\":\"Tier1001\"}," +
            "\"PriceId\":\"Base\"}");
        string merged = StorePricing.MergePricing(
            doc.RootElement,
            new Dictionary<string, string> { ["fr"] = "Tier1012" });
        using var back = JsonDocument.Parse(merged);
        Assert.Equal("Base", back.RootElement.GetProperty("PriceId").GetString());
        Assert.Equal("Tier1012", back.RootElement.GetProperty("MarketSpecificPricings").GetProperty("FR").GetString());
        Assert.Equal("NotAvailable", back.RootElement.GetProperty("MarketSpecificPricings").GetProperty("LB").GetString());
    }

    [Fact]
    public void MergePricing_BaseTier_Validated()
    {
        using var doc = JsonDocument.Parse("{\"TrialPeriod\":\"NoFreeTrial\",\"PriceId\":\"Base\"}");
        string merged = StorePricing.MergePricing(
            doc.RootElement,
            new Dictionary<string, string>(),
            baseTier: "Tier1012");
        using var back = JsonDocument.Parse(merged);
        Assert.Equal("Tier1012", back.RootElement.GetProperty("PriceId").GetString());
        Assert.Throws<Core.DecoException>(() => StorePricing.MergePricing(
            doc.RootElement, new Dictionary<string, string>(), baseTier: "2.99"));
    }

    [Fact]
    public void ApplyPricing_ReplacesNode_Only()
    {
        using var doc = JsonDocument.Parse(
            "{\"Id\":\"1\",\"Pricing\":{\"PriceId\":\"Base\"},\"Listings\":{}}");
        string updated = StorePricing.ApplyPricing(
            doc.RootElement, "{\"TrialPeriod\":\"NoFreeTrial\",\"PriceId\":\"Tier1012\"}");
        using var back = JsonDocument.Parse(updated);
        Assert.Equal("Tier1012", back.RootElement.GetProperty("Pricing").GetProperty("PriceId").GetString());
        Assert.Equal("1", back.RootElement.GetProperty("Id").GetString());
    }
}
