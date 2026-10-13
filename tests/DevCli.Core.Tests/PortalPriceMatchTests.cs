using DevCli.Core;

namespace DevCli.Core.Tests;

public sealed class PortalPriceMatchTests
{
    [Theory]
    [InlineData("€  2.99", "€ 2.99")]
    [InlineData("  £1.79\n", "£1.79")]
    [InlineData("EUR - France", "EUR - France")]
    public void Normalize_CollapsesWhitespace(string raw, string expected)
    {
        Assert.Equal(expected, PortalPriceMatch.Normalize(raw));
    }

    [Fact]
    public void TryMatchPrice_ExactNormalized_Wins()
    {
        string? hit = PortalPriceMatch.TryMatchPrice(["$ 1.99", "€  2.99", "£ 1.79"], "€ 2.99");
        Assert.Equal("€  2.99", hit);
    }

    [Fact]
    public void TryMatchPrice_CommaDecimal_MatchesDigits()
    {
        string? hit = PortalPriceMatch.TryMatchPrice(["€  2,99"], "€ 2.99");
        Assert.Equal("€  2,99", hit);
    }

    [Fact]
    public void TryMatchPrice_WrongSymbol_NeverMatches()
    {
        Assert.Null(PortalPriceMatch.TryMatchPrice(["$ 2.99"], "€ 2.99"));
        Assert.Null(PortalPriceMatch.TryMatchPrice(["£ 2.99"], "€ 2.99"));
    }

    [Fact]
    public void TryMatchPrice_SymbolLessWant_OnlyExact()
    {
        Assert.Null(PortalPriceMatch.TryMatchPrice(["€ 2.99"], "2.99"));
        Assert.Equal("2.99", PortalPriceMatch.TryMatchPrice(["2.99"], "2.99"));
    }

    [Fact]
    public void TryMatchPrice_NoDigits_NoMatch()
    {
        Assert.Null(PortalPriceMatch.TryMatchPrice(["Free"], "€ 2.99"));
    }

    [Theory]
    [InlineData("EUR - France", "EUR - France")]
    [InlineData("eur - france", "EUR - France")]
    [InlineData("France", "EUR - France")]
    public void TryMatchCurrency_Matches(string want, string expected)
    {
        string? hit = PortalPriceMatch.TryMatchCurrency(
            ["USD - United States", "EUR - France"], want);
        Assert.Equal(expected, hit);
    }

    [Fact]
    public void TryMatchComposedPrice_ReturnsRawText()
    {
        // Portal renders prefix "$" + raw "2.99"; click target is the raw text.
        string? hit = PortalPriceMatch.TryMatchComposedPrice(["0", "1.99", "2.99"], "$", "$ 2.99");
        Assert.Equal("2.99", hit);
    }

    [Fact]
    public void TryMatchComposedPrice_WrongSymbol_NoMatch()
    {
        Assert.Null(PortalPriceMatch.TryMatchComposedPrice(["2.99"], "$", "€ 2.99"));
    }

    [Theory]
    [InlineData("2.99", "2,99")]
    [InlineData("2,99", "2.99")]
    [InlineData("1.099,99", "1,099.99")]
    public void SwapSeparators_Swaps(string input, string expected)
    {
        Assert.Equal(expected, PortalPriceMatch.SwapSeparators(input));
    }

    [Fact]
    public void OptionTextPattern_MatchesNbspVariants()
    {
        string pattern = PortalPriceMatch.OptionTextPattern("€ 2,99");
        Assert.Matches(pattern, "€  2,99");
        Assert.Matches(pattern, "€ 2,99");
        Assert.DoesNotMatch(pattern, "$ 2,99");
    }

    [Fact]
    public void ValidateTarget_FlagsFree()
    {
        var ok = PortalPriceMatch.ValidateTarget("EUR - France", "€ 2.99");
        Assert.True(ok.Ok);
        Assert.False(ok.FreeShaped);
        var free = PortalPriceMatch.ValidateTarget("EUR - France", "Free");
        Assert.True(free.Ok);
        Assert.True(free.FreeShaped);
    }

    [Theory]
    [InlineData("", "€ 2.99")]
    [InlineData("EUR - France", "")]
    [InlineData("EUR - France", "priceless")]
    public void ValidateTarget_RejectsNonsense(string currency, string price)
    {
        Assert.False(PortalPriceMatch.ValidateTarget(currency, price).Ok);
    }
}
