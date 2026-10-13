using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class MsixPackagingTests
{
    [Fact]
    public void PatchManifestText_RewritesIdentity()
    {
        string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="fettah.Formixa" Publisher="CN=Old" Version="0.0.0.0" ProcessorArchitecture="x64" />
            </Package>
            """;
        string next = MsixPackaging.PatchManifestText(xml, "0.7.0.0", "x64", "CN=Acme");
        Assert.Contains(@"Version=""0.7.0.0""", next);
        Assert.Contains(@"Publisher=""CN=Acme""", next);
        Assert.Contains("fettah.Formixa", next); // Name untouched
    }

    [Fact]
    public void PatchManifestText_ThrowsWithoutIdentity()
    {
        Assert.Throws<Core.DecoException>(() =>
            MsixPackaging.PatchManifestText("<Package></Package>", "1.0.0.0", "x64", "CN=X"));
    }

    [Fact]
    public void BuildAppInstallerXml_IsWellFormed_AndEscapes()
    {
        string xml = MsixPackaging.BuildAppInstallerXml(
            "https://example.com/msix/App_1.0_x64.msix?x=1&y=2", "1.0.0.0", "pkg", "CN=A&B", "x64", 12);
        Assert.Contains("appinstaller/2021", xml);
        Assert.Contains("&amp;", xml);
        Assert.DoesNotContain("A&B\"", xml);
    }

    [Fact]
    public void MakeAppxPackArgs_OrderMatchesPs1()
    {
        Assert.Equal(["pack", "/d", "stage", "/p", "out.msix", "/nv"],
            MsixPackaging.MakeAppxPackArgs("stage", "out.msix"));
    }

    [Fact]
    public void RequiredStagingFiles_CoversManifestExePriTiles()
    {
        var req = MsixPackaging.RequiredStagingFiles("App.exe", "App.pri", ["tessdata\\eng.traineddata"]);
        Assert.Contains("AppxManifest.xml", req);
        Assert.Contains("resources.pri", req);
        Assert.Contains(@"Assets\SplashScreen.png", req);
        Assert.Contains("tessdata\\eng.traineddata", req);
    }

    [Fact]
    public void MissingStagingFiles_ListsAbsent()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-msix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "AppxManifest.xml"), "<x/>");
            var missing = MsixPackaging.MissingStagingFiles(tmp, "App.exe", "App.pri", []);
            Assert.Contains("App.exe", missing);
            Assert.DoesNotContain("AppxManifest.xml", missing);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void MsBuildPlatform_MapsArm()
    {
        Assert.Equal("ARM64", MsixPackaging.MsBuildPlatform("arm64"));
        Assert.Equal("x64", MsixPackaging.MsBuildPlatform("x64"));
        Assert.Equal("win-arm64", MsixPackaging.Rid("arm64"));
        Assert.Equal("x64", MsixPackaging.Arch("x64"));
    }

    [Fact]
    public void RenderTileArt_WritesFiveTiles()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-tiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string logo = Path.Combine(tmp, "Logo.png");
            using (var bmp = new SkiaSharp.SKBitmap(256, 256))
            {
                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(logo);
                data.SaveTo(fs);
            }

            string dest = Path.Combine(tmp, "Assets");
            MsixPackaging.RenderTileArt(logo, dest);
            var wide = SkiaSharp.SKBitmap.DecodeBounds(Path.Combine(dest, "Wide310x150Logo.png"));
            Assert.Equal(310, wide.Width);
            Assert.Equal(150, wide.Height);
            Assert.Equal(5, Directory.GetFiles(dest).Length);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}

public sealed class VelopackFlowTests
{
    [Fact]
    public void VpkPackArgs_IncludesIconAndNotes()
    {
        var args = VelopackFlow.VpkPackArgs("App", "1.0.1", "pub", "rel", "stable",
            "win-x64", "App.exe", "T", "A", "Assets/app.ico", "notes.md");
        Assert.Contains("--icon", args);
        Assert.Contains("--releaseNotes", args);
        Assert.DoesNotContain("--publish", args);
    }

    [Fact]
    public void VpkUploadArgs_Flags()
    {
        var args = VelopackFlow.VpkUploadArgs("https://github.com/o/r", "rel", "stable", "N 1.0", "v1.0", true, false);
        Assert.Contains("--publish", args);
        Assert.Contains("--tag", args);
        Assert.DoesNotContain("--pre", args);
    }

    [Fact]
    public void DefaultTag_FallsBackToVVersion()
    {
        Assert.Equal("v1.0.1", VelopackFlow.DefaultTag("1.0.1", ""));
        Assert.Equal("v9", VelopackFlow.DefaultTag("1.0.1", "v9"));
    }

