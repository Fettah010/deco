using System.Text.Json;
using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class StoreJsonTests
{
    [Fact]
    public void CanonicalWriter_MatchesPowerShellCompressFormat()
    {
        string json = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            w.PropertyName("a"); w.Value("x\"y\\z\né");
            w.PropertyName("n"); w.Value((long)1);
            w.PropertyName("b"); w.Value(true);
            w.PropertyName("arr");
            w.BeginArray();
            w.Value((long)1);
            w.Value("z");
            w.EndArray();
            w.PropertyName("o");
            w.BeginObject();
            w.PropertyName("k"); w.Null();
            w.EndObject();
            w.EndObject();
        });
        Assert.Equal("{\"a\":\"x\\\"y\\\\z\\né\",\"n\":1,\"b\":true,\"arr\":[1,\"z\"],\"o\":{\"k\":null}}", json);
    }

    [Fact]
    public void Validate_CatchesSchemaViolations()
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","required":["a","zz"],"additionalProperties":false,"properties":{"a":{"type":"string","minLength":2},"zz":{"type":"string"},"n":{"type":"integer"},"e":{"type":"string","enum":["x"]}}}""");
        var errors = new List<string>();
        using var bad = JsonDocument.Parse("""{"b":1,"a":"q","n":1.5,"e":"y"}""");
        StoreJson.Validate(bad.RootElement, schema.RootElement, "f.json", errors);
        Assert.Contains(errors, e => e.Contains("required property"));
        Assert.Contains(errors, e => e.Contains("not allowed"));
        Assert.Contains(errors, e => e.Contains("at least 2"));
        Assert.Contains(errors, e => e.Contains("expected an integer"));
        Assert.Contains(errors, e => e.Contains("allowed set"));
    }
}

public sealed class StoreBundleTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _source;

    public StoreBundleTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-bundle-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(_source);
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

    internal static void WriteListingSource(string source, string version, bool approved)
    {
        string status = approved ? "approved" : "draft";
        File.WriteAllText(Path.Combine(source, "listing.schema.json"),
            """{"type":"object","required":["locale","reviewStatus","productName","shortDescription","description","productFeatures"],"additionalProperties":false,"properties":{"locale":{"type":"string"},"reviewStatus":{"type":"string"},"reviewedBy":{"type":"string"},"productName":{"type":"string"},"shortDescription":{"type":"string"},"description":{"type":"string"},"productFeatures":{"type":"array","minItems":1,"items":{"type":"object","required":["id","text"],"additionalProperties":true,"properties":{"id":{"type":"string"},"text":{"type":"string"}}}}}}""");
        File.WriteAllText(Path.Combine(source, "release-notes.schema.json"),
            """{"type":"object","required":["locale","releaseVersion","whatsNew"],"additionalProperties":false,"properties":{"locale":{"type":"string"},"releaseVersion":{"type":"string"},"reviewStatus":{"type":"string"},"reviewedBy":{"type":"string"},"whatsNew":{"type":"string"}}}""");
        File.WriteAllText(Path.Combine(source, "ScreenshotPlan.json"), """{"schemaVersion":1}""");
        var texts = new Dictionary<string, (string Name, string Short, string Desc, string F1, string Notes)>
        {
            ["en-US"] = ("App", "Fast app", "Does things fast and well.", "Offline first", "Bug fixes."),
            ["es-ES"] = ("App", "Aplicación rápida", "Hace cosas rápido y bien.", "Sin conexión", "Correcciones."),
        };
        foreach ((string locale, var t) in texts)
        {
            Directory.CreateDirectory(Path.Combine(source, locale));
            File.WriteAllText(Path.Combine(source, locale, "listing.json"),
                $$"""{"locale":"{{locale}}","reviewStatus":"{{status}}","reviewedBy":"ana","productName":"{{t.Name}}","shortDescription":"{{t.Short}}","description":"{{t.Desc}}","productFeatures":[{"id":"offline","text":"{{t.F1}}"}]}""");
        }

        foreach ((string locale, var t) in texts)
        {
            string dir = Path.Combine(source, "release-notes", version);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, locale + ".json"),
                $$"""{"locale":"{{locale}}","releaseVersion":"{{version}}","reviewStatus":"{{status}}","reviewedBy":"ana","whatsNew":"{{t.Notes}}"}""");
        }
    }

    [Fact]
    public void Load_ApprovedBundle()
    {
        WriteListingSource(_source, "1.0.0", approved: true);
        var bundle = StoreListingBundle.Load(_source, "1.0.0", _tmp, ["en-US", "es-ES"], allowDraft: false);
        Assert.True(bundle.IsApproved);
        Assert.Equal(2, bundle.Locales.Count);
        Assert.Empty(bundle.ReviewIssues);
    }

    [Fact]
    public void Load_DraftNeedsPreview()
    {
        WriteListingSource(_source, "1.0.0", approved: false);
        var ex = Assert.Throws<Core.DecoException>(() =>
            StoreListingBundle.Load(_source, "1.0.0", _tmp, ["en-US", "es-ES"], allowDraft: false));
        Assert.Contains("not approved", ex.Message);
        var draft = StoreListingBundle.Load(_source, "1.0.0", _tmp, ["en-US", "es-ES"], allowDraft: true);
        Assert.False(draft.IsApproved);
        Assert.NotEmpty(draft.ReviewIssues);
    }

    [Fact]
    public void Load_RejectsIdenticalTranslations()
    {
        WriteListingSource(_source, "1.0.0", approved: true);
        // Make es-ES description identical to en-US.
        string esPath = Path.Combine(_source, "es-ES", "listing.json");
        string json = File.ReadAllText(esPath).Replace("Hace cosas rápido y bien.", "Does things fast and well.");
        File.WriteAllText(esPath, json);
        Assert.Throws<Core.DecoException>(() =>
            StoreListingBundle.Load(_source, "1.0.0", _tmp, ["en-US", "es-ES"], allowDraft: true));
    }

    [Fact]
    public void PngIdentity_ReadsIhdr()
    {
        string png = Path.Combine(_tmp, "t.png");
        using (var bmp = new SkiaSharp.SKBitmap(1366, 768))
        {
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(png);
            data.SaveTo(fs);
        }

        Assert.Equal((1366, 768), StoreShots.PngIdentity(png));
        string txt = Path.Combine(_tmp, "t.txt");
        File.WriteAllText(txt, "not a png at all, definitely longer than thirty-three bytes!!");
        Assert.Throws<Core.DecoException>(() => StoreShots.PngIdentity(txt));
    }
}

