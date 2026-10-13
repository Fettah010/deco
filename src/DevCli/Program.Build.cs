// Command handlers: Build. Shared helpers, types, Main, and the command tree stay in Program.cs.
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using DevCli.Commands;
using DevCli.Core;
using DevCli.Infrastructure;
using Spectre.Console;

static partial class Program
{
    private static void EnsureTool(IProcessRunner runner, string exe, string installHint)
    {
        try
        {
            runner.RunAsync(exe, ["--version"], timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        }
        catch
        {
            throw new DecoException($"'{exe}' not found. {installHint}", "tool-missing");
        }
    }

    private static string ReadAssemblyName(string csprojPath, string fallback)
    {
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(csprojPath);
            foreach (System.Xml.XmlNode n in doc.GetElementsByTagName("AssemblyName"))
            {
                if (!string.IsNullOrWhiteSpace(n.InnerText))
                {
                    return n.InnerText.Trim();
                }
            }
        }
        catch
        {
            // Fall through to AppId.
        }

        return fallback;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, f);
            string target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }

    private static int RunBuildMsix(string? project, string verbosity, string format, bool noAnsi,
        string version, string platform, string publisher, string certPath, string certPassword,
        string timestampUrl, string outputDir, string manifestSrc, string logoSrc,
        bool appInstaller, string installUrl, int hours, bool storeUpload,
        string[] require, bool validate, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        try
        {
            MsixPackaging.ValidatePlatform(platform);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        string arch = MsixPackaging.Arch(platform);
        string rid = MsixPackaging.Rid(platform);
        string msbuildPlatform = MsixPackaging.MsBuildPlatform(platform);

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(ctx.CsprojPath))
        {
            Console.Error.WriteLine($"Error: No .csproj found under {ctx.RepoRoot}.");
            return ExitCodes.Error;
        }

        if (string.IsNullOrWhiteSpace(manifestSrc))
        {
            manifestSrc = Path.Combine(ctx.RepoRoot, "Packaging", "Msix", "Package.appxmanifest");
        }

        if (string.IsNullOrWhiteSpace(logoSrc))
        {
            logoSrc = Path.Combine(ctx.RepoRoot, "Assets", "Logo.png");
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = ctx.MsixDir;
        }

        string packageVersion;
        try
        {
            packageVersion = MsixPackaging.ResolveVersion(version, () => ctx.Version ?? string.Empty);
            packageVersion = VersionQuad.ToMsixQuad(packageVersion);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        foreach (string path in new[] { ctx.CsprojPath, manifestSrc, logoSrc })
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Error: Missing required file: {path}");
                return ExitCodes.Error;
            }
        }

        IProcessRunner runner = new ProcessRunner();

        if (validate)
        {
            if (string.Equals(publisher.Trim(), "CN=YourPublisher", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Error: Publisher is still the CN=YourPublisher placeholder. Pass --publisher with your Partner Center Publisher ID (Store) or signing cert subject (sideload).");
                return ExitCodes.Error;
            }

            string manifestText = File.ReadAllText(manifestSrc, Encoding.UTF8);
            (string? idName, string? idPub) = MsixPackaging.ReadIdentity(manifestText);
            if (string.IsNullOrWhiteSpace(idName))
            {
                Console.Error.WriteLine("Error: Manifest Identity Name is empty.");
                return ExitCodes.Error;
            }

            if (string.IsNullOrWhiteSpace(idPub))
            {
                Console.Error.WriteLine("Error: Manifest Identity Publisher is empty.");
                return ExitCodes.Error;
            }

            Console.WriteLine($"Version quad OK: {packageVersion}");
            Console.WriteLine($"Manifest OK: {idName} / {idPub}");
            try
            {
                ProcessResult tags = runner.RunAsync("git", ["tag", "--list", "v*"], ctx.RepoRoot, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                Version? max = null;
                if (tags.ExitCode == 0)
                {
                    foreach (string t in tags.StdOut.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(t.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)");
                        if (!m.Success)
                        {
                            continue;
                        }

                        var cand = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), 0);
                        if (max is null || cand > max)
                        {
                            max = cand;
                        }
                    }
                }

                if (max is not null)
                {
                    if (new Version(packageVersion) <= max)
                    {
                        Console.Error.WriteLine($"Error: Version quad {packageVersion} does not increase over latest tag ({max}). Bump it first (deco version bump).");
                        return ExitCodes.Error;
                    }

                    Console.WriteLine($"Version increases over latest tag ({max}).");
                }
                else
                {
                    DevCli.Commands.CliConsole.Warn("no v* tags found, skipping increase check.", noAnsi);
                }
            }
            catch (Exception ex)
            {
                DevCli.Commands.CliConsole.Warn($"tag comparison skipped ({ex.Message})", noAnsi);
            }

            Console.WriteLine("Validate PASSED (publisher, version, manifest, tile source).");
            return ExitCodes.Success;
        }

        if (appInstaller && string.IsNullOrWhiteSpace(installUrl) && !dryRun)
        {
            Console.Error.WriteLine("Error: --app-installer needs --install-url (public base URL hosting the .msix).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        string? makeappx = SdkTools.Find("makeappx");
        if (makeappx is null && !dryRun)
        {
            Console.Error.WriteLine("Error: makeappx.exe not found. Install the Windows SDK (or run with --dry-run to validate staging only).");
            return ExitCodes.Error;
        }

        string staging = Path.Combine(Path.GetTempPath(), "msix-stage-" + Guid.NewGuid().ToString("N"));
        string publishDir = Path.Combine(staging, "publish");
        string appDir = Path.Combine(staging, "app");
        bool keepStaging = dryRun;

        try
        {
            DevCli.Commands.CliConsole.Info($"MSIX version: {packageVersion} ({arch}), publisher: {publisher}", noAnsi);
            DevCli.Commands.CliConsole.Info("==> dotnet publish", noAnsi);
            try
            {
                RunTool(runner, "dotnet",
                    ["publish", ctx.CsprojPath, "-c", "Release", $"-p:Platform={msbuildPlatform}",
                        "-r", rid, "--self-contained", "-o", publishDir],
                    ctx.RepoRoot, TimeSpan.FromMinutes(20), "dotnet publish");
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            string exeName = ReadAssemblyName(ctx.CsprojPath, ctx.AppId) + ".exe";
            string? exe = Directory.GetFiles(publishDir, exeName, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe is null)
            {
                Console.Error.WriteLine($"Error: Main exe '{exeName}' not found in publish output.");
                return ExitCodes.Error;
            }

            DevCli.Commands.CliConsole.Info($"Main exe: {Path.GetFileName(exe)}", noAnsi);
            CopyDirectory(publishDir, appDir);
            try
            {
                MsixPackaging.RenderTileArt(logoSrc, Path.Combine(appDir, "Assets"));
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            string stagedManifest;
            try
            {
                stagedManifest = MsixPackaging.PatchManifestText(
                    File.ReadAllText(manifestSrc, Encoding.UTF8), packageVersion, arch, publisher.Trim());
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            File.WriteAllText(Path.Combine(appDir, "AppxManifest.xml"), stagedManifest, new UTF8Encoding(false));

            string appPriStaged = Path.Combine(appDir, Path.GetFileNameWithoutExtension(exeName) + ".pri");
            string resPriStaged = Path.Combine(appDir, "resources.pri");
            string? makepri = SdkTools.Find("makepri");
            if (makepri is not null)
            {
                string? embedDir = FindEmbedDir(Path.Combine(ctx.RepoRoot, "obj"), msbuildPlatform);
                if (embedDir is null || !File.Exists(Path.Combine(embedDir, "embed.resfiles")))
                {
                    Console.Error.WriteLine($"Error: XBF embed tree not found under obj/{msbuildPlatform} (Release). Run dotnet build -c Release -p:Platform={msbuildPlatform} first.");
                    return ExitCodes.Error;
                }

                string packList = Path.Combine(staging, "pack.resfiles");
                File.WriteAllLines(packList,
                    Directory.GetFiles(Path.Combine(appDir, "Assets"), "*.png")
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                string mergeList = Path.Combine(staging, "merge.resfiles");
                string? sdkMerge = Directory.GetFiles(Path.Combine(ctx.RepoRoot, "obj"), "pri.resfiles", SearchOption.AllDirectories)
                    .Where(p => p.Contains(msbuildPlatform, StringComparison.OrdinalIgnoreCase)
                        && p.Contains("Release", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                    .FirstOrDefault();
                if (sdkMerge is not null)
                {
                    File.Copy(sdkMerge, mergeList, overwrite: true);
                }
                else
                {
                    File.WriteAllLines(mergeList,
                        Directory.GetFiles(appDir, "*.pri")
                            .Where(p => !string.Equals(p, appPriStaged, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(p, resPriStaged, StringComparison.OrdinalIgnoreCase)));
                }

                string priConfig = Path.Combine(staging, "priconfig-pack.xml");
                File.WriteAllText(priConfig, MsixPackaging.PriconfigXml(
                    packList, embedDir, Path.Combine(embedDir, "embed.resfiles"), mergeList), new UTF8Encoding(false));

                try
                {
                    RunTool(runner, makepri,
                        ["new", "/pr", appDir, "/cf", priConfig, "/mn", Path.Combine(appDir, "AppxManifest.xml"),
                            "/of", resPriStaged, "/o"],
                        null, TimeSpan.FromMinutes(10), "makepri new");
                    RunTool(runner, makepri,
                        ["dump", "/if", resPriStaged, "/of", Path.Combine(staging, "pridump.xml")],
                        null, TimeSpan.FromMinutes(5), "makepri dump");
                }
                catch (DecoException ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return ex.ExitCode;
                }

                try
                {
                    var priDoc = new System.Xml.XmlDocument();
                    priDoc.Load(Path.Combine(staging, "pridump.xml"));
                    var maps = new List<string>();
                    foreach (System.Xml.XmlNode n in priDoc.SelectNodes("//*[local-name()='ResourceMap']")!)
                    {
                        string? name = n.Attributes?["name"]?.Value;
                        if (name is not null)
                        {
                            maps.Add(name);
                        }
                    }

                    (string? pkgName, _) = MsixPackaging.ReadIdentity(stagedManifest);
                    if (!maps.Contains(pkgName ?? string.Empty))
                    {
                        Console.Error.WriteLine($"Error: Staged resources.pri has no map named '{pkgName}' (WACK REQ19).");
                        return ExitCodes.Error;
                    }

                    var resNames = new HashSet<string>();
                    foreach (System.Xml.XmlNode n in priDoc.SelectNodes("//*[local-name()='NamedResource']")!)
                    {
                        string? name = n.Attributes?["name"]?.Value;
                        if (name is not null)
                        {
                            resNames.Add(name);
                        }
                    }

                    foreach (string need in new[] { "SplashScreen.xbf", "MainWindow.xbf", "App.xbf", "Logo.png", "themeresources.xbf" })
                    {
                        if (!resNames.Contains(need))
                        {
                            Console.Error.WriteLine($"Error: Staged resources.pri is missing {need}.");
                            return ExitCodes.Error;
                        }
                    }

                    DevCli.Commands.CliConsole.Success($"resources.pri map OK: {pkgName} (XBF verified)", noAnsi);
                }
                catch (DecoException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: Could not verify resources.pri: {ex.Message}");
                    return ExitCodes.Error;
                }
            }
            else
            {
                DevCli.Commands.CliConsole.Warn("makepri.exe not found - resources.pri falls back to <App>.pri bytes (map name unchecked).", noAnsi);
                if (File.Exists(appPriStaged))
                {
                    File.Copy(appPriStaged, resPriStaged, overwrite: true);
                }
            }

            string priName = Path.GetFileNameWithoutExtension(exeName) + ".pri";
            List<string> missing = MsixPackaging.MissingStagingFiles(appDir, exeName, priName, require);
            if (missing.Count > 0)
            {
                Console.Error.WriteLine("Error: Staging incomplete, missing: " + string.Join(", ", missing));
                return ExitCodes.Error;
            }

            if (arch == "arm64")
            {
                DevCli.Commands.CliConsole.Info("ARM64: native OCR/CV binaries are warning-only; CPU-fallback paths stay active.", noAnsi);
            }

            try
            {
                var check = new System.Xml.XmlDocument();
                check.Load(Path.Combine(appDir, "AppxManifest.xml"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Staged manifest is not well-formed XML: {ex.Message}");
                return ExitCodes.Error;
            }

            DevCli.Commands.CliConsole.Success($"Staging valid: {appDir}", noAnsi);

            string msixFileName = MsixPackaging.MsixFileName(ctx.AppId, packageVersion, arch);
            string installerFileName = Path.GetFileNameWithoutExtension(msixFileName) + ".appinstaller";
            string? installerXml = null;
            if (appInstaller)
            {
                string feedBase = installUrl;
                if (string.IsNullOrWhiteSpace(feedBase))
                {
                    feedBase = "https://example.com/msix/";
                }

                if (!feedBase.EndsWith('/'))
                {
                    feedBase += "/";
                }

                (string? pkgName, _) = MsixPackaging.ReadIdentity(stagedManifest);
                if (string.IsNullOrWhiteSpace(pkgName))
                {
                    Console.Error.WriteLine("Error: Staged manifest has no Identity Name.");
                    return ExitCodes.Error;
                }

                string mainUri = feedBase + msixFileName;
                try
                {
                    installerXml = MsixPackaging.BuildAppInstallerXml(
                        mainUri, packageVersion, pkgName, publisher.Trim(), arch, hours == 0 ? 12 : hours);
                }
                catch (DecoException ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return ex.ExitCode;
                }

                Console.WriteLine($"AppInstaller feed valid ({installerFileName} -> {mainUri})");
            }

            if (storeUpload && dryRun)
            {
                Console.WriteLine($"DryRun: would bundle {Path.GetFileNameWithoutExtension(msixFileName)}.msixbundle and wrap .msixupload (needs makeappx).");
            }

            if (dryRun)
            {
                Console.WriteLine($"DryRun: skipping makeappx (and signing). Staging kept at: {appDir}");
                if (format == "json")
                {
                    EmitJson(new
                    {
                        tool = CliConstants.ToolName,
                        version = CliConstants.Version,
                        command = "build msix",
                        dryRun = true,
                        msix = msixFileName,
                        staging = appDir,
                        appInstaller = installerFileName,
                    });
                }

                return ExitCodes.Success;
            }

            Directory.CreateDirectory(outputDir);
            string msixPath = Path.Combine(outputDir, msixFileName);
            if (File.Exists(msixPath))
            {
                File.Delete(msixPath);
            }

            Console.WriteLine("==> makeappx pack");
            try
            {
                RunTool(runner, makeappx!, MsixPackaging.MakeAppxPackArgs(appDir, msixPath),
                    null, TimeSpan.FromMinutes(10), "makeappx pack");
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            DevCli.Commands.CliConsole.Success($"Packed: {msixPath}", noAnsi);

            if (!string.IsNullOrWhiteSpace(certPath))
            {
                string? signtool = SdkTools.Find("signtool");
                if (signtool is null)
                {
                    Console.Error.WriteLine("Error: signtool.exe not found (Windows SDK required for signing).");
                    return ExitCodes.Error;
                }

                Console.WriteLine("==> signtool sign");
                try
                {
                    RunTool(runner, signtool, MsixPackaging.SignArgs(certPath, certPassword, timestampUrl, msixPath),
                        null, TimeSpan.FromMinutes(5), "signtool sign");
                }
                catch (DecoException ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return ex.ExitCode;
                }

                Console.WriteLine($"Signed: {msixPath}");
            }
            else
            {
                DevCli.Commands.CliConsole.Warn("unsigned package (installable only after signing, even self-signed).", noAnsi);
            }

            if (appInstaller && installerXml is not null)
            {
                File.WriteAllText(Path.Combine(outputDir, installerFileName), installerXml, new UTF8Encoding(false));
                Console.WriteLine($"Wrote: {installerFileName}");
            }

            if (storeUpload)
            {
                string bundleBase = Path.GetFileNameWithoutExtension(msixFileName);
                string bundleDir = Path.Combine(staging, "bundle");
                Directory.CreateDirectory(bundleDir);
                File.Copy(msixPath, Path.Combine(bundleDir, Path.GetFileName(msixPath)), overwrite: true);
                string bundle = Path.Combine(outputDir, bundleBase + ".msixbundle");
                if (File.Exists(bundle))
                {
                    File.Delete(bundle);
                }

                Console.WriteLine("==> makeappx bundle");
                try
                {
                    RunTool(runner, makeappx!, MsixPackaging.MakeAppxBundleArgs(bundleDir, bundle),
                        null, TimeSpan.FromMinutes(10), "makeappx bundle");
                }
                catch (DecoException ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    return ex.ExitCode;
                }

                DevCli.Commands.CliConsole.Success($"Bundled: {bundle}", noAnsi);
                if (!string.IsNullOrWhiteSpace(certPath))
                {
                    string? signtool = SdkTools.Find("signtool");
                    Console.WriteLine("==> signtool sign (bundle)");
                    try
                    {
                        RunTool(runner, signtool!, MsixPackaging.SignArgs(certPath, certPassword, timestampUrl, bundle),
                            null, TimeSpan.FromMinutes(5), "signtool sign (bundle)");
                    }
                    catch (DecoException ex)
                    {
                        Console.Error.WriteLine($"Error: {ex.Message}");
                        return ex.ExitCode;
                    }

                    Console.WriteLine($"Signed: {bundle}");
                }

                string upload = Path.Combine(outputDir, bundleBase + ".msixupload");
                if (File.Exists(upload))
                {
                    File.Delete(upload);
                }

                System.IO.Compression.ZipFile.CreateFromDirectory(bundleDir, upload);
                DevCli.Commands.CliConsole.Success($"Store upload wrapped: {upload} (bundle only - the Store signs it)", noAnsi);
            }

            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "build msix",
                    dryRun = false,
                    msix = msixPath,
                });
            }

            return ExitCodes.Success;
        }
        finally
        {
            if (!keepStaging)
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch
                {
                    // Staging cleanup is best effort.
                }
            }
        }
    }

    private static string? FindEmbedDir(string objRoot, string msbuildPlatform)
    {
        try
        {
            return Directory.GetDirectories(objRoot, "embed", SearchOption.AllDirectories)
                .Where(p => p.Contains(msbuildPlatform, StringComparison.OrdinalIgnoreCase)
                    && p.Contains("Release", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(p, "embed.resfiles")))
                .OrderByDescending(p => Directory.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int RunBuildVelopack(string? project, string verbosity, string format, bool noAnsi,
        string version, string channel, string runtime, string releaseNotes,
        bool skipBuild, bool download, bool publish, bool preRelease, string tag, string repo, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(version))
        {
            Console.Error.WriteLine("Error: --version is required (e.g. -v 1.0.1).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(ctx.CsprojPath))
        {
            Console.Error.WriteLine($"Error: No .csproj found under {ctx.RepoRoot}.");
            return ExitCodes.Error;
        }

        string repoUrl = string.IsNullOrWhiteSpace(repo)
            ? Environment.GetEnvironmentVariable("VPK_REPO_URL") ?? string.Empty
            : repo;
        string token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
        string mainExe = ctx.AppId + ".exe";
        string publishDir = VelopackFlow.PublishDirFor(ctx.RepoRoot, "net10.0-windows10.0.19041.0", runtime);
        string releasesDir = Path.Combine(ctx.RepoRoot, "Releases");
        string effTag = VelopackFlow.DefaultTag(version.Trim(), tag.Trim());
        string iconPath = Path.Combine(ctx.RepoRoot, "Assets", "app.ico");
        string? iconArg = File.Exists(iconPath) ? iconPath : null;

        if (!string.IsNullOrWhiteSpace(releaseNotes) && !File.Exists(releaseNotes))
        {
            Console.Error.WriteLine($"Error: Release notes file not found: {releaseNotes}");
            return ExitCodes.Error;
        }

        var steps = new List<string>();
        if (!skipBuild)
        {
            steps.Add("dotnet " + string.Join(" ", VelopackFlow.PublishArgs(ctx.CsprojPath, runtime, "x64", publishDir)));
        }

        if (download && !string.IsNullOrWhiteSpace(token))
        {
            steps.Add("vpk " + string.Join(" ", VelopackFlow.VpkDownloadArgs(repoUrl, releasesDir, channel)));
        }

        steps.Add("vpk " + string.Join(" ", VelopackFlow.VpkPackArgs(
            ctx.AppId, version.Trim(), publishDir, releasesDir, channel, runtime, mainExe,
            ctx.AppId, "Fettah", iconArg, string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes)));

        bool willUpload = !string.IsNullOrWhiteSpace(token);
        if (willUpload)
        {
            steps.Add("vpk " + string.Join(" ", VelopackFlow.VpkUploadArgs(
                repoUrl, releasesDir, channel, $"{ctx.AppId} {version.Trim()}", effTag, publish, preRelease)));
        }

        if (dryRun)
        {
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "build velopack",
                    dryRun = true,
                    steps,
                    skippedUpload = !willUpload,
                });
                return ExitCodes.Success;
            }

            Console.WriteLine($"[dry-run] would run {steps.Count} step(s):");
            foreach (string s in steps)
            {
                Console.WriteLine("  " + s);
            }

            if (!willUpload)
            {
                Console.WriteLine("GITHUB_TOKEN is not set - upload would be skipped (run deco github upload later).");
            }

            return ExitCodes.Success;
        }

        IProcessRunner runner = new ProcessRunner();
        try
        {
            EnsureTool(runner, "vpk", "Install it: dotnet tool install --global vpk");
            if (!skipBuild)
            {
                DevCli.Commands.CliConsole.Info("==> dotnet publish (self-contained, WinUI + WinAppSDK bundled)", noAnsi);
                RunTool(runner, "dotnet", VelopackFlow.PublishArgs(ctx.CsprojPath, runtime, "x64", publishDir),
                    ctx.RepoRoot, TimeSpan.FromMinutes(20), "dotnet publish");
            }

            if (!File.Exists(Path.Combine(publishDir, mainExe)))
            {
                Console.Error.WriteLine($"Error: Publish output not found: {Path.Combine(publishDir, mainExe)}");
                return ExitCodes.Error;
            }

            if (download && !string.IsNullOrWhiteSpace(token))
            {
                Console.WriteLine("==> vpk download github (previous release for delta generation)");
                try
                {
                    RunTool(runner, "vpk", VelopackFlow.VpkDownloadArgs(repoUrl, releasesDir, channel),
                        null, TimeSpan.FromMinutes(10), "vpk download");
                }
                catch (DecoException ex)
                {
                    DevCli.Commands.CliConsole.Warn($"{ex.Message} - continuing without deltas.", noAnsi);
                }
            }

            Console.WriteLine("==> vpk pack");
            RunTool(runner, "vpk", VelopackFlow.VpkPackArgs(
                ctx.AppId, version.Trim(), publishDir, releasesDir, channel, runtime, mainExe,
                ctx.AppId, "Fettah", iconArg, string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes),
                null, TimeSpan.FromMinutes(20), "vpk pack");

            if (!willUpload)
            {
                Console.WriteLine($"GITHUB_TOKEN is not set - skipping upload. Release is ready in {releasesDir}");
                Console.WriteLine("Run deco github upload after exporting GITHUB_TOKEN.");
                return ExitCodes.Success;
            }

            Console.WriteLine("==> vpk upload github");
            RunTool(runner, "vpk", VelopackFlow.VpkUploadArgs(
                repoUrl, releasesDir, channel, $"{ctx.AppId} {version.Trim()}", effTag, publish, preRelease),
                null, TimeSpan.FromMinutes(60), "vpk upload");
            Console.WriteLine($"Done. Release {version.Trim()} published to {repoUrl}");
            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
    }

    private static int RunGithubUpload(string? project, string verbosity, string format, bool noAnsi,
        string channel, string tag, string title, string repo, string dir, string[] files,
        string notesFile, bool publish, bool preRelease, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            string? env = Environment.GetEnvironmentVariable("VPK_REPO_URL");
            if (!string.IsNullOrWhiteSpace(env))
            {
                int idx = env.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
                repo = idx >= 0 ? env[(idx + "github.com/".Length)..].TrimEnd('/') : env;
            }
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(ctx.RepoRoot, "Releases");
        }

        string[] assets = files.Length > 0
            ? files
            : Directory.Exists(dir) ? Directory.GetFiles(dir) : [];

        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"{ctx.AppId} {tag}";
        }

        var plan = new GithubUploader.Plan(tag.Trim(), title, assets, repo.Trim(), notesFile, publish, preRelease);
        try
        {
            GithubUploader.Validate(plan, File.Exists);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (dryRun)
        {
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "github upload",
                    dryRun = true,
                    tag = plan.Tag,
                    repo = plan.Repo,
                    files = plan.Files,
                });
                return ExitCodes.Success;
            }

            Console.WriteLine($"[dry-run] would upload {plan.Files.Length} asset(s) to {plan.Repo}@{plan.Tag}:");
            foreach (string f in plan.Files)
            {
                Console.WriteLine("  " + f);
            }

            return ExitCodes.Success;
        }

        IProcessRunner runner = new ProcessRunner();
        try
        {
            EnsureTool(runner, "gh", "Install it: https://cli.github.com/");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GithubUploader.Result result = GithubUploader.UploadAsync(
                runner, plan, Console.WriteLine).GetAwaiter().GetResult();
            sw.Stop();
            double totalMb = Math.Round(result.TotalBytes / (1024.0 * 1024.0), 1);
            double rate = sw.Elapsed.TotalSeconds > 0 ? Math.Round(totalMb / sw.Elapsed.TotalSeconds, 2) : 0;
            string totals = $"Upload totals: {totalMb} MB in {Math.Round(sw.Elapsed.TotalMinutes, 1)} min (effective {rate} MB/s).";
            Console.WriteLine($"All {plan.Files.Length} asset(s) uploaded to {plan.Tag}.");
            Console.WriteLine(totals);
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "github upload",
                    dryRun = false,
                    tag = plan.Tag,
                    uploaded = result.Uploaded,
                    totalMb,
                    rateMbS = rate,
                });
            }

            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
    }

    private static int RunSmoke(string? project, string verbosity, string format, bool noAnsi,
        string runtime, string baseVersion, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(ctx.CsprojPath))
        {
            Console.Error.WriteLine($"Error: No .csproj found under {ctx.RepoRoot}.");
            return ExitCodes.Error;
        }

        string updateVersion = BumpPatch(baseVersion.Trim());
        string publishDir = VelopackFlow.PublishDirFor(ctx.RepoRoot, "net10.0-windows10.0.19041.0", runtime);
        string releases = Path.Combine(ctx.RepoRoot, "Releases");
        string releasesLocal = Path.Combine(ctx.RepoRoot, "ReleasesLocal");
        string mainExe = ctx.AppId + ".exe";
        string iconPath = Path.Combine(ctx.RepoRoot, "Assets", "app.ico");
        string? iconArg = File.Exists(iconPath) ? iconPath : null;

        var steps = new List<string>
        {
            "dotnet " + string.Join(" ", VelopackFlow.PublishArgs(ctx.CsprojPath, runtime, "x64", publishDir)),
            "vpk " + string.Join(" ", VelopackFlow.VpkPackArgs(
                ctx.AppId, baseVersion.Trim(), publishDir, releases, "stable", runtime, mainExe,
                ctx.AppId, "Fettah", iconArg, null)),
            "dotnet " + string.Join(" ", VelopackFlow.PublishArgsWithVersion(
                ctx.CsprojPath, runtime, "x64", publishDir, updateVersion)),
            "vpk " + string.Join(" ", VelopackFlow.VpkPackArgs(
                ctx.AppId, updateVersion, publishDir, releasesLocal, "stable", runtime, mainExe,
                ctx.AppId, "Fettah", iconArg, null)),
        };

        if (dryRun)
        {
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "smoke",
                    dryRun = true,
                    steps,
                });
                return ExitCodes.Success;
            }

            Console.WriteLine("[dry-run] local smoke test would run:");
            foreach (string s in steps)
            {
                Console.WriteLine("  " + s);
            }

            Console.WriteLine($"Then install .\\Releases\\setup.exe and point the app feed at .\\ReleasesLocal\\.");
            return ExitCodes.Success;
        }

        IProcessRunner runner = new ProcessRunner();
        try
        {
            EnsureTool(runner, "vpk", "Install it: dotnet tool install --global vpk");
            Console.WriteLine($"==> publish {baseVersion.Trim()} + vpk pack (Releases/)");
            RunTool(runner, "dotnet", VelopackFlow.PublishArgs(ctx.CsprojPath, runtime, "x64", publishDir),
                ctx.RepoRoot, TimeSpan.FromMinutes(20), "dotnet publish");
            RunTool(runner, "vpk", VelopackFlow.VpkPackArgs(
                ctx.AppId, baseVersion.Trim(), publishDir, releases, "stable", runtime, mainExe,
                ctx.AppId, "Fettah", iconArg, null), null, TimeSpan.FromMinutes(20), "vpk pack");
            Console.WriteLine($"==> publish {updateVersion} + vpk pack (ReleasesLocal/)");
            RunTool(runner, "dotnet", VelopackFlow.PublishArgsWithVersion(
                ctx.CsprojPath, runtime, "x64", publishDir, updateVersion),
                ctx.RepoRoot, TimeSpan.FromMinutes(20), "dotnet publish (update)");
            RunTool(runner, "vpk", VelopackFlow.VpkPackArgs(
                ctx.AppId, updateVersion, publishDir, releasesLocal, "stable", runtime, mainExe,
                ctx.AppId, "Fettah", iconArg, null), null, TimeSpan.FromMinutes(20), "vpk pack (update)");
            Console.WriteLine("Local smoke-test complete.");
            Console.WriteLine("  Install:  .\\Releases\\setup.exe");
            Console.WriteLine("  Update:   .\\ReleasesLocal\\  (point the app feed here for a local update demo)");
            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
    }

    private static string BumpPatch(string version)
    {
        string plain = version.Split('-', 2)[0];
        string[] parts = plain.Split('.');
        if (parts.Length >= 3 && int.TryParse(parts[2], out int patch))
        {
            parts[2] = (patch + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(".", parts);
        }

        return plain + ".1";
    }

}
