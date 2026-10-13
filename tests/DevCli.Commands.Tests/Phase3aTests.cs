using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class WackRunnerTests
{
    private const string PassReport = """
        <?xml version="1.0"?>
        <REPORT OVERALL_RESULT="PASS">
          <TEST NAME="App manifest test"><RESULT><![CDATA[PASS]]></RESULT></TEST>
        </REPORT>
        """;

    private const string FailReport = """
        <?xml version="1.0"?>
        <REPORT OVERALL_RESULT="FAIL">
          <TEST NAME="Supported APIs test"><RESULT><![CDATA[FAIL]]></RESULT><MESSAGES><MESSAGE TEXT="API foo is not supported for this application type. &quot;details&quot; here."/></MESSAGES></TEST>
          <TEST NAME="Other test"><RESULT><![CDATA[PASS]]></RESULT></TEST>
        </REPORT>
        """;

    [Fact]
    public void ParseReport_Pass_NoFailures()
    {
        var v = WackRunner.ParseReport(PassReport);
        Assert.Equal("PASS", v.Overall);
        Assert.Empty(v.Failures);
        Assert.Equal(0, WackRunner.MapVerdictToExit(v.Overall));
    }

    [Fact]
    public void ParseReport_Fail_ExtractsTestAndMessage()
    {
        var v = WackRunner.ParseReport(FailReport);
        Assert.Equal("FAIL", v.Overall);
        var f = Assert.Single(v.Failures);
        Assert.Equal("Supported APIs test", f.Test);
        Assert.Contains("API foo is not supported", f.Messages[0]);
        Assert.DoesNotContain("&quot;", f.Messages[0]);
        Assert.Equal(1, WackRunner.MapVerdictToExit(v.Overall));
    }

    [Fact]
    public void ParseReport_Unknown_WhenMissing()
    {
        var v = WackRunner.ParseReport("<REPORT></REPORT>");
        Assert.Equal("UNKNOWN", v.Overall);
        Assert.Equal(1, WackRunner.MapVerdictToExit(v.Overall));
    }

    [Fact]
    public void ValidatePackage_RejectsUploadArchive()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-wack-" + Guid.NewGuid().ToString("N") + ".msixupload");
        File.WriteAllBytes(tmp, [1]);
        try
        {
            Assert.Throws<Core.DecoException>(() => WackRunner.ValidatePackage(tmp));
            Assert.Throws<Core.DecoException>(() => WackRunner.ValidatePackage(Path.Combine(Path.GetTempPath(), "nope.msix")));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void AppCertArgs_OrderMatchesPs1()
    {
        Assert.Equal(["test", "-appxpackagepath", "a.msix", "-reportoutputpath", "w.xml"],
            WackRunner.AppCertArgs("a.msix", "w.xml"));
    }

    [Fact]
    public void DefaultReportPath_UsesPackageName()
    {
        string p = WackRunner.DefaultReportPath("C:\\r\\App_1.0_x64.msix");
        Assert.EndsWith("wack-App_1.0_x64.xml", p);
    }
}

public sealed class MsstoreConfigTests
{
    [Fact]
    public void Resolve_PrefersFlag_ThenEnv()
    {
        Environment.SetEnvironmentVariable("MSSTORE_TENANT_ID", "env-tenant");
        try
        {
            var fromEnv = MsstoreConfig.Resolve("", "", "", "", "", "", "");
            Assert.Equal("env-tenant", fromEnv.TenantId);
            var fromFlag = MsstoreConfig.Resolve("flag-tenant", "", "", "", "", "", "");
            Assert.Equal("flag-tenant", fromFlag.TenantId);
            Assert.Equal("flag", fromFlag.TenantSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSTORE_TENANT_ID", null);
        }
    }

    [Fact]
    public void MissingIdentity_NamesAllThree()
    {
        var c = MsstoreConfig.Resolve("", "", "", "secret", "", "", "");
        Assert.Equal(3, MsstoreConfig.MissingIdentity(c).Count);
    }

    [Fact]
    public void Resolve_ReadsDecoPrefixedEnv()
    {
        string? prior = Environment.GetEnvironmentVariable("DECO_STORE_TENANT_ID");
        Environment.SetEnvironmentVariable("DECO_STORE_TENANT_ID", "deco-tenant");
        try
        {
            var c = MsstoreConfig.Resolve("", "", "", "", "", "", "");
            Assert.Equal("deco-tenant", c.TenantId);
            Assert.Equal("env DECO_STORE_TENANT_ID", c.TenantSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DECO_STORE_TENANT_ID", prior);
        }
    }

    [Fact]
    public void ReconfigureArgs_SecretPath()
    {
        var c = MsstoreConfig.Resolve("t", "s", "c", "shh", "", "", "");
        Assert.Equal(
            ["reconfigure", "--tenantId", "t", "--sellerId", "s", "--clientId", "c", "--clientSecret", "shh"],
            MsstoreConfig.ReconfigureArgs(c));
    }

    [Fact]
    public void ReconfigureArgs_RequiresAuth()
    {
        var c = MsstoreConfig.Resolve("t", "s", "c", "", "", "", "");
        Assert.Throws<Core.DecoException>(() => MsstoreConfig.ReconfigureArgs(c));
    }

    [Fact]
    public void ReconfigureArgs_CertFileWithPassword()
    {
        var c = MsstoreConfig.Resolve("t", "s", "c", "", "", "cert.pfx", "pw");
        var args = MsstoreConfig.ReconfigureArgs(c);
        Assert.Contains("--certificateFilePath", args);
        Assert.Contains("--certificatePassword", args);
        Assert.DoesNotContain("--clientSecret", args);
    }

    [Fact]
    public void RedactForDisplay_HidesAllSecrets()
    {
        var c = MsstoreConfig.Resolve("t123", "s456", "c789", "super-secret", "", "", "cert-pw");
        var display = MsstoreConfig.RedactForDisplay(MsstoreConfig.ReconfigureArgs(c), c);
        string joined = string.Join(" ", display);
        Assert.DoesNotContain("super-secret", joined);
        Assert.DoesNotContain("cert-pw", joined);
        Assert.DoesNotContain("t123", joined);
        Assert.Contains("--clientSecret <redacted>", joined);
    }

    private sealed class FakeRunner : Infrastructure.IProcessRunner
    {
        private readonly Func<string, IReadOnlyList<string>, Infrastructure.ProcessResult> _respond;

        public FakeRunner(Func<string, IReadOnlyList<string>, Infrastructure.ProcessResult> respond) => _respond = respond;

        public Task<Infrastructure.ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? env = null) =>
            Task.FromResult(_respond(exe, args));
    }

    [Fact]
    public void FindCli_AcceptsHealthyCli()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-ms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string cli = Path.Combine(tmp, "msstore.exe");
            File.WriteAllBytes(cli, [0]);
            var runner = new FakeRunner((exe, args) => new Infrastructure.ProcessResult(0, "Microsoft Store Developer CLI 1.0", string.Empty, TimeSpan.Zero));
            Assert.Equal(cli, MsstoreConfig.FindCli(runner, [tmp]));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void FindCli_RejectsImpostorAndMissing()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-ms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string fake = Path.Combine(tmp, "msstore.exe");
            File.WriteAllBytes(fake, [0]);
            var badRunner = new FakeRunner((exe, args) => new Infrastructure.ProcessResult(1, string.Empty, "nope", TimeSpan.Zero));
            Assert.Null(MsstoreConfig.FindCli(badRunner, [tmp]));
            Assert.Null(MsstoreConfig.FindCli(badRunner, [Path.Combine(tmp, "empty")]));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void BuildSubmissionListings_PatchesTextAndPackages()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "description.txt"), "New description here.");
            File.WriteAllBytes(Path.Combine(tmp, "01-a.png"), [1, 2, 3]);
            string patched = StoreApiSubmission.BuildSubmissionListings(
                """{"listings":{"en-us":{"baseListing":{"description":"Old","releaseNotes":"Old notes","images":[]}}},"applicationPackages":[]}""",
                Path.Combine(tmp, "description.txt"), "New notes", tmp, "App.msixbundle");
            Assert.Contains("New description here.", patched);
            Assert.Contains("New notes", patched);
            Assert.Contains("01-a.png", patched);
            Assert.Contains("App.msixbundle", patched);
            Assert.DoesNotContain("Old notes", patched);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void BuildSubmissionListings_RequiresEnUs()
    {
        Assert.Throws<Core.DecoException>(() => StoreApiSubmission.BuildSubmissionListings(
            """{"listings":{}}""", "nonexistent.txt", "", null, "b.msixbundle"));
    }

    [Fact]
    public void BuildSubmissionZip_BundlesPackageAndShots()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, "App.msixbundle"), [9]);
            File.WriteAllBytes(Path.Combine(tmp, "01-a.png"), [1]);
            string zip = StoreApiSubmission.BuildSubmissionZip(
                Path.Combine(tmp, "App.msixbundle"), tmp, Path.Combine(tmp, "up.zip"));
            Assert.True(File.Exists(zip));
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            Assert.Equal(2, archive.Entries.Count);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void SelectStages_AllByDefault_NameOrIndex()
    {
        Assert.Equal(8, ReleaseStages.Select([]).Count);
        Assert.Equal([4], ReleaseStages.Select(["wack"]));
        Assert.Equal([2], ReleaseStages.Select(["2"]));
        Assert.Equal([0, 5], ReleaseStages.Select(["preflight", "5"]));
        Assert.Throws<Core.DecoException>(() => ReleaseStages.Select(["nope"]));
    }
}
