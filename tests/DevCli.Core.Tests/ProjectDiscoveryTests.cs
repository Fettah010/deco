using DevCli.Core;

namespace DevCli.Core.Tests;

public sealed class ProjectDiscoveryTests : IDisposable
{
    private readonly string _tmp;

    public ProjectDiscoveryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-tests-" + Guid.NewGuid().ToString("N"));
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
            // Best effort cleanup; never fail a test on tmp delete.
        }
    }

    [Fact]
    public void Discover_UsesManifestAndCsprojVersions()
    {
        File.WriteAllText(
            Path.Combine(_tmp, ".devcli.json"),
            """{ "appId": "SampleApp", "csproj": "SampleApp.csproj", "storeAppId": "9ABC" }""");
        File.WriteAllText(
            Path.Combine(_tmp, "SampleApp.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Version>0.7.0</Version>
                <InformationalVersion>0.7.0-beta</InformationalVersion>
              </PropertyGroup>
            </Project>
            """);

        ProjectContext ctx = ProjectDiscovery.Discover(_tmp);

        Assert.Equal("SampleApp", ctx.AppId);
        Assert.True(ctx.IsManifestFound);
        Assert.Equal("0.7.0", ctx.Version);
        Assert.Equal("0.7.0-beta", ctx.InformationalVersion);
        Assert.Equal("9ABC", ctx.StoreAppId);
    }

    [Fact]
    public void Discover_FallsBackToFolderName_WhenNothingFound()
    {
        ProjectContext ctx = ProjectDiscovery.Discover(_tmp);

        Assert.False(ctx.IsManifestFound);
        Assert.Null(ctx.CsprojPath);
        Assert.Equal(new DirectoryInfo(_tmp).Name, ctx.AppId);
    }

    [Fact]
    public void Discover_ThrowsUsageError_ForMissingPath()
    {
        var ex = Assert.Throws<DecoException>(
            () => ProjectDiscovery.Discover(Path.Combine(_tmp, "nope")));
        Assert.Equal(ExitCodes.UsageOrAuthNeeded, ex.ExitCode);
    }
}
