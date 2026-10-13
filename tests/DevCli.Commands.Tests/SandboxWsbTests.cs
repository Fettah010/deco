using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class SandboxWsbTests
{
    [Fact]
    public void Build_EmitsMemoryNetworkingMappingsLogon()
    {
        string xml = SandboxWsb.Build(
            4096, networking: false,
            [new SandboxWsb.Mapping("C:\\work\\in", "C:\\deco-stage", false)],
            "powershell -File C:\\deco-stage\\go.ps1");

        Assert.Contains("<MemoryInMB>4096</MemoryInMB>", xml);
        Assert.Contains("<Networking>Disable</Networking>", xml);
        Assert.Contains("<HostFolder>C:\\work\\in</HostFolder>", xml);
        Assert.Contains("<SandboxFolder>C:\\deco-stage</SandboxFolder>", xml);
        Assert.Contains("<ReadOnly>false</ReadOnly>", xml);
        Assert.Contains("<LogonCommand><Command>powershell -File C:\\deco-stage\\go.ps1</Command></LogonCommand>", xml);
        Assert.Contains("<VGpu>Disable</VGpu>", xml);
    }

    [Fact]
    public void Build_VGpuOptIn()
    {
        string xml = SandboxWsb.Build(
            4096, networking: true, [], string.Empty, vgpu: true);

        Assert.Contains("<VGpu>Enable</VGpu>", xml);
        Assert.DoesNotContain("<VGpu>Disable</VGpu>", xml);
    }

    [Fact]
    public void Build_OmitsOptionals()
    {
        string xml = SandboxWsb.Build(0, networking: true, [], string.Empty);

        Assert.DoesNotContain("MemoryInMB", xml);
        Assert.DoesNotContain("MappedFolders", xml);
        Assert.DoesNotContain("LogonCommand", xml);
        Assert.Contains("<Networking>Default</Networking>", xml);
    }

    [Fact]
    public void Build_EscapesXml()
    {
        string xml = SandboxWsb.Build(
            2048, networking: true,
            [new SandboxWsb.Mapping("C:\\A&B", "C:\\stage", true)],
            "run <fast> & go");

        Assert.Contains("C:\\A&amp;B", xml);
        Assert.Contains("run &lt;fast&gt; &amp; go", xml);
    }
}
