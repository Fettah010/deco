using System.Text.Json;
using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class StoreGuardrailTests
{
    private static StoreListingBundle.LocaleEntry Entry(string locale, string description)
    {
        using var doc = JsonDocument.Parse(
            "{\"description\":" + JsonSerializer.Serialize(description) + "}");
        using var notes = JsonDocument.Parse("{}");
        return new StoreListingBundle.LocaleEntry(locale, doc.RootElement.Clone(), notes.RootElement.Clone());
    }

    [Theory]
    [InlineData("en-US", "Read docs. New in 0.9.0: stuff.", true)]
    [InlineData("es-ES", "Lee. Novedad en 0.9.0: cosas.", true)]
    [InlineData("fr-FR", "Lisez. Nouveau en 0.9.0 : choses.", true)]
    [InlineData("en-US", "Read docs. Everything runs locally.", false)]
    [InlineData("en-US", "", false)]
    public void FindEmbeddedReleaseNotes_FlagsVersionedBlurbs(string locale, string desc, bool expected)
    {
        var hit = StoreListingBundle.FindEmbeddedReleaseNotes([Entry(locale, desc)]);
        Assert.Equal(expected ? 1 : 0, hit.Count);
        if (expected)
        {
            Assert.Equal(locale, hit[0]);
        }
    }

    [Fact]
    public void FindDuplicateImages_GroupsBySha()
    {
        using var doc = JsonDocument.Parse(
            "{\"images\":[" +
            "{\"locale\":\"en-US\",\"fileName\":\"01-home.png\",\"sha256\":\"aaa\"}," +
            "{\"locale\":\"en-US\",\"fileName\":\"02-studio.png\",\"sha256\":\"bbb\"}," +
            "{\"locale\":\"es-ES\",\"fileName\":\"01-home.png\",\"sha256\":\"aaa\"}," +
            "{\"locale\":\"fr-FR\",\"fileName\":\"01-home.png\",\"sha256\":\"ccc\"}" +
            "]}");
        var dups = StoreShots.FindDuplicateImages(doc.RootElement);
        var single = Assert.Single(dups);
        Assert.Equal(2, single.Count);
        Assert.Contains("en-US/01-home.png", single);
        Assert.Contains("es-ES/01-home.png", single);
    }

    [Fact]
    public void FindDuplicateImages_Empty_WhenClean()
    {
        using var doc = JsonDocument.Parse(
            "{\"images\":[" +
            "{\"locale\":\"en-US\",\"fileName\":\"01-home.png\",\"sha256\":\"aaa\"}," +
            "{\"locale\":\"en-US\",\"fileName\":\"02-studio.png\",\"sha256\":\"bbb\"}" +
            "]}");
        Assert.Empty(StoreShots.FindDuplicateImages(doc.RootElement));
        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(StoreShots.FindDuplicateImages(empty.RootElement));
    }
}