public sealed class StoreShotsFlowTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _candidate;
    private readonly StoreShots.ShotPlan _plan;
    private readonly string _planSha = "plan-sha";
    private readonly Dictionary<string, string> _listingHashes = new()
    {
        ["en-US"] = "lh1",
        ["es-ES"] = "lh2",
    };

    public StoreShotsFlowTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-shots-" + Guid.NewGuid().ToString("N"));
        _candidate = Path.Combine(_tmp, "cand");
        Directory.CreateDirectory(_candidate);
        var shots = new List<StoreShots.Shot>();
        for (int i = 1; i <= 4; i++)
        {
            shots.Add(new StoreShots.Shot(
                $"s{i}", i, $"0{i}-shot.png",
                new Dictionary<string, string> { ["en-US"] = $"Caption {i} en", ["es-ES"] = $"Caption {i} es" },
                new Dictionary<string, string> { ["en-US"] = $"Marker {i} en", ["es-ES"] = $"Marker {i} es" }));
        }

        _plan = new StoreShots.ShotPlan(1, "Desktop", ["en-US", "es-ES"], 8, 8, 1_000_000, 1, 10, shots);
        foreach (string locale in _plan.Locales)
        {
            foreach (var shot in _plan.Shots)
            {
                string dir = Path.Combine(_candidate, "screenshots", locale, "desktop");
                Directory.CreateDirectory(dir);
                using var bmp = new SkiaSharp.SKBitmap(8, 8);
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(Path.Combine(dir, shot.FileName));
                data.SaveTo(fs);
            }
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

    private StoreShots.Provenance Prov() =>
        new("abc123", false, "status-sha", null, null, null, null, _listingHashes);

    [Fact]
    public void FullFlow_Manifest_Test_Review_Approve_Gate()
    {
        (string _, string sha) = StoreShots.NewManifest(_candidate, "1.0.0", "Release", _plan, _planSha, Prov());
        Assert.False(string.IsNullOrWhiteSpace(sha));

        var tested = StoreShots.TestManifest(_candidate, "1.0.0", "Release", _plan, _planSha, Prov(), requireApproval: false);
        Assert.Equal(8, tested.Manifest.GetProperty("images").GetArrayLength());

        // Approval requires review assets first.
        Assert.Throws<Core.DecoException>(() =>
            StoreShots.NewApproval(_candidate, tested, _plan, _planSha, _listingHashes, "ana"));
        StoreShots.WriteReviewAssets(_candidate, tested.Manifest, ["en-US", "es-ES"]);
        Assert.True(File.Exists(Path.Combine(_candidate, "review", "en-US-contact-sheet.png")));
        Assert.True(File.Exists(Path.Combine(_candidate, "review", "index.html")));

        var approval = StoreShots.NewApproval(_candidate, tested, _plan, _planSha, _listingHashes, "ana");
        Assert.Equal("ana", approval.ApprovedBy);
        Assert.Equal(8, approval.ScreenshotCount);

        // Gate now passes with approval required.
        StoreShots.TestManifest(_candidate, "1.0.0", "Release", _plan, _planSha, Prov(), requireApproval: true);
    }

    [Fact]
    public void TestManifest_RejectsTamperedFile()
    {
        StoreShots.NewManifest(_candidate, "1.0.0", "Release", _plan, _planSha, Prov());
        File.AppendAllText(Path.Combine(_candidate, "screenshots", "en-US", "desktop", "01-shot.png"), "tamper");
        Assert.Throws<Core.DecoException>(() =>
            StoreShots.TestManifest(_candidate, "1.0.0", "Release", _plan, _planSha, Prov(), requireApproval: false));
    }

    [Fact]
    public void LoadPlan_ValidatesMatrix()
    {
        string planPath = Path.Combine(_tmp, "ScreenshotPlan.json");
        File.WriteAllText(planPath, """
            {"schemaVersion":1,"deviceFamily":"Desktop","minimumWidth":8,"minimumHeight":8,"maximumFileSizeBytes":1000000,
             "minimumShotsPerLocale":1,"maximumShotsPerLocale":10,
             "locales":[{"tag":"en-US"},{"tag":"es-ES"}],
             "shots":[
               {"id":"a","order":1,"fileName":"01-a.png","caption":{"en-US":"Cap a en","es-ES":"Cap a es"},"expectedMarkerText":{"en-US":"M a en","es-ES":"M a es"}},
               {"id":"b","order":2,"fileName":"02-b.png","caption":{"en-US":"Cap b en","es-ES":"Cap b es"},"expectedMarkerText":{"en-US":"M b en","es-ES":"M b es"}}]}
            """);
        var plan = StoreShots.LoadPlan(planPath, ["en-US", "es-ES"]);
        Assert.Equal(2, plan.Shots.Count);
        Assert.Equal("01-a.png", plan.Shots[0].FileName);
    }
}

