using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class InitProjectTests : IDisposable
{
    private readonly string _tmp;

    public InitProjectTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-init-" + Guid.NewGuid().ToString("N"));
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

    private InitProject.Plan Plan(
        string? dir = null, string appId = "SampleApp", string? storeId = "9ABC",
        string? locales = null, string? vmName = "Lab", string? vmUser = null,
        bool force = false, bool dryRun = false) =>
        new(dir ?? _tmp, appId, null, storeId,
            InitProject.ValidateLocales(locales ?? "en-US, fr-FR"),
            vmName, vmUser, force, dryRun);

    [Theory]
    [InlineData(null, new[] { "en-US" })]
    [InlineData("", new[] { "en-US" })]
    [InlineData("en-US", new[] { "en-US" })]
    [InlineData("en-US, fr-FR", new[] { "en-US", "fr-FR" })]
    [InlineData(" en-US ,en-US ", new[] { "en-US" })]
    public void ValidateLocales_Parses(string? csv, string[] expected)
    {
        Assert.Equal(expected, InitProject.ValidateLocales(csv));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("en-US, nope!")]
    public void ValidateLocales_Rejects(string csv)
    {
        var ex = Assert.Throws<Core.DecoException>(() => InitProject.ValidateLocales(csv));
        Assert.Equal("init-locales", ex.Reason);
    }

    [Fact]
    public void BuildManifest_RoundTrips()
    {
        string json = InitProject.BuildManifest(Plan());

        var manifest = Core.ProjectDiscovery.LoadManifest(WriteTemp("m.json", json));
        Assert.Equal("SampleApp", manifest.AppId);
        Assert.Equal(["en-US", "fr-FR"], manifest.Locales!);
        Assert.Equal("9ABC", manifest.StoreAppId);
        Assert.Equal("Lab", manifest.Capture!.VmName);
        Assert.Null(manifest.Capture.VmUser);
    }

    [Fact]
    public void BuildManifest_OmitsEmptyOptionals()
    {
        string json = InitProject.BuildManifest(Plan(storeId: "", vmName: null));

        Assert.DoesNotContain("storeAppId", json);
        Assert.DoesNotContain("capture", json);
    }

    [Fact]
    public void Run_WritesManifestAndShim()
    {
        InitProject.Result r = InitProject.Run(Plan());

        Assert.True(r.ManifestWritten);
        Assert.True(r.ShimWritten);
        Assert.True(File.Exists(r.ManifestPath));
        Assert.NotNull(r.ShimPath);
        Assert.True(File.Exists(r.ShimPath));
        var manifest = Core.ProjectDiscovery.LoadManifest(r.ManifestPath);
        Assert.Equal("SampleApp", manifest.AppId);
    }

    [Fact]
    public void Run_RefusesWithoutForce()
    {
        InitProject.Run(Plan());
        var ex = Assert.Throws<Core.DecoException>(() => InitProject.Run(Plan()));
        Assert.Equal("init-exists", ex.Reason);
    }

    [Fact]
    public void Run_ForceOverwrites()
    {
        InitProject.Run(Plan());
        InitProject.Result r = InitProject.Run(Plan(appId: "Other", force: true));

        Assert.True(r.ManifestWritten);
        Assert.Equal("Other", Core.ProjectDiscovery.LoadManifest(r.ManifestPath).AppId);
    }

    [Fact]
    public void Run_DryRunWritesNothing()
    {
        InitProject.Result r = InitProject.Run(Plan(dryRun: true));

        Assert.False(r.ManifestWritten);
        Assert.False(r.ShimWritten);
        Assert.False(File.Exists(r.ManifestPath));
    }

    [Fact]
    public void DevShim_MatchesTemplate()
    {
        string? template = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && template is null)
        {
            string cand = Path.Combine(dir.FullName, "templates", "dev.ps1");
            if (File.Exists(cand))
            {
                template = cand;
            }

            dir = dir.Parent;
        }

        Assert.True(template is not null, "templates/dev.ps1 not found above test binaries.");
        static string Norm(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] a = Norm(File.ReadAllText(template!)).Split('\n');
        string[] b = Norm(InitProject.DevShim).Split('\n');
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            string x = i < a.Length ? a[i] : "<missing>";
            string y = i < b.Length ? b[i] : "<missing>";
            Assert.True(x == y, $"Line {i + 1} differs:\ntemplate: {x}\nconst:    {y}");
        }
    }

    [Fact]
    public void TemplateManifest_Parses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? template = null;
        while (dir is not null && template is null)
        {
            string cand = Path.Combine(dir.FullName, "templates", ".devcli.json");
            if (File.Exists(cand))
            {
                template = cand;
            }

            dir = dir.Parent;
        }

        Assert.True(template is not null, "templates/.devcli.json not found above test binaries.");
        var manifest = Core.ProjectDiscovery.LoadManifest(template!);
        Assert.Equal("MyApp", manifest.AppId);
        Assert.Equal(["en-US"], manifest.Locales!);
    }

    private string WriteTemp(string name, string content)
    {
        string path = Path.Combine(_tmp, name);
        File.WriteAllText(path, content);
        return path;
    }
}