    [Fact]
    public void PublishArgsWithVersion_Overrides()
    {
        var args = VelopackFlow.PublishArgsWithVersion("a.csproj", "win-x64", "x64", "out", "1.0.1");
        Assert.Contains("/p:Version=1.0.1", args);
        Assert.Contains("/p:AssemblyVersion=1.0.1.0", args);
    }
}

public sealed class SplashServiceTests : IDisposable
{
    private readonly string _tmp;

    public SplashServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-splash-" + Guid.NewGuid().ToString("N"));
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

    private string WriteLogo()
    {
        string path = Path.Combine(_tmp, "Logo.png");
        using var bmp = new SkiaSharp.SKBitmap(256, 256);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.SteelBlue };
            canvas.DrawCircle(128, 128, 100, paint);
        }

        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    private static SplashService.Request Req(string tmp, string logo, bool statik, string theme = "Dark") =>
        new("Sample", "1.2.3-beta", "beta", 232, logo, Path.Combine(tmp, statik ? "static.png" : "full.png"), statik, theme);

    [Fact]
    public void Render_Full_640x400()
    {
        string logo = WriteLogo();
        SplashService.Render(Req(_tmp, logo, statik: false));
        var info = SkiaSharp.SKBitmap.DecodeBounds(Path.Combine(_tmp, "full.png"));
        Assert.Equal(640, info.Width);
        Assert.Equal(400, info.Height);
    }

    [Fact]
    public void Render_Static_DiffersFromFull()
    {
        string logo = WriteLogo();
        SplashService.Render(Req(_tmp, logo, statik: false));
        SplashService.Render(Req(_tmp, logo, statik: true));
        byte[] full = File.ReadAllBytes(Path.Combine(_tmp, "full.png"));
        byte[] statik = File.ReadAllBytes(Path.Combine(_tmp, "static.png"));
        Assert.NotEqual(full, statik);
    }

    [Fact]
    public void MetaLine_Formats()
    {
        Assert.Equal("Beta channel  ·  ~232 MB", SplashService.MetaLine("beta", 232, statik: false));
        Assert.Equal("Stable channel", SplashService.MetaLine("stable", 0, statik: false));
        Assert.Equal("Beta channel", SplashService.MetaLine("beta", 232, statik: true));
    }

    [Fact]
    public void Validate_RejectsBadTheme()
    {
        Assert.Throws<Core.DecoException>(() =>
            SplashService.Validate(Req(_tmp, "x", statik: true, theme: "Neon")));
    }
}

public sealed class ParityCheckerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _app;
    private readonly string _tpl;

    public ParityCheckerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-parity-" + Guid.NewGuid().ToString("N"));
        _app = Path.Combine(_tmp, "app");
        _tpl = Path.Combine(_tmp, "tpl");
        Directory.CreateDirectory(_app);
        Directory.CreateDirectory(_tpl);
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

    private void Write(string root, string rel, string content)
    {
        string full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Compare_FindsMissingAndDifferent()
    {
        Write(_app, "same.cs", "x");
        Write(_tpl, "same.cs", "x");
        Write(_app, "diff.cs", "a");
        Write(_tpl, "diff.cs", "b");
        Write(_app, "only-app.cs", "x");
        Write(_tpl, "only-tpl.cs", "x");
        Write(_app, Path.Combine("bin", "skip.dll"), "x");

        var r = ParityChecker.Compare(_app, _tpl, ["bin", "obj"], [], new Dictionary<string, string>());
        Assert.Equal(["only-app.cs"], r.MissingInTemplate);
        Assert.Equal(["only-tpl.cs"], r.MissingInApp);
        Assert.Equal(["diff.cs"], r.Different);
        Assert.Equal(2, r.Compared);
    }

    [Fact]
    public void Compare_HonorsExcludesAndRenames()
    {
        Write(_app, "App.sln", "x");
        Write(_tpl, "__SafeName__.sln", "x");
        Write(_app, "CHANGELOG.md", "x");
        var renames = new Dictionary<string, string> { ["App.sln"] = "__SafeName__.sln" };
        var r = ParityChecker.Compare(_app, _tpl, [], ["CHANGELOG.md"], renames);
        Assert.Empty(r.MissingInTemplate);
        Assert.Empty(r.MissingInApp);
        Assert.Empty(r.Different);
    }

    [Theory]
    [InlineData("UI/x.cs", "UI/**", true)]
    [InlineData("docs/a/b.md", "docs/**", true)]
    [InlineData("Scripts/test-x.ps1", "Scripts/test-*.ps1", true)]
    [InlineData("Scripts/run.ps1", "Scripts/test-*.ps1", false)]
    public void IsExcluded_Globs(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, ParityChecker.IsExcluded(path, [pattern]));
    }
}