public sealed class StoreContractPlanTests : IDisposable
{
    private readonly string _tmp;

    public StoreContractPlanTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
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
    public void NewPayload_FillsListingText_And_ZipBuilds()
    {
        string source = Path.Combine(_tmp, "src");
        Directory.CreateDirectory(source);
        StoreBundleTests.WriteListingSource(source, "1.0.0", approved: true);
        var bundle = StoreListingBundle.Load(source, "1.0.0", _tmp, ["en-US", "es-ES"], allowDraft: false);

        // 4 shots per locale to satisfy the 4-10 contract rule.
        string cand = Path.Combine(_tmp, "cand");
        var shots = new List<StoreShots.Shot>();
        for (int i = 1; i <= 4; i++)
        {
            shots.Add(new StoreShots.Shot(
                $"s{i}", i, $"0{i}-shot.png",
                new Dictionary<string, string> { ["en-US"] = $"Caption {i} en", ["es-ES"] = $"Caption {i} es" },
                new Dictionary<string, string> { ["en-US"] = $"Marker {i} en", ["es-ES"] = $"Marker {i} es" }));
        }

        var plan = new StoreShots.ShotPlan(1, "Desktop", ["en-US", "es-ES"], 8, 8, 1_000_000, 1, 10, shots);
        foreach (string locale in plan.Locales)
        {
            foreach (var shot in plan.Shots)
            {
                string dir = Path.Combine(cand, "screenshots", locale, "desktop");
                Directory.CreateDirectory(dir);
                using var bmp = new SkiaSharp.SKBitmap(8, 8);
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(Path.Combine(dir, shot.FileName));
                data.SaveTo(fs);
            }
        }

        var prov = new StoreShots.Provenance("c", false, "s", null, null, null, null,
            new Dictionary<string, string> { ["en-US"] = "h", ["es-ES"] = "h2" });
        StoreShots.NewManifest(cand, "1.0.0", "Release", plan, "psha", prov);
        var tested = StoreShots.TestManifest(cand, "1.0.0", "Release", plan, "psha", prov, requireApproval: false);

        using var submission = JsonDocument.Parse(
            """{"listings":{"en-us":{"baseListing":{"title":"Old"}},"es-es":{"baseListing":{"title":"Viejo"}}},"applicationPackages":[]}""");
        var payload = StoreSubmissionContract.NewPayload(
            submission.RootElement, bundle, tested.Manifest, cand, "App.msixbundle", ["en-US", "es-ES"]);
        Assert.Equal(8, payload.Images.Count);
        string title = payload.Payload.GetProperty("listings").GetProperty("en-us")
            .GetProperty("baseListing").GetProperty("title").GetString() ?? string.Empty;
        Assert.Equal("App", title);
        int imgCount = payload.Payload.GetProperty("listings").GetProperty("en-us")
            .GetProperty("baseListing").GetProperty("images").GetArrayLength();
        Assert.Equal(4, imgCount);

        string bundlePath = Path.Combine(_tmp, "App.msixbundle");
        File.WriteAllBytes(bundlePath, [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(cand, "review"));
        string zip = StoreSubmissionContract.NewUploadZip(payload, bundlePath, Path.Combine(cand, "review", "up.zip"));
        Assert.True(File.Exists(zip));
        Assert.Throws<Core.DecoException>(() =>
            StoreSubmissionContract.NewUploadZip(payload, bundlePath, Path.Combine(cand, "review", "up.zip")));
    }
}
