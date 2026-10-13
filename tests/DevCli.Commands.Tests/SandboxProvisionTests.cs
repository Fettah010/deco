using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class SandboxProvisionTests : IDisposable
{
    private readonly string _tmp;

    public SandboxProvisionTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-sbprov-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Func<string, byte[]> _respond;
        public int Calls;

        public FakeHttp(Func<string, byte[]> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_respond(request.RequestUri!.ToString())),
            });
        }
    }

    private static byte[] ZipOf(params (string Path, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, data) in entries)
            {
                var e = zip.CreateEntry(path);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    private static byte[] ZipFor(string libPath) => ZipOf((libPath, [0x4D, 0x5A, 0x90, 0x00]));

    [Fact]
    public void Packages_HasThreePinnedEntries()
    {
        var pkgs = SandboxProvision.Packages();

        Assert.Equal(3, pkgs.Count);
        Assert.Contains(pkgs, p => p.FileName == "FlaUI.Core.dll" && p.LibPath == "lib/net48/FlaUI.Core.dll");
        Assert.Contains(pkgs, p => p.FileName == "FlaUI.UIA3.dll");
        Assert.Contains(pkgs, p => p.FileName == "Interop.UIAutomationClient.dll" && p.LibPath == "lib/net45/Interop.UIAutomationClient.dll");
        foreach (var p in pkgs)
        {
            Assert.Contains("v3-flatcontainer", SandboxProvision.NupkgUrl(p));
            Assert.Contains(p.Version, SandboxProvision.NupkgUrl(p));
        }
    }

    [Fact]
    public void DepsDir_IsVersioned()
    {
        string dir = SandboxProvision.DepsDir(_tmp);
        Assert.Equal(Path.Combine(_tmp, "deco", "sandbox-guest", "flaui-5.0.0"), dir);
    }

    [Fact]
    public void IsProvisioned_FalseWhenEmpty()
    {
        Assert.False(SandboxProvision.IsProvisioned(Path.Combine(_tmp, "nope")));
    }

    [Fact]
    public async Task ProvisionAsync_LaysOutFilesAndManifest()
    {
        var fake = new FakeHttp(url => ZipFor(
            url.Contains("flaui.core") ? "lib/net48/FlaUI.Core.dll"
            : url.Contains("flaui.uia3") ? "lib/net48/FlaUI.UIA3.dll"
            : "lib/net45/Interop.UIAutomationClient.dll"));
        string dir = Path.Combine(_tmp, "deps");

        SandboxProvision.DepsManifest m = await SandboxProvision.ProvisionAsync(
            dir, SandboxProvision.FlaUIVersion, new HttpClient(fake), force: false, log: null);

        Assert.Equal(3, m.Files.Count);
        Assert.Equal("5.0.0", m.FlaUIVersion);
        foreach (var f in m.Files)
        {
            Assert.True(File.Exists(Path.Combine(dir, f.Name)));
        }

        Assert.True(SandboxProvision.IsProvisioned(dir));
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task ProvisionAsync_SkipsWhenProvisioned()
    {
        string dir = Path.Combine(_tmp, "deps");
        Directory.CreateDirectory(dir);
        await SandboxProvision.ProvisionAsync(dir, SandboxProvision.FlaUIVersion,
            new HttpClient(new FakeHttp(url => ZipFor(
                url.Contains("flaui.core") ? "lib/net48/FlaUI.Core.dll"
                : url.Contains("flaui.uia3") ? "lib/net48/FlaUI.UIA3.dll"
                : "lib/net45/Interop.UIAutomationClient.dll"))),
            force: false, log: null);

        var counting = new FakeHttp(url => throw new InvalidOperationException("must not download"));
        SandboxProvision.DepsManifest m = await SandboxProvision.ProvisionAsync(
            dir, SandboxProvision.FlaUIVersion, new HttpClient(counting), force: false, log: null);

        Assert.Equal(0, counting.Calls);
        Assert.Equal(3, m.Files.Count);
    }

    [Fact]
    public async Task ProvisionAsync_MissingEntryThrowsLayout()
    {
        var fake = new FakeHttp(url => ZipOf(("lib/net48/Other.dll", [0x00])));
        var ex = await Assert.ThrowsAsync<Core.DecoException>(() => SandboxProvision.ProvisionAsync(
            Path.Combine(_tmp, "deps"), SandboxProvision.FlaUIVersion, new HttpClient(fake), force: true, log: null));
        Assert.Equal("sandbox-provision-layout", ex.Reason);
    }
}
