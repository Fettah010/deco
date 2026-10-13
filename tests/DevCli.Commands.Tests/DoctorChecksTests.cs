using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class DoctorChecksTests : IDisposable
{
    private readonly string _tmp;

    public DoctorChecksTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-doctor-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeProc : Infrastructure.IProcessRunner
    {
        private readonly Func<string, string, Infrastructure.ProcessResult> _respond;

        public FakeProc(Func<string, string, Infrastructure.ProcessResult> respond) => _respond = respond;

        public Task<Infrastructure.ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? env = null)
        {
            return Task.FromResult(_respond(exe, string.Join(" ", args)));
        }
    }

    private static Infrastructure.ProcessResult Ok(string stdout) =>
        new(0, stdout, string.Empty, TimeSpan.FromMilliseconds(5));

    private static Infrastructure.ProcessResult Fail(string stderr) =>
        new(1, string.Empty, stderr, TimeSpan.FromMilliseconds(5));

    private Core.ProjectContext Ctx(
        bool manifest = true, string[]? locales = null, string? vmName = null, string appId = "SampleApp") =>
        new(
            _tmp, appId, null, null, null, null, manifest,
            locales ?? ["en-US"],
            Path.Combine(_tmp, "Msix"), Path.Combine(_tmp, "Store"), Path.Combine(_tmp, "Listing"),
            vmName, null);

    private static Dictionary<string, string?> EnvOf(params (string K, string? V)[] pairs) =>
        pairs.ToDictionary(p => p.K, p => p.V);

    [Fact]
    public void DotnetSdk_PassFailError()
    {
        var ok = new FakeProc((exe, args) => Ok("10.0.100 [C:\\dotnet]\n8.0.400 [C:\\dotnet]\n"));
        Assert.Equal("pass", DoctorChecks.DotnetSdk(ok).Status);

        var missing = new FakeProc((exe, args) => Ok("8.0.400 [C:\\dotnet]\n"));
        var miss = DoctorChecks.DotnetSdk(missing);
        Assert.Equal("fail", miss.Status);
        Assert.Contains("winget", miss.Fix, StringComparison.OrdinalIgnoreCase);

        var dead = new FakeProc((exe, args) => throw new InvalidOperationException("nope"));
        Assert.Equal("fail", DoctorChecks.DotnetSdk(dead).Status);
    }

    [Fact]
    public void DotnetRuntime_PassFail()
    {
        var ok = new FakeProc((exe, args) => Ok("Microsoft.NETCore.App 10.0.0 [C:\\dotnet]\n"));
        Assert.Equal("pass", DoctorChecks.DotnetRuntime(ok).Status);

        var missing = new FakeProc((exe, args) => Ok("Microsoft.NETCore.App 8.0.0 [C:\\dotnet]\n"));
        Assert.Equal("fail", DoctorChecks.DotnetRuntime(missing).Status);
    }

    [Fact]
    public void MsstoreCli_FoundAndMissing()
    {
        File.WriteAllText(Path.Combine(_tmp, "MSStore.exe"), "x");
        var ok = new FakeProc((exe, args) => Ok("0.4.3"));
        var found = DoctorChecks.MsstoreCli(ok, [_tmp]);
        Assert.Equal("pass", found.Status);
        Assert.Contains("MSStore.exe", found.Detail, StringComparison.OrdinalIgnoreCase);

        var missing = DoctorChecks.MsstoreCli(ok, [Path.Combine(_tmp, "empty")]);
        Assert.Equal("warn", missing.Status);
        Assert.Contains("winget", missing.Fix);
    }

    [Fact]
    public void EntraEnv_PassAndWarn()
    {
        var full = EnvOf(("DECO_STORE_TENANT_ID", "t"), ("DECO_STORE_SELLER_ID", "s"), ("DECO_STORE_CLIENT_ID", "c"), ("DECO_STORE_CLIENT_SECRET", "sh"));
        var pass = DoctorChecks.EntraEnv(k => full.TryGetValue(k, out string? v) ? v : null);
        Assert.Equal("pass", pass.Status);

        var none = DoctorChecks.EntraEnv(k => null);
        Assert.Equal("warn", none.Status);
        Assert.Contains("TenantId", none.Detail);
        Assert.Contains("DECO_STORE_TENANT_ID", none.Fix);
    }

    [Fact]
    public void Manifest_FoundAndMissing()
    {
        var found = DoctorChecks.Manifest(Ctx(manifest: true, locales: ["en-US", "de-DE"], vmName: "Lab"));
        Assert.Equal("pass", found.Status);
        Assert.Contains("en-US, de-DE", found.Detail);

        var missing = DoctorChecks.Manifest(Ctx(manifest: false));
        Assert.Equal("warn", missing.Status);
        Assert.Contains("deco init", missing.Fix);
    }

    [Fact]
    public void CaptureVm_LegacyWarnsPinnedPasses()
    {
        var legacy = DoctorChecks.CaptureVm(Ctx(vmName: null), string.Empty, string.Empty);
        Assert.Equal("warn", legacy.Status);
        Assert.Contains("Formixa-Capture", legacy.Detail);

        var pinned = DoctorChecks.CaptureVm(Ctx(vmName: "Lab"), string.Empty, string.Empty);
        Assert.Equal("pass", pinned.Status);

        var flagged = DoctorChecks.CaptureVm(Ctx(vmName: "Lab"), "Flag", string.Empty);
        Assert.Equal("pass", flagged.Status);
        Assert.Contains("Flag", flagged.Detail);
    }

    [Fact]
    public void HyperV_EnabledDisabledUnknown()
    {
        var on = new FakeProc((exe, args) => Ok("Enabled"));
        Assert.Equal("pass", DoctorChecks.HyperV(on).Status);

        var off = new FakeProc((exe, args) => Ok("Disabled"));
        var offCheck = DoctorChecks.HyperV(off);
        Assert.Equal("warn", offCheck.Status);
        Assert.Contains("vm backend", offCheck.Fix);

        var dead = new FakeProc((exe, args) => throw new InvalidOperationException("denied"));
        Assert.Equal("warn", DoctorChecks.HyperV(dead).Status);

        var elevated = new FakeProc((exe, args) => Fail("Get-WindowsOptionalFeature : The requested operation requires elevation."));
        var elevCheck = DoctorChecks.HyperV(elevated);
        Assert.Equal("warn", elevCheck.Status);
        Assert.Contains("needs elevation", elevCheck.Detail);
        Assert.DoesNotContain("\n", elevCheck.Detail);
    }

    [Fact]
    public void VmExists_SkipPassWarn()
    {
        var runner = new FakeProc((exe, args) => Ok("Lab"));
        Assert.Equal("skip", DoctorChecks.VmExists(Ctx(vmName: "Lab"), runner, string.Empty, hyperVOn: false).Status);
        Assert.Equal("pass", DoctorChecks.VmExists(Ctx(vmName: "Lab"), runner, string.Empty, hyperVOn: true).Status);

        var gone = new FakeProc((exe, args) => Ok(string.Empty));
        var miss = DoctorChecks.VmExists(Ctx(vmName: "Lab"), gone, string.Empty, hyperVOn: true);
        Assert.Equal("warn", miss.Status);
        Assert.Contains("vm new", miss.Fix);
    }

    [Fact]
    public void PortalProfile_MissingWarns()
    {
        var check = DoctorChecks.PortalProfile(Ctx(appId: "deco-test-" + Guid.NewGuid().ToString("N")));
        Assert.Equal("warn", check.Status);
        Assert.Contains("portal-auth", check.Fix);
    }

    [Fact]
    public void PortalProfile_PresentPasses()
    {
        string appId = "deco-test-" + Guid.NewGuid().ToString("N");
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appId, "portal-profile");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Cookies"), "x");
        try
        {
            var check = DoctorChecks.PortalProfile(Ctx(appId: appId));
            Assert.Equal("pass", check.Status);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appId), recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void RunAll_HasTenChecksElevenWhenFull()
    {
        var runner = new FakeProc((exe, args) => Ok(string.Empty));
        var slim = DoctorChecks.RunAll(Ctx(manifest: false), runner, string.Empty, string.Empty, full: false, k => null, () => []);
        Assert.Equal(10, slim.Count);
        Assert.Equal(slim.Count, slim.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(slim, c => Assert.Contains(c.Status, new[] { "pass", "warn", "fail", "skip" }));

        var full = DoctorChecks.RunAll(Ctx(manifest: false), runner, string.Empty, string.Empty, full: true, k => null, () => [],
            loginProbe: () => new DoctorChecks.Check("portal-login", "pass", "stub", string.Empty));
        Assert.Equal(11, full.Count);
        Assert.Contains(full, c => c.Id == "portal-login");
    }
}
