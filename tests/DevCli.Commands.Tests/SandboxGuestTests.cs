using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class SandboxGuestTests
{
    [Fact]
    public void Bootstrap_MatchesGuestScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? script = null;
        while (dir is not null && script is null)
        {
            string cand = Path.Combine(dir.FullName, "guest", "capture.ps1");
            if (File.Exists(cand))
            {
                script = cand;
            }

            dir = dir.Parent;
        }

        Assert.True(script is not null, "guest/capture.ps1 not found above test binaries.");
        static string Norm(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        Assert.Equal(Norm(File.ReadAllText(script!)), Norm(SandboxGuest.Bootstrap));
    }
}
