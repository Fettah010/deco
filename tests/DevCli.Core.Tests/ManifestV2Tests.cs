using DevCli.Core;

namespace DevCli.Core.Tests;

public sealed class ManifestV2Tests : IDisposable
{
    private readonly string _tmp;

    public ManifestV2Tests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-manifestv2-" + Guid.NewGuid().ToString("N"));
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

    private string WriteManifest(string json)
    {
        string path = Path.Combine(_tmp, ".devcli.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LoadManifest_ParsesV2Fields()
    {
        string path = WriteManifest("""
            {
              "appId": "SampleApp",
              "locales": ["en-US", "de-DE"],
              "paths": { "msixDir": "out/msix", "storeDir": "out/store", "listingSourceDir": "ListingSource" },
              "capture": { "vmName": "Sample-Capture", "vmUser": "cap" }
            }
            """);

        ProjectManifest m = ProjectDiscovery.LoadManifest(path);

        Assert.Equal(["en-US", "de-DE"], m.Locales!);
        Assert.Equal("out/msix", m.Paths!.MsixDir);
        Assert.Equal("out/store", m.Paths.StoreDir);
        Assert.Equal("ListingSource", m.Paths.ListingSourceDir);
        Assert.Equal("Sample-Capture", m.Capture!.VmName);
        Assert.Equal("cap", m.Capture.VmUser);
    }

    [Fact]
    public void LoadManifest_V1StaysNull()
    {
        string path = WriteManifest("""{ "appId": "SampleApp", "storeAppId": "9ABC" }""");

        ProjectManifest m = ProjectDiscovery.LoadManifest(path);

        Assert.Null(m.Locales);
        Assert.Null(m.Paths);
        Assert.Null(m.Capture);
    }

    [Fact]
    public void LoadManifest_IgnoresUnknownFields()
    {
        string path = WriteManifest("""{ "appId": "SampleApp", "vm": { "name": "X" }, "repoRoot": "." }""");

        ProjectManifest m = ProjectDiscovery.LoadManifest(path);

        Assert.Equal("SampleApp", m.AppId);
        Assert.Null(m.Capture);
    }

    [Theory]
    [InlineData(null)]
    public void NormalizeLocales_NullStaysNull(string[]? raw)
    {
        Assert.Null(ProjectManifest.NormalizeLocales(raw));
    }

    [Fact]
    public void NormalizeLocales_CleansAndDedupes()
    {
        string[]? clean = ProjectManifest.NormalizeLocales([" en-US ", "", "en-US", "fr-FR"]);

        Assert.Equal(["en-US", "fr-FR"], clean!);
    }

    [Fact]
    public void NormalizeLocales_AllEmptyGivesNull()
    {
        Assert.Null(ProjectManifest.NormalizeLocales(["", "  "]));
        Assert.Null(ProjectManifest.NormalizeLocales([]));
    }

    [Fact]
    public void Discover_ResolvesV2Context()
    {
        WriteManifest("""
            {
              "appId": "SampleApp",
              "locales": ["en-US"],
              "paths": { "msixDir": "out/msix" },
              "capture": { "vmName": "Sample-Capture" }
            }
            """);

        ProjectContext ctx = ProjectDiscovery.Discover(_tmp);

        Assert.Equal(["en-US"], ctx.Locales);
        Assert.Equal(Path.Combine(_tmp, "out", "msix"), ctx.MsixDir);
        Assert.Equal(Path.Combine(_tmp, "Releases", "Store"), ctx.StoreDir);
        Assert.Equal(Path.Combine(_tmp, "Store", "ListingSource"), ctx.ListingSourceDir);
        Assert.Equal("Sample-Capture", ctx.VmName);
        Assert.Null(ctx.VmUser);
    }

    [Fact]
    public void Discover_V1ContextMatchesLegacyLayout()
    {
        WriteManifest("""{ "appId": "SampleApp" }""");

        ProjectContext ctx = ProjectDiscovery.Discover(_tmp);

        Assert.Equal(CliConstants.DefaultLocales, ctx.Locales);
        Assert.Equal(Path.Combine(_tmp, "Releases", "Msix"), ctx.MsixDir);
        Assert.Equal(Path.Combine(_tmp, "Releases", "Store"), ctx.StoreDir);
        Assert.Equal(Path.Combine(_tmp, "Store", "ListingSource"), ctx.ListingSourceDir);
        Assert.Null(ctx.VmName);
        Assert.Null(ctx.VmUser);
    }

    [Fact]
    public void Discover_KeepsAbsolutePaths()
    {
        string abs = Path.Combine(Path.GetTempPath(), "deco-abs-" + Guid.NewGuid().ToString("N"));
        WriteManifest("""{ "appId": "SampleApp", "paths": { "msixDir": "__ABS__" } }"""
            .Replace("__ABS__", abs.Replace("\\", "\\\\", StringComparison.Ordinal)));

        ProjectContext ctx = ProjectDiscovery.Discover(_tmp);

        Assert.Equal(Path.GetFullPath(abs), ctx.MsixDir);
    }
}
