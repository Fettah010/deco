using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class PortalContentTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _source;

    public PortalContentTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-portal-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_tmp, "ListingSource");
        Directory.CreateDirectory(_source);
        var texts = new Dictionary<string, (string Desc, string Notes)>
        {
            ["en-US"] = ("Fast app that does things.", "Bug fixes."),
            ["es-ES"] = ("Aplicación rápida.", "Correcciones."),
        };
        foreach ((string locale, var t) in texts)
        {
            Directory.CreateDirectory(Path.Combine(_source, locale));
            File.WriteAllText(Path.Combine(_source, locale, "listing.json"),
                $$"""{"description":"{{t.Desc}}","shortDescription":"Short","productFeatures":[{"text":"F1"}]}""");
            string nd = Path.Combine(_source, "release-notes", "1.0.0");
            Directory.CreateDirectory(nd);
            File.WriteAllText(Path.Combine(nd, locale + ".json"),
                $$"""{"whatsNew":"{{t.Notes}}"}""");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void ResolveLocaleContent_PerLocale_WithEnUsOverride()
    {
        var content = PortalContent.ResolveLocaleContent(
            _source, "1.0.0", ["en-US", "es-ES"], "OVERRIDE", "", certifiedConfirmed: false);
        Assert.Equal("OVERRIDE", content["en-US"].Description);
        Assert.Equal("Bug fixes.", content["en-US"].Notes);
        Assert.Equal("Aplicación rápida.", content["es-ES"].Description);
    }

    [Fact]
    public void ResolveLocaleContent_CertifiedGate()
    {
        File.WriteAllText(Path.Combine(_source, "en-US", "listing.json"),
            """{"description":"Certified fast app.","shortDescription":"S","productFeatures":[]}""");
        Assert.Throws<Core.DecoException>(() => PortalContent.ResolveLocaleContent(
            _source, "1.0.0", ["en-US", "es-ES"], "", "", certifiedConfirmed: false));
        var ok = PortalContent.ResolveLocaleContent(
            _source, "1.0.0", ["en-US", "es-ES"], "", "", certifiedConfirmed: true);
        Assert.Contains("Certified", ok["en-US"].Description);
    }

    [Fact]
    public void DiscoverShots_MatrixLeafAndSiblings()
    {
        foreach (string loc in new[] { "en-US", "es-ES" })
        {
            string dir = Path.Combine(_tmp, "matrix", loc, "desktop");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "01-a.png"), [1]);
        }

        var found = PortalContent.DiscoverShots(Path.Combine(_tmp, "matrix"), ["en-US", "es-ES"]);
        Assert.Single(found["en-US"]);
        Assert.Single(found["es-ES"]);
        var warns = new List<string>();
        var partial = PortalContent.DiscoverShots(Path.Combine(_tmp, "matrix"), ["en-US", "es-ES", "fr-FR"], warns.Add);
        Assert.False(partial.ContainsKey("fr-FR"));
        Assert.Contains(warns, w => w.Contains("fr-FR"));
    }

    [Fact]
    public void PriceSnapshot_Canonical_OrderIndependent()
    {
        var a = new PortalContent.PriceSnapshot("Free", [("Zed", "$1"), ("Alpha", "$2")]);
        var b = new PortalContent.PriceSnapshot("Free", [("Alpha", "$2"), ("Zed", "$1")]);
        Assert.Equal(a.Canonical(), b.Canonical());
        var c = new PortalContent.PriceSnapshot("Tier1", [("Alpha", "$2")]);
        Assert.NotEqual(a.Canonical(), c.Canonical());
    }
}
