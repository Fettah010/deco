using DevCli.Commands;
using DevCli.Core;

namespace DevCli.Commands.Tests;

public sealed class VersionBumpServiceTests
{
    [Theory]
    [InlineData("0.6.2")]
    [InlineData("0.6.2.0")]
    [InlineData("1.0.1-beta")]
    [InlineData("10.20.30.40")]
    public void Validate_AcceptsGoodVersions(string v)
    {
        VersionBumpService.Validate(v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("a.b.c")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.x")]
    public void Validate_RejectsBadVersions(string v)
    {
        Assert.Throws<DecoException>(() => VersionBumpService.Validate(v));
    }

    [Fact]
    public void Validate_RejectsMsixOutOfRange()
    {
        Assert.Throws<DecoException>(() => VersionBumpService.Validate("1.2.3.65535"));
    }

    [Fact]
    public void ToQuad_PadsAndStripsSuffix()
    {
        Assert.Equal("0.6.2.0", VersionBumpService.ToQuad("0.6.2"));
        Assert.Equal("0.6.2.0", VersionBumpService.ToQuad("0.6.2-beta"));
        Assert.Equal("1.2.3.4", VersionBumpService.ToQuad("1.2.3.4"));
    }

    [Fact]
    public void UpdateCsprojText_RewritesFourFieldsAndPrevious()
    {
        string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Version>0.6.1</Version>
                <AssemblyVersion>0.6.1.0</AssemblyVersion>
                <FileVersion>0.6.1.0</FileVersion>
                <ReleasePreviousVersion>0.6.0.0</ReleasePreviousVersion>
                <InformationalVersion>0.6.1-beta</InformationalVersion>
              </PropertyGroup>
            </Project>
            """;

        (string next, List<string> touched) = VersionBumpService.UpdateCsprojText(xml, "0.6.2-beta");

        Assert.Contains("<Version>0.6.2</Version>", next);
        Assert.Contains("<AssemblyVersion>0.6.2.0</AssemblyVersion>", next);
        Assert.Contains("<FileVersion>0.6.2.0</FileVersion>", next);
        Assert.Contains("<InformationalVersion>0.6.2-beta</InformationalVersion>", next);
        Assert.Contains("<ReleasePreviousVersion>0.6.1.0</ReleasePreviousVersion>", next);
        Assert.Equal(5, touched.Count);
    }

    [Fact]
    public void IsWinExeCsproj_DetectsOutputType()
    {
        Assert.True(VersionBumpService.IsWinExeCsproj("<Project><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>"));
        Assert.False(VersionBumpService.IsWinExeCsproj("<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>"));
        Assert.False(VersionBumpService.IsWinExeCsproj("not xml"));
    }

    [Fact]
    public void UpdateCitationText_RewritesVersionAndDate()
    {
        string cff = "version: \"0.6.1\"\ndate-released: \"2026-01-01\"\n";
        string next = VersionBumpService.UpdateCitationText(cff, "0.6.2-beta", "2026-10-08");
        Assert.Contains("version: \"0.6.2-beta\"", next);
        Assert.Contains("date-released: \"2026-10-08\"", next);
    }

    [Fact]
    public void InsertChangelogStub_InsertsOnce()
    {
        string md = "# Changelog\n\n## [0.6.1] - 2026-01-01\n\n- Old\n";
        (string next, bool inserted) = VersionBumpService.InsertChangelogStub(md, "0.6.2", "2026-10-08");
        Assert.True(inserted);
        Assert.Contains("## [0.6.2] - 2026-10-08", next);
        Assert.True(next.IndexOf("## [0.6.2]", StringComparison.Ordinal) < next.IndexOf("## [0.6.1]", StringComparison.Ordinal));

        (string again, bool insertedAgain) = VersionBumpService.InsertChangelogStub(next, "0.6.2", "2026-10-08");
        Assert.False(insertedAgain);
        Assert.Equal(next, again);
    }

    [Fact]
    public void StampBuildInfoCommit_ReplacesCommit()
    {
        string cs = "internal const string Commit = \"abc123\";";
        Assert.Contains("Commit = \"def456\"", VersionBumpService.StampBuildInfoCommit(cs, "def456"));
        Assert.Contains("Commit = \"unknown\"", VersionBumpService.StampBuildInfoCommit(cs, ""));
    }
}
