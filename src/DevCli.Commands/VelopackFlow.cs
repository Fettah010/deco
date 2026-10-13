namespace DevCli.Commands;

/// <summary>
/// Pure argument builders for the Velopack flow (build-and-release.ps1,
/// local-smoke-test.ps1, upload-github.ps1): dotnet publish, vpk download /
/// pack / upload-github. Process execution stays in the handler.
/// </summary>
public static class VelopackFlow
{
    public static List<string> PublishArgs(string csproj, string runtime, string msbuildPlatform, string outDir) =>
        ["publish", csproj, "-c", "Release", $"-p:Platform={msbuildPlatform}", "-r", runtime,
            "--self-contained", "-o", outDir];

    public static List<string> PublishArgsWithVersion(
        string csproj, string runtime, string msbuildPlatform, string outDir, string version) =>
        [.. PublishArgs(csproj, runtime, msbuildPlatform, outDir),
            $"/p:Version={version}", $"/p:AssemblyVersion={version}.0", $"/p:FileVersion={version}.0"];

    public static List<string> VpkDownloadArgs(string repoUrl, string releasesDir, string channel) =>
        ["download", "github", "--repoUrl", repoUrl, "-o", releasesDir, "-c", channel];

    public static List<string> VpkPackArgs(
        string appId, string version, string publishDir, string releasesDir,
        string channel, string runtime, string mainExe,
        string packTitle, string packAuthors, string? iconPath, string? releaseNotes)
    {
        var args = new List<string>
        {
            "pack", "-u", appId, "-v", version, "-p", publishDir,
            "-o", releasesDir, "-c", channel, "-r", runtime, "-e", mainExe,
            "--packTitle", packTitle, "--packAuthors", packAuthors,
        };
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            args.AddRange(["--icon", iconPath]);
        }

        if (!string.IsNullOrWhiteSpace(releaseNotes))
        {
            args.AddRange(["--releaseNotes", releaseNotes]);
        }

        return args;
    }

    public static List<string> VpkUploadArgs(
        string repoUrl, string releasesDir, string channel, string releaseName, string tag,
        bool publish, bool preRelease)
    {
        var args = new List<string>
        {
            "upload", "github", "--repoUrl", repoUrl,
            "-o", releasesDir, "-c", channel,
            "--releaseName", releaseName, "--tag", tag,
        };
        if (publish)
        {
            args.Add("--publish");
        }

        if (preRelease)
        {
            args.Add("--pre");
        }

        return args;
    }

    public static string DefaultTag(string version, string tag) =>
        string.IsNullOrWhiteSpace(tag) ? $"v{version}" : tag;

    public static string PublishDirFor(string projectRoot, string tfm, string runtime) =>
        Path.Combine(projectRoot, "bin", "Release", tfm, runtime, "publish");
}
