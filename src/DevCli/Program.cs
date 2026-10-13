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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static async Task<int> Main(string[] args)
    {
        var projectOption = new Option<string>("--project")
        {
            Description = "Target repo folder or .csproj file (default: current directory, walk-up discovery).",
            Recursive = true,
        };
        var verbosityOption = new Option<string>("--verbosity")
        {
            Description = "Output detail: quiet|minimal|normal|detailed (default: normal).",
            Recursive = true,
        };
        var formatOption = new Option<string>("--format")
        {
            Description = "Output shape: table|json (default: table).",
            Recursive = true,
        };
        var noAnsiOption = new Option<bool>("--no-ansi")
        {
            Description = "Disable ANSI styling (plain text, for CI logs; NO_COLOR is honored too).",
            Recursive = true,
        };

        var root = new RootCommand($"{CliConstants.ToolTitle} ({CliConstants.ToolName}) — build, release, Store and VM workflows for any DevEcosystem app.");
        root.Options.Add(projectOption);
        root.Options.Add(verbosityOption);
        root.Options.Add(formatOption);
        root.Options.Add(noAnsiOption);

        var info = new Command("info", "Show the resolved project context (.devcli.json + csproj versions).");
        root.Subcommands.Add(info);
        info.SetAction((ParseResult pr) => RunInfo(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption)));

        var doctor = new Command("doctor", "Check the environment + repo config for release readiness (changes nothing). E.g. deco doctor --full");
        var dVm = new Option<string>("--vm") { Description = "Check a specific capture VM (default: manifest or legacy)." };
        var dUser = new Option<string>("--user") { Description = "Guest user for the VM check (default: manifest or legacy)." };
        var dFull = new Option<bool>("--full") { Description = "Include the live portal-login check (starts a headless browser, ~1 min)." };
        doctor.Options.Add(dVm);
        doctor.Options.Add(dUser);
        doctor.Options.Add(dFull);
        root.Subcommands.Add(doctor);
        doctor.SetAction((ParseResult pr) => RunDoctor(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(dVm) ?? string.Empty,
            pr.GetValue(dUser) ?? string.Empty,
            pr.GetValue(dFull)));

        var init = new Command("init", "Initialize a repo for deco (writes .devcli.json + Scripts/dev.ps1). E.g. deco init --app-id MyApp --locales en-US,fr-FR");
        var iAppId = new Option<string>("--app-id") { Description = "App ID (default: manifest, csproj, or folder name)." };
        var iStoreId = new Option<string>("--store-id") { Description = "Store Product ID (optional)." };
        var iCsproj = new Option<string>("--csproj") { Description = "Main .csproj path (default: auto-detected)." };
        var iLocales = new Option<string>("--locales") { Description = "Comma-separated BCP-47 locales (default: en-US)." };
        var iVmName = new Option<string>("--vm-name") { Description = "Capture VM name (optional)." };
        var iVmUser = new Option<string>("--vm-user") { Description = "Capture VM user (optional)." };
        var iForce = new Option<bool>("--force") { Description = "Overwrite existing .devcli.json / dev.ps1." };
        var iDryRun = new Option<bool>("--dry-run") { Description = "Print the plan, write nothing." };
        init.Options.Add(iAppId);
        init.Options.Add(iStoreId);
        init.Options.Add(iCsproj);
        init.Options.Add(iLocales);
        init.Options.Add(iVmName);
        init.Options.Add(iVmUser);
        init.Options.Add(iForce);
        init.Options.Add(iDryRun);
        root.Subcommands.Add(init);
        init.SetAction((ParseResult pr) => RunInit(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(iAppId) ?? string.Empty,
            pr.GetValue(iStoreId) ?? string.Empty,
            pr.GetValue(iCsproj) ?? string.Empty,
            pr.GetValue(iLocales) ?? string.Empty,
            pr.GetValue(iVmName) ?? string.Empty,
            pr.GetValue(iVmUser) ?? string.Empty,
            pr.GetValue(iForce),
            pr.GetValue(iDryRun)));

        var versionCmd = new Command("version", "Version utilities (csproj quad-sync, CHANGELOG stubs).");
        root.Subcommands.Add(versionCmd);
        var bump = new Command("bump", "Bump the app version across csproj + CITATION.cff + CHANGELOG.md. E.g. deco version bump 0.6.2-beta --dry-run");
        var newVersionArg = new Argument<string>("newVersion") { Description = "New version: 2-4 numeric parts, optional -suffix (e.g. 0.6.2 or 0.6.2-beta)." };
        var dateOption = new Option<string>("--date") { Description = "Release date yyyy-MM-dd for CITATION.cff/CHANGELOG (default: today)." };
        var bumpDryRun = new Option<bool>("--dry-run") { Description = "Print planned edits, write nothing." };
        bump.Arguments.Add(newVersionArg);
        bump.Options.Add(dateOption);
        bump.Options.Add(bumpDryRun);
        versionCmd.Subcommands.Add(bump);
        bump.SetAction((ParseResult pr) => RunVersionBump(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(newVersionArg) ?? string.Empty,
            pr.GetValue(dateOption) ?? string.Empty,
            pr.GetValue(bumpDryRun)));

        var weight = new Command("weight", "Publish win-x64 Release to temp and report size vs baseline. E.g. deco version weight --fail-on-jump");
        var failOnJump = new Option<bool>("--fail-on-jump") { Description = "Exit 1 on an unexplained >10% jump (CI guardrail); otherwise warn only." };
        var timeoutOption = new Option<int>("--timeout-sec") { Description = "dotnet publish timeout in seconds (default: 600)." };
        weight.Options.Add(failOnJump);
        weight.Options.Add(timeoutOption);
        versionCmd.Subcommands.Add(weight);
        weight.SetAction((ParseResult pr) => RunVersionWeight(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(failOnJump),
            pr.GetValue(timeoutOption)));

        var scaffold = new Command("scaffold", "Generate repo files (Store listings, protocol registration).");
        root.Subcommands.Add(scaffold);

        var listing = new Command("listing", "Draft Partner Center listing files from README.md. E.g. deco scaffold listing --dry-run");
        var outputOption = new Option<string>("--output") { Description = "Output folder (default: <repo>/Store/Listing)." };
        var listingDryRun = new Option<bool>("--dry-run") { Description = "Print planned files, write nothing." };
        listing.Options.Add(outputOption);
        listing.Options.Add(listingDryRun);
        scaffold.Subcommands.Add(listing);
        listing.SetAction((ParseResult pr) => RunScaffoldListing(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(outputOption) ?? string.Empty,
            pr.GetValue(listingDryRun)));

        var protocol = new Command("protocol", "Register the app deep-link URI scheme (HKCU, per-user). E.g. deco scaffold protocol --dry-run");
        var schemeOption = new Option<string>("--scheme") { Description = "URI scheme with :// (default: protocol from .devcli.json)." };
        var exeOption = new Option<string>("--exe") { Description = "App exe path (default: auto-find bin/**/<App>.exe)." };
        var unregisterOption = new Option<bool>("--unregister") { Description = "Remove the scheme instead of registering." };
        var protocolDryRun = new Option<bool>("--dry-run") { Description = "Print planned registry actions, write nothing." };
        protocol.Options.Add(schemeOption);
        protocol.Options.Add(exeOption);
        protocol.Options.Add(unregisterOption);
        protocol.Options.Add(protocolDryRun);
        scaffold.Subcommands.Add(protocol);
        protocol.SetAction((ParseResult pr) => RunScaffoldProtocol(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(schemeOption) ?? string.Empty,
            pr.GetValue(exeOption) ?? string.Empty,
            pr.GetValue(unregisterOption),
            pr.GetValue(protocolDryRun)));

        var icon = new Command("icon", "Regenerate app.ico + Logo PNGs from one square PNG. E.g. deco scaffold icon --source C:\\art\\logo.png --dry-run");
        var sourceOption = new Option<string>("--source") { Description = "Square source PNG, >= 512px per side (required)." };
        var assetsOption = new Option<string>("--assets-dir") { Description = "Target Assets folder (default: <repo>/Assets)." };
        var paddingOption = new Option<int>("--padding") { Description = "Transparent-margin trim 0-25% (default: 0)." };
        var iconDryRun = new Option<bool>("--dry-run") { Description = "Preview source analysis, write nothing." };
        icon.Options.Add(sourceOption);
        icon.Options.Add(assetsOption);
        icon.Options.Add(paddingOption);
        icon.Options.Add(iconDryRun);
        scaffold.Subcommands.Add(icon);
        icon.SetAction((ParseResult pr) => RunScaffoldIcon(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(sourceOption) ?? string.Empty,
            pr.GetValue(assetsOption) ?? string.Empty,
            pr.GetValue(paddingOption),
            pr.GetValue(iconDryRun)));

        var shortcut = new Command("shortcut", "Create desktop shortcuts (launch + dev loop). E.g. deco scaffold shortcut --dry-run");
        var nameOption = new Option<string>("--name") { Description = "Shortcut base name (default: app id)." };
        var shortcutExe = new Option<string>("--exe") { Description = "Built exe path (default: auto-find bin/**/<App>.exe)." };
        var devScriptOption = new Option<string>("--dev-script") { Description = "Dev launcher script (default: Scripts/run-app.ps1 else launch-dev.ps1)." };
        var outputDirOption = new Option<string>("--output-dir") { Description = "Where to write .lnk files (default: Desktop)." };
        var shortcutDryRun = new Option<bool>("--dry-run") { Description = "Print planned shortcuts, write nothing." };
        shortcut.Options.Add(nameOption);
        shortcut.Options.Add(shortcutExe);
        shortcut.Options.Add(devScriptOption);
        shortcut.Options.Add(outputDirOption);
        shortcut.Options.Add(shortcutDryRun);
        scaffold.Subcommands.Add(shortcut);
        shortcut.SetAction((ParseResult pr) => RunScaffoldShortcut(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(nameOption) ?? string.Empty,
            pr.GetValue(shortcutExe) ?? string.Empty,
            pr.GetValue(devScriptOption) ?? string.Empty,
            pr.GetValue(outputDirOption) ?? string.Empty,
            pr.GetValue(shortcutDryRun)));

        var splash = new Command("splash", "Render the Velopack Setup.exe splash (640x400). E.g. deco scaffold splash --version 1.0.0-beta --dry-run");
        var splashVersion = new Option<string>("--version") { Description = "Release version for the pill (required unless --static)." };
        var channelOption = new Option<string>("--channel") { Description = "Release channel: beta|stable (default: beta)." };
        var sizeOption = new Option<double>("--size-mb") { Description = "Payload size MB for the meta line (default: 0, omitted)." };
        var splashOutput = new Option<string>("--output") { Description = "Output PNG (default: <repo>/Releases/setup-splash.png)." };
        var splashLogo = new Option<string>("--logo") { Description = "Logo PNG path (default: <repo>/Assets/Logo.png)." };
        var staticOption = new Option<bool>("--static") { Description = "Timeless fallback: no version pill, no meta line." };
        var themeOption = new Option<string>("--theme") { Description = "Dark|Light (default: Dark)." };
        var splashDryRun = new Option<bool>("--dry-run") { Description = "Validate inputs, render nothing." };
        splash.Options.Add(splashVersion);
        splash.Options.Add(channelOption);
        splash.Options.Add(sizeOption);
        splash.Options.Add(splashOutput);
        splash.Options.Add(splashLogo);
        splash.Options.Add(staticOption);
        splash.Options.Add(themeOption);
        splash.Options.Add(splashDryRun);
        scaffold.Subcommands.Add(splash);
        splash.SetAction((ParseResult pr) => RunScaffoldSplash(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(splashVersion) ?? string.Empty,
            pr.GetValue(channelOption) ?? "beta",
            pr.GetValue(sizeOption),
            pr.GetValue(splashOutput) ?? string.Empty,
            pr.GetValue(splashLogo) ?? string.Empty,
            pr.GetValue(staticOption),
            pr.GetValue(themeOption) ?? "Dark",
            pr.GetValue(splashDryRun)));

        var build = new Command("build", "Build and package the app (MSIX, Velopack).");
        root.Subcommands.Add(build);

        var msix = new Command("msix", "Pack an MSIX (+ .appinstaller / .msixupload). E.g. deco build msix --validate --publisher \"CN=Acme\"");
        var msixVersion = new Option<string>("--version") { Description = "Package version (default: csproj <Version>)." };
        var platformOption = new Option<string>("--platform") { Description = "x64|arm64 (default: x64)." };
        var publisherOption = new Option<string>("--publisher") { Description = "Publisher ID (must match signing cert; placeholder rejected by --validate)." };
        var certOption = new Option<string>("--cert") { Description = "Signing .pfx path (unsigned without it)." };
        var certPassOption = new Option<string>("--cert-password") { Description = "Signing .pfx password (never logged)." };
        var timestampOption = new Option<string>("--timestamp-url") { Description = "RFC3161 timestamp URL." };
        var msixOutput = new Option<string>("--output") { Description = "Output folder (default: <repo>/Releases/Msix)." };
        var manifestOption = new Option<string>("--manifest") { Description = "Package.appxmanifest path (default: Packaging/Msix/Package.appxmanifest)." };
        var logoOption = new Option<string>("--logo") { Description = "Tile source Logo.png (default: Assets/Logo.png)." };
        var appInstallerOption = new Option<bool>("--app-installer") { Description = "Emit the .appinstaller update feed next to the package." };
        var installUrlOption = new Option<string>("--install-url") { Description = "Public base URL hosting the .msix (required with --app-installer)." };
        var hoursOption = new Option<int>("--hours") { Description = "HoursBetweenUpdateChecks (default: 12)." };
        var storeUploadOption = new Option<bool>("--store-upload") { Description = "Bundle + wrap as .msixupload (Store signs it)." };
        var requireOption = new Option<string[]>("--require") { Description = "Extra required staged file, repeatable (e.g. tessdata\\eng.traineddata)." };
        var validateOption = new Option<bool>("--validate") { Description = "Pre-flight only: publisher, version, manifest, tag increase. No SDK, no publish." };
        var msixDryRun = new Option<bool>("--dry-run") { Description = "Stage + validate everything short of makeappx (staging kept)." };
        msix.Options.Add(msixVersion);
        msix.Options.Add(platformOption);
        msix.Options.Add(publisherOption);
        msix.Options.Add(certOption);
        msix.Options.Add(certPassOption);
        msix.Options.Add(timestampOption);
        msix.Options.Add(msixOutput);
        msix.Options.Add(manifestOption);
        msix.Options.Add(logoOption);
        msix.Options.Add(appInstallerOption);
        msix.Options.Add(installUrlOption);
        msix.Options.Add(hoursOption);
        msix.Options.Add(storeUploadOption);
        msix.Options.Add(requireOption);
        msix.Options.Add(validateOption);
        msix.Options.Add(msixDryRun);
        build.Subcommands.Add(msix);
        msix.SetAction((ParseResult pr) => RunBuildMsix(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(msixVersion) ?? string.Empty,
            pr.GetValue(platformOption) ?? "x64",
            pr.GetValue(publisherOption) ?? "CN=YourPublisher",
            pr.GetValue(certOption) ?? string.Empty,
            pr.GetValue(certPassOption) ?? string.Empty,
            pr.GetValue(timestampOption) ?? "http://timestamp.digicert.com",
            pr.GetValue(msixOutput) ?? string.Empty,
            pr.GetValue(manifestOption) ?? string.Empty,
            pr.GetValue(logoOption) ?? string.Empty,
            pr.GetValue(appInstallerOption),
            pr.GetValue(installUrlOption) ?? string.Empty,
            pr.GetValue(hoursOption),
            pr.GetValue(storeUploadOption),
            pr.GetValue(requireOption) ?? [],
            pr.GetValue(validateOption),
            pr.GetValue(msixDryRun)));

        var velopack = new Command("velopack", "Publish + vpk pack (+ upload) a Velopack release. E.g. deco build velopack -v 1.0.1 -c stable --dry-run");
        var vVersion = new Option<string>("--version") { Description = "Release version (required)." };
        var vChannel = new Option<string>("--channel") { Description = "Release channel (default: stable)." };
        var vRuntime = new Option<string>("--runtime") { Description = "RID (default: win-x64)." };
        var vNotes = new Option<string>("--release-notes") { Description = "Markdown notes file path." };
        var vSkipBuild = new Option<bool>("--skip-build") { Description = "Skip dotnet publish." };
        var vDownload = new Option<bool>("--download") { Description = "Fetch previous release for deltas (needs GITHUB_TOKEN)." };
        var vPublish = new Option<bool>("--publish") { Description = "Create the GitHub release published (not draft)." };
        var vPre = new Option<bool>("--pre-release") { Description = "Mark the GitHub release as pre-release." };
        var vTag = new Option<string>("--tag") { Description = "Git tag (default: v<version>)." };
        var vRepo = new Option<string>("--repo") { Description = "Release repo URL (default: $VPK_REPO_URL)." };
        var vDryRun = new Option<bool>("--dry-run") { Description = "Print planned steps, run nothing." };
        velopack.Options.Add(vVersion);
        velopack.Options.Add(vChannel);
        velopack.Options.Add(vRuntime);
        velopack.Options.Add(vNotes);
        velopack.Options.Add(vSkipBuild);
        velopack.Options.Add(vDownload);
        velopack.Options.Add(vPublish);
        velopack.Options.Add(vPre);
        velopack.Options.Add(vTag);
        velopack.Options.Add(vRepo);
        velopack.Options.Add(vDryRun);
        build.Subcommands.Add(velopack);
        velopack.SetAction((ParseResult pr) => RunBuildVelopack(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(vVersion) ?? string.Empty,
            pr.GetValue(vChannel) ?? "stable",
            pr.GetValue(vRuntime) ?? "win-x64",
            pr.GetValue(vNotes) ?? string.Empty,
            pr.GetValue(vSkipBuild),
            pr.GetValue(vDownload),
            pr.GetValue(vPublish),
            pr.GetValue(vPre),
            pr.GetValue(vTag) ?? string.Empty,
            pr.GetValue(vRepo) ?? string.Empty,
            pr.GetValue(vDryRun)));

        var github = new Command("github", "Ship an already-packed Releases/ folder to GitHub Releases.");
        root.Subcommands.Add(github);
        var upload = new Command("upload", "Parallel gh upload (+ publish). E.g. deco github upload --tag v1.0.0 --repo owner/name --dry-run");
        var uChannel = new Option<string>("--channel") { Description = "Release channel (default: stable)." };
        var uTag = new Option<string>("--tag") { Description = "Release tag (required)." };
        var uTitle = new Option<string>("--title") { Description = "Release title (default: \"<App> <tag>\")." };
        var uRepo = new Option<string>("--repo") { Description = "owner/name (default: $VPK_REPO_URL path)." };
        var uDir = new Option<string>("--dir") { Description = "Packed folder (default: <repo>/Releases)." };
        var uFiles = new Option<string[]>("--files") { Description = "Explicit asset paths, repeatable (default: every file in --dir)." };
        var uNotesFile = new Option<string>("--notes-file") { Description = "Release notes file for new drafts." };
        var uPublish = new Option<bool>("--publish") { Description = "Un-draft (publish) after upload." };
        var uPre = new Option<bool>("--pre-release") { Description = "Mark pre-release." };
        var uDryRun = new Option<bool>("--dry-run") { Description = "Print planned gh calls, run nothing." };
        upload.Options.Add(uChannel);
        upload.Options.Add(uTag);
        upload.Options.Add(uTitle);
        upload.Options.Add(uRepo);
        upload.Options.Add(uDir);
        upload.Options.Add(uFiles);
        upload.Options.Add(uNotesFile);
        upload.Options.Add(uPublish);
        upload.Options.Add(uPre);
        upload.Options.Add(uDryRun);
        github.Subcommands.Add(upload);
        upload.SetAction((ParseResult pr) => RunGithubUpload(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(uChannel) ?? "stable",
            pr.GetValue(uTag) ?? string.Empty,
            pr.GetValue(uTitle) ?? string.Empty,
            pr.GetValue(uRepo) ?? string.Empty,
            pr.GetValue(uDir) ?? string.Empty,
            pr.GetValue(uFiles) ?? [],
            pr.GetValue(uNotesFile) ?? string.Empty,
            pr.GetValue(uPublish),
            pr.GetValue(uPre),
            pr.GetValue(uDryRun)));

        var smoke = new Command("smoke", "Local Velopack smoke test without GitHub (1.0.0 + update). E.g. deco smoke --dry-run");
        var sRuntime = new Option<string>("--runtime") { Description = "RID (default: win-x64)." };
        var sBase = new Option<string>("--base-version") { Description = "First version (default: 1.0.0; update is +0.0.1)." };
        var sDryRun = new Option<bool>("--dry-run") { Description = "Print planned steps, run nothing." };
        smoke.Options.Add(sRuntime);
        smoke.Options.Add(sBase);
        smoke.Options.Add(sDryRun);
        root.Subcommands.Add(smoke);
        smoke.SetAction((ParseResult pr) => RunSmoke(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(sRuntime) ?? "win-x64",
            pr.GetValue(sBase) ?? "1.0.0",
            pr.GetValue(sDryRun)));

        var test = new Command("test", "Repo quality gates.");
        root.Subcommands.Add(test);
        var parity = new Command("parity", "App-vs-template mirror check. E.g. deco test parity --exclude \"UI/**\" --rename App.sln=__SafeName__.sln");
        var pAppRoot = new Option<string>("--app-root") { Description = "App tree (default: repo root)." };
        var pTplRoot = new Option<string>("--template-root") { Description = "Template tree (default: <repo>/Templates/Project)." };
        var pExclude = new Option<string[]>("--exclude") { Description = "Glob with no counterpart, repeatable (e.g. UI/**)." };
        var pRename = new Option<string[]>("--rename") { Description = "A=B content-matched rename, repeatable." };
        parity.Options.Add(pAppRoot);
        parity.Options.Add(pTplRoot);
        parity.Options.Add(pExclude);
        parity.Options.Add(pRename);
        test.Subcommands.Add(parity);
        parity.SetAction((ParseResult pr) => RunTestParity(
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(pAppRoot) ?? string.Empty,
            pr.GetValue(pTplRoot) ?? string.Empty,
            pr.GetValue(pExclude) ?? [],
            pr.GetValue(pRename) ?? [],
            pr.GetValue(projectOption)));

        var pipeline = new Command("pipeline", "Official repo gate: build (0 warnings) + tests. Sandbox-safe: never publishes or packs. E.g. deco test pipeline --skip-tests");
        var skipTestsOption = new Option<bool>("--skip-tests") { Description = "Build only, skip dotnet test." };
        pipeline.Options.Add(skipTestsOption);
        test.Subcommands.Add(pipeline);
        pipeline.SetAction((ParseResult pr) => RunTestPipeline(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(skipTestsOption)));

        var release = new Command("release", "Full release pipelines.");
        root.Subcommands.Add(release);
        var relStore = new Command("store", "Green tree to committed Partner Center submission (7 stages). E.g. deco release store --dry-run");
        var rPublisher = new Option<string>("--publisher") { Description = "Signing publisher ID (required; placeholder rejected)." };
        var rAppId = new Option<string>("--app-id") { Description = "Store Product ID (default: manifest storeAppId)." };
        var rTenant = new Option<string>("--tenant-id") { Description = "Entra tenant ID (Api submitter)." };
        var rClient = new Option<string>("--client-id") { Description = "Entra app client ID (Api submitter)." };
        var rSecret = new Option<string>("--client-secret") { Description = "Entra app secret, never logged (Api submitter)." };
        var rPublishMode = new Option<string>("--publish-mode") { Description = "Immediate|Manual (default: Manual)." };
        var rSubmitter = new Option<string>("--submitter") { Description = "Api|Portal (default: Api)." };
        var rDescription = new Option<string>("--description-file") { Description = "Store description text (default: Store/Listing/description.txt)." };
        var rShotsDir = new Option<string>("--shots-dir") { Description = "Upload screenshots dir (default: Releases/Shots/<version>)." };
        var rReleaseNotes = new Option<string>("--release-notes") { Description = "What's-new text." };
        var rCertNotes = new Option<string>("--cert-notes") { Description = "Notes for certification." };
        var rPackageDir = new Option<string>("--package-dir") { Description = "Copy the submission set here." };
        var rSkipTests = new Option<bool>("--skip-tests") { Description = "Skip unit tests." };
        var rSkipBuild = new Option<bool>("--skip-build") { Description = "Reuse existing packages (freshness-checked)." };
        var rRequireShots = new Option<bool>("--require-shots") { Description = "Fail when no screenshots found." };
        var rSkipShots = new Option<bool>("--skip-shots") { Description = "Upload no listing images." };
        var rSkipWack = new Option<bool>("--skip-wack") { Description = "Skip WACK (owner-side gate)." };
        var rCertified = new Option<bool>("--certified-confirmed") { Description = "Confirm 'certified' wording cross-check done." };
        var rRehearse = new Option<bool>("--rehearse") { Description = "Full live flow, then DELETE instead of commit." };
        var rListingOnly = new Option<bool>("--listing-only") { Description = "Text-only draft update, then delete." };
        var rSkipSubmit = new Option<bool>("--skip-submit") { Description = "Stop after upload-ready packages." };
        var rDryRun = new Option<bool>("--dry-run") { Description = "Print every action, touch nothing." };
        var rResume = new Option<bool>("--resume") { Description = "Skip stages recorded in release-state-<version>.json (submit re-runs)." };
        var rAllowDirty = new Option<bool>("--allow-dirty") { Description = "Ship from a modified tree." };
        var rCleanupOnFail = new Option<bool>("--cleanup-on-fail") { Description = "Delete uncommitted submission after errors." };
        var rBuildTimeout = new Option<int>("--build-timeout-sec") { Description = "Per-arch build timeout (default: 1800)." };
        var rPollTimeout = new Option<int>("--poll-timeout-sec") { Description = "Certification poll timeout (default: 1800)." };
        var rWackTimeout = new Option<int>("--wack-timeout-sec") { Description = "WACK timeout (default: 1200)." };
        var rStage = new Option<string[]>("--stage") { Description = "Run only these stages (0-7 or names), repeatable." };
        var rListStages = new Option<bool>("--list-stages") { Description = "Print the stage graph and exit." };
        relStore.Options.Add(rPublisher);
        relStore.Options.Add(rAppId);
        relStore.Options.Add(rTenant);
        relStore.Options.Add(rClient);
        relStore.Options.Add(rSecret);
        relStore.Options.Add(rPublishMode);
        relStore.Options.Add(rSubmitter);
        relStore.Options.Add(rDescription);
        relStore.Options.Add(rShotsDir);
        relStore.Options.Add(rReleaseNotes);
        relStore.Options.Add(rCertNotes);
        relStore.Options.Add(rPackageDir);
        relStore.Options.Add(rSkipTests);
        relStore.Options.Add(rSkipBuild);
        relStore.Options.Add(rRequireShots);
        relStore.Options.Add(rSkipShots);
        relStore.Options.Add(rSkipWack);
        relStore.Options.Add(rCertified);
        relStore.Options.Add(rRehearse);
        relStore.Options.Add(rListingOnly);
        relStore.Options.Add(rSkipSubmit);
        relStore.Options.Add(rDryRun);
        relStore.Options.Add(rResume);
        relStore.Options.Add(rAllowDirty);
        relStore.Options.Add(rCleanupOnFail);
        relStore.Options.Add(rBuildTimeout);
        relStore.Options.Add(rPollTimeout);
        relStore.Options.Add(rWackTimeout);
        relStore.Options.Add(rStage);
        relStore.Options.Add(rListStages);
        release.Subcommands.Add(relStore);
        relStore.SetAction((ParseResult pr) => RunReleaseStore(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(rPublisher) ?? string.Empty,
            pr.GetValue(rAppId) ?? string.Empty,
            pr.GetValue(rTenant) ?? string.Empty,
            pr.GetValue(rClient) ?? string.Empty,
            pr.GetValue(rSecret) ?? string.Empty,
            pr.GetValue(rPublishMode) ?? "Manual",
            pr.GetValue(rSubmitter) ?? "Api",
            pr.GetValue(rDescription) ?? string.Empty,
            pr.GetValue(rShotsDir) ?? string.Empty,
            pr.GetValue(rReleaseNotes) ?? string.Empty,
            pr.GetValue(rCertNotes) ?? string.Empty,
            pr.GetValue(rPackageDir) ?? string.Empty,
            pr.GetValue(rSkipTests),
            pr.GetValue(rSkipBuild),
            pr.GetValue(rRequireShots),
            pr.GetValue(rSkipShots),
            pr.GetValue(rSkipWack),
            pr.GetValue(rCertified),
            pr.GetValue(rRehearse),
            pr.GetValue(rListingOnly),
            pr.GetValue(rSkipSubmit),
            pr.GetValue(rDryRun),
            pr.GetValue(rResume),
            pr.GetValue(rAllowDirty),
            pr.GetValue(rCleanupOnFail),
            pr.GetValue(rBuildTimeout),
            pr.GetValue(rPollTimeout),
            pr.GetValue(rWackTimeout),
            pr.GetValue(rStage) ?? [],
            pr.GetValue(rListStages)));

        var store = new Command("store", "Microsoft Store listing, screenshot and evidence workflows.");
        root.Subcommands.Add(store);
        var localeOption = new Option<string[]>("--locale")
        {
            Description = "Expected locales, repeatable (default: en-US es-ES fr-FR). First is the reference.",
        };
        var stVersion = new Option<string>("--version") { Description = "Release version (default: csproj InformationalVersion)." };
        var stCandidate = new Option<string>("--candidate") { Description = "Screenshot candidate dir (default: <repo>/Releases/Store/<version>)." };
        var stConfig = new Option<string>("--configuration") { Description = "Debug|Release (default: Release)." };

        var listings = new Command("listings", "Validate Store/ListingSource (words, translations, approval). E.g. deco store listings --preview");
        var lSource = new Option<string>("--source") { Description = "ListingSource dir (default: manifest paths.listingSourceDir or <repo>/Store/ListingSource)." };
        var lPreview = new Option<bool>("--preview") { Description = "Inspect drafts (allow unapproved)." };
        var lRequireApproval = new Option<bool>("--require-approval") { Description = "Fail when not approved (default without --preview)." };
        listings.Options.Add(lSource);
        listings.Options.Add(stVersion);
        listings.Options.Add(localeOption);
        listings.Options.Add(lPreview);
        listings.Options.Add(lRequireApproval);
        store.Subcommands.Add(listings);
        listings.SetAction((ParseResult pr) => RunStoreListings(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(lSource) ?? string.Empty,
            pr.GetValue(stVersion) ?? string.Empty,
            pr.GetValue(localeOption) ?? [],
            pr.GetValue(lPreview),
            pr.GetValue(lRequireApproval)));

        var shots = new Command("shots", "Validate (or create) the screenshot manifest. E.g. deco store shots --require-approval");
        var sRequireApproval = new Option<bool>("--require-approval") { Description = "Require review/approval.json." };
        var sNewManifest = new Option<bool>("--new-manifest") { Description = "Build screenshot-manifest.json from screenshots/ (capture step)." };
        shots.Options.Add(stCandidate);
        shots.Options.Add(stVersion);
        shots.Options.Add(stConfig);
        shots.Options.Add(localeOption);
        shots.Options.Add(sRequireApproval);
        shots.Options.Add(sNewManifest);
        store.Subcommands.Add(shots);
        shots.SetAction((ParseResult pr) => RunStoreShots(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(stCandidate) ?? string.Empty,
            pr.GetValue(stVersion) ?? string.Empty,
            pr.GetValue(stConfig) ?? "Release",
            pr.GetValue(localeOption) ?? [],
            pr.GetValue(sRequireApproval),
            pr.GetValue(sNewManifest)));

        var approve = new Command("approve", "Record a human approval receipt. E.g. deco store approve --reviewer ana");
        var aReviewer = new Option<string>("--reviewer") { Description = "Reviewer identity (required, max 120 chars)." };
        approve.Options.Add(aReviewer);
        approve.Options.Add(stCandidate);
        approve.Options.Add(stVersion);
        approve.Options.Add(stConfig);
        approve.Options.Add(localeOption);
        store.Subcommands.Add(approve);
        approve.SetAction((ParseResult pr) => RunStoreApprove(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(aReviewer) ?? string.Empty,
            pr.GetValue(stCandidate) ?? string.Empty,
            pr.GetValue(stVersion) ?? string.Empty,
            pr.GetValue(stConfig) ?? "Release",
            pr.GetValue(localeOption) ?? []));

        var evidence = new Command("evidence", "Assemble the release-evidence bundle. E.g. deco store evidence --dry-run");
        var eManifest = new Option<string>("--release-manifest") { Description = "release-manifest-*.json (default: Releases/Msix/release-manifest-<Version>.json)." };
        var eOutput = new Option<string>("--output") { Description = "Output dir, must be inside Releases/StoreEvidence (default there)." };
        var eListingVersion = new Option<string>("--listing-version") { Description = "Listing source version (default: release version)." };
        var eWack = new Option<string>("--wack-status") { Description = "passed|failed|skipped|not-run (default: not-run)." };
        var eWackReason = new Option<string>("--wack-reason") { Description = "WACK status reason." };
        var eUi = new Option<string>("--ui-status") { Description = "passed|failed|skipped|not-run (default: not-run)." };
        var eUiReason = new Option<string>("--ui-reason") { Description = "UI smoke status reason." };
        var eClean = new Option<bool>("--clean") { Description = "Remove a pre-existing output dir." };
        var eDryRun = new Option<bool>("--dry-run") { Description = "Validate inputs, assemble nothing." };
        evidence.Options.Add(eManifest);
        evidence.Options.Add(eOutput);
        evidence.Options.Add(stCandidate);
        evidence.Options.Add(eListingVersion);
        evidence.Options.Add(eWack);
        evidence.Options.Add(eWackReason);
        evidence.Options.Add(eUi);
        evidence.Options.Add(eUiReason);
        evidence.Options.Add(eClean);
        evidence.Options.Add(eDryRun);
        store.Subcommands.Add(evidence);
        evidence.SetAction((ParseResult pr) => RunStoreEvidence(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(eManifest) ?? string.Empty,
            pr.GetValue(eOutput) ?? string.Empty,
            pr.GetValue(stCandidate) ?? string.Empty,
            pr.GetValue(eListingVersion) ?? string.Empty,
            pr.GetValue(eWack) ?? "not-run",
            pr.GetValue(eWackReason) ?? string.Empty,
            pr.GetValue(eUi) ?? "not-run",
            pr.GetValue(eUiReason) ?? string.Empty,
            pr.GetValue(eClean),
            pr.GetValue(eDryRun)));

        var review = new Command("review", "Build the read-only review gallery (index.html + index.json). E.g. deco store review --version 0.8.0-beta");
        var rVersion = new Option<string>("--version") { Description = "Release version (default: csproj InformationalVersion)." };
        var rCandidate = new Option<string>("--candidate") { Description = "Screenshot candidate dir (default: <repo>/Releases/Store/<version>)." };
        review.Options.Add(rVersion);
        review.Options.Add(rCandidate);
        store.Subcommands.Add(review);
        review.SetAction((ParseResult pr) => RunStoreReview(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(rVersion) ?? string.Empty,
            pr.GetValue(rCandidate) ?? string.Empty));

        var plan = new Command("plan", "Build the offline submission contract (payload + ZIP + plan JSON). E.g. deco store plan --snapshot sub.json --bundle App.msixbundle --dry-run");
        var pSnapshot = new Option<string>("--snapshot") { Description = "Partner Center submission snapshot JSON (required)." };
        var pBundle = new Option<string>("--bundle") { Description = "Package bundle path (required)." };
        var pCandidate = new Option<string>("--candidate") { Description = "Screenshot candidate dir (default: <repo>/Releases/Store/<version>)." };
        var pVersion = new Option<string>("--version") { Description = "Release version (default: csproj InformationalVersion)." };
        var pConfig = new Option<string>("--configuration") { Description = "Debug|Release (default: Release)." };
        var pDryRun = new Option<bool>("--dry-run") { Description = "Validate only, write nothing." };
        plan.Options.Add(pSnapshot);
        plan.Options.Add(pBundle);
        plan.Options.Add(pCandidate);
        plan.Options.Add(pVersion);
        plan.Options.Add(pConfig);
        plan.Options.Add(localeOption);
        plan.Options.Add(pDryRun);
        store.Subcommands.Add(plan);
        plan.SetAction((ParseResult pr) => RunStorePlan(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(pSnapshot) ?? string.Empty,
            pr.GetValue(pBundle) ?? string.Empty,
            pr.GetValue(pCandidate) ?? string.Empty,
            pr.GetValue(pVersion) ?? string.Empty,
            pr.GetValue(pConfig) ?? "Release",
            pr.GetValue(localeOption) ?? [],
            pr.GetValue(pDryRun)));

        var submit = new Command("submit", "Submit a .msixupload via the Partner Center API (create, upload, commit, poll). E.g. deco store submit --dry-run");
        var sMsixUpload = new Option<string>("--msixupload") { Description = "Package path (default: newest Releases/Msix/*.msixupload)." };
        var sAppId = new Option<string>("--app-id") { Description = "Store Product ID (default: manifest storeAppId or DECO_STORE_APP_ID)." };
        var sTenant = new Option<string>("--tenant-id") { Description = "Entra tenant ID." };
        var sClient = new Option<string>("--client-id") { Description = "Entra app client ID." };
        var sSecret = new Option<string>("--client-secret") { Description = "Entra app secret (never logged)." };
        var sPublishMode = new Option<string>("--publish-mode") { Description = "Immediate|Manual (default: Immediate)." };
        var sCertNotes = new Option<string>("--cert-notes") { Description = "Notes for certification." };
        var sWait = new Option<bool>("--wait") { Description = "Poll until Published instead of returning after commit." };
        var sPollTimeout = new Option<int>("--poll-timeout-sec") { Description = "Status poll timeout (default: 1200)." };
        var subDryRun = new Option<bool>("--dry-run") { Description = "Validate inputs, call nothing." };
        submit.Options.Add(sMsixUpload);
        submit.Options.Add(sAppId);
        submit.Options.Add(sTenant);
        submit.Options.Add(sClient);
        submit.Options.Add(sSecret);
        submit.Options.Add(sPublishMode);
        submit.Options.Add(sCertNotes);
        submit.Options.Add(sWait);
        submit.Options.Add(sPollTimeout);
        submit.Options.Add(sDryRun);
        store.Subcommands.Add(submit);
        submit.SetAction((ParseResult pr) => RunStoreSubmit(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(sMsixUpload) ?? string.Empty,
            pr.GetValue(sAppId) ?? string.Empty,
            pr.GetValue(sTenant) ?? string.Empty,
            pr.GetValue(sClient) ?? string.Empty,
            pr.GetValue(sSecret) ?? string.Empty,
            pr.GetValue(sPublishMode) ?? "Immediate",
            pr.GetValue(sCertNotes) ?? string.Empty,
            pr.GetValue(sWait),
            pr.GetValue(sPollTimeout),
            pr.GetValue(subDryRun)));

        var portalAuth = new Command("portal-auth", "Partner Center login session (persistent profile, headed). E.g. deco store portal-auth --dry-run");
        portalAuth.Aliases.Add("login");
        var paProfile = new Option<string>("--profile-dir") { Description = "Browser profile dir (default: %LocalAppData%/<App>/portal-profile)." };
        var paLogout = new Option<bool>("--logout") { Description = "Wipe the profile (log out). Only the portal-profile browser is closed; your own Edge windows are never touched." };
        var paDryRun = new Option<bool>("--dry-run") { Description = "Print the plan, change nothing." };
        var paTimeout = new Option<int>("--login-timeout-sec") { Description = "Manual-login wait (default: 600)." };
        portalAuth.Options.Add(paProfile);
        portalAuth.Options.Add(paLogout);
        portalAuth.Options.Add(paDryRun);
        portalAuth.Options.Add(paTimeout);
        store.Subcommands.Add(portalAuth);
        portalAuth.SetAction((ParseResult pr) => RunPortalAuth(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(paProfile) ?? string.Empty,
            pr.GetValue(paLogout),
            pr.GetValue(paDryRun),
            pr.GetValue(paTimeout)));

        var portal = new Command("portal", "Checkpointed Partner Center submission driver (headless-first). E.g. deco store portal --dry-run");
        var ptUpload = new Option<string>("--upload") { Description = "Package path (default: newest Releases/Msix/*.msixupload)." };
        var ptDescription = new Option<string>("--description") { Description = "Description override FILE (en-US only)." };
        var ptShots = new Option<string>("--shots") { Description = "Screenshots dir or matrix root (siblings auto-discovered)." };
        var ptNotes = new Option<string>("--release-notes") { Description = "Release-notes text override (en-US only)." };
        var ptPublishMode = new Option<string>("--publish-mode") { Description = "Immediate|Manual (default: Manual)." };
        var ptStoreId = new Option<string>("--store-id") { Description = "Store Product ID (required)." };
        var ptProfile = new Option<string>("--profile") { Description = "Browser profile dir (default: %LocalAppData%/<App>/portal-profile)." };
        var ptVersion = new Option<string>("--version") { Description = "Listing version (default: csproj InformationalVersion)." };
        var ptListingSource = new Option<string>("--listing-source") { Description = "ListingSource dir (default: manifest paths.listingSourceDir or <repo>/Store/ListingSource)." };
        var ptAutoConfirm = new Option<bool>("--auto-confirm") { Description = "Auto-confirm checkpoints (final review still prompts)." };
        var ptHeaded = new Option<bool>("--headed") { Description = "Force a visible window (debugging)." };
        var ptNoLoginAssist = new Option<bool>("--no-login-assist") { Description = "Never pop the login window (exit 2 instead; CI)." };
        var ptSkipShots = new Option<bool>("--skip-shots") { Description = "Do not upload listing images." };
        var ptSkipPackages = new Option<bool>("--skip-packages") { Description = "Do not upload the package (draft must have staged packages)." };
        var ptCertified = new Option<bool>("--certified-confirmed") { Description = "Confirm 'certified' wording cross-check done." };
        var ptLoginTimeout = new Option<int>("--login-timeout") { Description = "Manual sign-in wait seconds (default: 600)." };
        var ptProgress = new Option<string>("--progress-json") { Description = "JSONL progress file (default: temp)." };
        var ptDryRun = new Option<bool>("--dry-run") { Description = "Preflight only, no browser." };
        var ptSelfTest = new Option<bool>("--self-test") { Description = "Engine self-test on local fixtures (no Partner Center contact)." };
        portal.Options.Add(ptUpload);
        portal.Options.Add(ptDescription);
        portal.Options.Add(ptShots);
        portal.Options.Add(ptNotes);
        portal.Options.Add(ptPublishMode);
        portal.Options.Add(ptStoreId);
        portal.Options.Add(ptProfile);
        portal.Options.Add(ptVersion);
        portal.Options.Add(ptListingSource);
        portal.Options.Add(ptAutoConfirm);
        portal.Options.Add(ptHeaded);
        portal.Options.Add(ptNoLoginAssist);
        portal.Options.Add(ptSkipShots);
        portal.Options.Add(ptSkipPackages);
        portal.Options.Add(ptCertified);
        portal.Options.Add(ptLoginTimeout);
        portal.Options.Add(ptProgress);
        portal.Options.Add(ptDryRun);
        portal.Options.Add(ptSelfTest);
        store.Subcommands.Add(portal);
        portal.SetAction((ParseResult pr) => RunStorePortal(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(ptUpload) ?? string.Empty,
            pr.GetValue(ptDescription) ?? string.Empty,
            pr.GetValue(ptShots) ?? string.Empty,
            pr.GetValue(ptNotes) ?? string.Empty,
            pr.GetValue(ptPublishMode) ?? "Manual",
            pr.GetValue(ptStoreId) ?? string.Empty,
            pr.GetValue(ptProfile) ?? string.Empty,
            pr.GetValue(ptVersion) ?? string.Empty,
            pr.GetValue(ptListingSource) ?? string.Empty,
            pr.GetValue(ptAutoConfirm),
            pr.GetValue(ptHeaded),
            pr.GetValue(ptNoLoginAssist),
            pr.GetValue(ptSkipShots),
            pr.GetValue(ptSkipPackages),
            pr.GetValue(ptCertified),
            pr.GetValue(ptLoginTimeout),
            pr.GetValue(ptProgress) ?? string.Empty,
            pr.GetValue(ptDryRun),
            pr.GetValue(ptSelfTest)));

        var portalPrice = new Command("portal-price", "Partner Center base-price driver (CloakBrowser/Playwright, step screenshots; default is read-only diagnose). E.g. deco store portal-price --store-id 9ABCDEF12345 --submission-id 123 --currency \"EUR - France\" --price \"€ 2.99\" --apply");
        var ppStoreId = new Option<string>("--store-id") { Description = "Store Product ID (required)." };
        var ppSubmission = new Option<string>("--submission-id") { Description = "Pending submission ID (required; from the portal URL or cert-watch)." };
        var ppCurrency = new Option<string>("--currency") { Description = "Base-price currency label (e.g. \"EUR - France\"). Required for --apply." };
        var ppPrice = new Option<string>("--price") { Description = "Base retail price label (e.g. \"€ 2.99\"). Required for --apply; never guessed." };
        var ppProfile = new Option<string>("--profile-dir") { Description = "Browser profile dir (default: %LocalAppData%/<App>/portal-profile)." };
        var ppApply = new Option<bool>("--apply") { Description = "Set currency + price, Save, and read-back verify. Without it: read-only diagnose." };
        var ppSubmit = new Option<bool>("--submit") { Description = "Submit for certification in the portal after verified pricing (requires --apply). Starts certification." };
        var ppAllowFree = new Option<bool>("--allow-free") { Description = "Allow setting a Free-shaped price (refused by default)." };
        var ppAutoConfirm = new Option<bool>("--auto-confirm") { Description = "Auto-confirm the apply/commit checkpoints." };
        var ppHeaded = new Option<bool>("--headed") { Description = "Force a visible window (debugging)." };
        var ppNoLoginAssist = new Option<bool>("--no-login-assist") { Description = "Never pop the login window (exit 2 instead; CI)." };
        var ppLoginTimeout = new Option<int>("--login-timeout-sec") { Description = "Manual sign-in wait seconds (default: 600)." };
        var ppRunDir = new Option<string>("--run-dir") { Description = "Step screenshots + manifest dir (default: temp)." };
        var ppDryRun = new Option<bool>("--dry-run") { Description = "Print the plan, change nothing (no browser)." };
        var ppSelfTest = new Option<bool>("--self-test") { Description = "Behavioral fixture E2E on local pages (no Partner Center contact)." };
        portalPrice.Options.Add(ppStoreId);
        portalPrice.Options.Add(ppSubmission);
        portalPrice.Options.Add(ppCurrency);
        portalPrice.Options.Add(ppPrice);
        portalPrice.Options.Add(ppProfile);
        portalPrice.Options.Add(ppApply);
        portalPrice.Options.Add(ppSubmit);
        portalPrice.Options.Add(ppAllowFree);
        portalPrice.Options.Add(ppAutoConfirm);
        portalPrice.Options.Add(ppHeaded);
        portalPrice.Options.Add(ppNoLoginAssist);
        portalPrice.Options.Add(ppLoginTimeout);
        portalPrice.Options.Add(ppRunDir);
        portalPrice.Options.Add(ppDryRun);
        portalPrice.Options.Add(ppSelfTest);
        store.Subcommands.Add(portalPrice);
        portalPrice.SetAction((ParseResult pr) => RunStorePortalPrice(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(ppStoreId) ?? string.Empty,
            pr.GetValue(ppSubmission) ?? string.Empty,
            pr.GetValue(ppCurrency) ?? string.Empty,
            pr.GetValue(ppPrice) ?? string.Empty,
            pr.GetValue(ppProfile) ?? string.Empty,
            pr.GetValue(ppApply),
            pr.GetValue(ppSubmit),
            pr.GetValue(ppAllowFree),
            pr.GetValue(ppAutoConfirm),
            pr.GetValue(ppHeaded),
            pr.GetValue(ppNoLoginAssist),
            pr.GetValue(ppLoginTimeout),
            pr.GetValue(ppRunDir) ?? string.Empty,
            pr.GetValue(ppDryRun),
            pr.GetValue(ppSelfTest)));

        var certWatch = new Command("cert-watch", "Watch Partner Center certification (polls msstore status; --list/--all for multiple apps, hourly task for multi-day waits). E.g. deco store cert-watch --once");
        certWatch.Aliases.Add("certwatch");
        var cwAppId = new Option<string>("--app-id") { Description = "Store Product ID (remembered per project after first use)." };
        var cwTimeout = new Option<int>("--timeout-min") { Description = "Stop watching after N minutes (default: 120)." };
        var cwInterval = new Option<int>("--interval-min") { Description = "Minutes between polls (default: 15, min: 1)." };
        var cwOnce = new Option<bool>("--once") { Description = "Single check, then exit (for tasks and quick peeks)." };
        var cwNotify = new Option<bool>("--notify") { Description = "Banner + state file on terminal state (for scheduled runs)." };
        var cwRegister = new Option<bool>("--register-task") { Description = "Register an hourly per-user task (one bounded pass per hour) and exit." };
        var cwUnregister = new Option<bool>("--unregister-task") { Description = "Remove the hourly task and exit." };
        certWatch.Options.Add(cwAppId);
        certWatch.Options.Add(cwTimeout);
        certWatch.Options.Add(cwInterval);
        certWatch.Options.Add(cwOnce);
        certWatch.Options.Add(cwNotify);
        var cwNoCache = new Option<bool>("--no-cache") { Description = "Skip the instant cached answer, always poll live." };
        var cwList = new Option<bool>("--list") { Description = "List remembered apps with last known state, then exit." };
        var cwAll = new Option<bool>("--all") { Description = "Check every remembered app once (table), then exit." };
        certWatch.Options.Add(cwRegister);
        certWatch.Options.Add(cwUnregister);
        certWatch.Options.Add(cwNoCache);
        certWatch.Options.Add(cwList);
        certWatch.Options.Add(cwAll);
        store.Subcommands.Add(certWatch);
        certWatch.SetAction((ParseResult pr) => RunStoreCertWatch(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(cwAppId) ?? string.Empty,
            pr.GetValue(cwTimeout),
            pr.GetValue(cwInterval),
            pr.GetValue(cwOnce),
            pr.GetValue(cwNotify),
            pr.GetValue(cwRegister),
            pr.GetValue(cwUnregister),
            pr.GetValue(cwNoCache),
            pr.GetValue(cwList),
            pr.GetValue(cwAll)));

        var pricing = new Command("pricing", "Read or set per-market submission pricing (base tier + MARKET=TIER overrides). E.g. deco store pricing --app-id 9ABCDEF12345 --set FR=Tier1012 --dry-run");
        var pcAppId = new Option<string>("--app-id") { Description = "Store Product ID (required)." };
        var pcSet = new Option<string[]>("--set") { Description = "MARKET=TIER override, repeatable (e.g. FR=Tier1012). Merged, never deletes." };
        var pcBase = new Option<string>("--base-tier") { Description = "Base price tier (owner-confirmed). Needs --allow-pricing-migration on advanced products." };
        var pcAllow = new Option<bool>("--allow-pricing-migration") { Description = "REQUIRED to change the base tier on advanced/per-market products (explicit owner decision)." };
        var pcDryRun = new Option<bool>("--dry-run") { Description = "Print the merged pricing, touch nothing." };
        pricing.Options.Add(pcAppId);
        pricing.Options.Add(pcSet);
        pricing.Options.Add(pcBase);
        pricing.Options.Add(pcAllow);
        pricing.Options.Add(pcDryRun);
        store.Subcommands.Add(pricing);
        pricing.SetAction((ParseResult pr) => RunStorePricing(
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(pcAppId) ?? string.Empty,
            pr.GetValue(pcSet) ?? [],
            pr.GetValue(pcBase) ?? string.Empty,
            pr.GetValue(pcAllow),
            pr.GetValue(pcDryRun)));

        var wack = new Command("wack", "Run the Windows App Certification Kit and gate on the verdict. E.g. deco wack --package App.msix --elevate");
        root.Subcommands.Add(wack);
        var wPackage = new Option<string>("--package") { Description = "Package or bundle path (.msix/.msixbundle; not .msixupload)." };
        var wReport = new Option<string>("--report") { Description = "Report XML path (default: temp wack-<name>.xml)." };
        var wElevate = new Option<bool>("--elevate") { Description = "Re-launch elevated on UAC (one click) when not admin." };
        var wParseOnly = new Option<bool>("--parse-only") { Description = "Gate on an existing report; skip the appcert run." };
        var wTimeout = new Option<int>("--timeout-sec") { Description = "appcert timeout in seconds (default: 1500)." };
        var wDryRun = new Option<bool>("--dry-run") { Description = "Validate inputs + probe appcert, run nothing." };
        wack.Options.Add(wPackage);
        wack.Options.Add(wReport);
        wack.Options.Add(wElevate);
        wack.Options.Add(wParseOnly);
        wack.Options.Add(wTimeout);
        wack.Options.Add(wDryRun);
        wack.SetAction((ParseResult pr) => RunWack(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(wPackage) ?? string.Empty,
            pr.GetValue(wReport) ?? string.Empty,
            pr.GetValue(wElevate),
            pr.GetValue(wParseOnly),
            pr.GetValue(wTimeout),
            pr.GetValue(wDryRun)));

        var msstore = new Command("msstore", "Microsoft Store Developer CLI workflows.");
        root.Subcommands.Add(msstore);
        var configure = new Command("configure", "Configure msstore from flags or env (lengths echoed, secrets never shown). E.g. deco msstore configure --dry-run");
        var cTenant = new Option<string>("--tenant-id") { Description = "Partner Center tenant ID." };
        var cSeller = new Option<string>("--seller-id") { Description = "Partner Center seller ID." };
        var cClient = new Option<string>("--client-id") { Description = "Entra app client ID." };
        var cSecret = new Option<string>("--client-secret") { Description = "Entra app secret (never logged)." };
        var cThumb = new Option<string>("--cert-thumbprint") { Description = "Auth certificate thumbprint (alternative to secret)." };
        var cCertFile = new Option<string>("--cert-file") { Description = "Auth certificate .pfx path (alternative to secret)." };
        var cCertPass = new Option<string>("--cert-password") { Description = "Auth certificate password (never logged)." };
        var cVerify = new Option<bool>("--verify") { Description = "Run `msstore apps list` afterwards." };
        var cDryRun = new Option<bool>("--dry-run") { Description = "Resolve + preview (names only, never values), run nothing." };
        configure.Options.Add(cTenant);
        configure.Options.Add(cSeller);
        configure.Options.Add(cClient);
        configure.Options.Add(cSecret);
        configure.Options.Add(cThumb);
        configure.Options.Add(cCertFile);
        configure.Options.Add(cCertPass);
        configure.Options.Add(cVerify);
        configure.Options.Add(cDryRun);
        msstore.Subcommands.Add(configure);
        configure.SetAction((ParseResult pr) => RunMsstoreConfigure(
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(cTenant) ?? string.Empty,
            pr.GetValue(cSeller) ?? string.Empty,
            pr.GetValue(cClient) ?? string.Empty,
            pr.GetValue(cSecret) ?? string.Empty,
            pr.GetValue(cThumb) ?? string.Empty,
            pr.GetValue(cCertFile) ?? string.Empty,
            pr.GetValue(cCertPass) ?? string.Empty,
            pr.GetValue(cVerify),
            pr.GetValue(cDryRun)));

        var publish = new Command("publish", "One-command Store publish via the msstore CLI. E.g. deco msstore publish --dry-run");
        var pubMsix = new Option<string>("--msixupload") { Description = "Package path (default: newest Releases/Msix/*.msixupload|*.msix)." };
        var pubAppId = new Option<string>("--app-id") { Description = "Store Product ID (required)." };
        var pubFlight = new Option<string>("--flight-id") { Description = "Flight ID (optional)." };
        var pubRollout = new Option<int>("--rollout") { Description = "Package rollout 0-100 (default: unset)." };
        var pubPrice = new Option<string>("--price-id") { Description = "Base price tier, owner-confirmed (e.g. Tier3). Never guessed, never Free-by-default." };
        var pubAllowMigration = new Option<bool>("--allow-pricing-migration") { Description = "REQUIRED to migrate advanced/per-market pricing to a single tier (explicit owner decision)." };
        var pubUploadTimeout = new Option<int>("--upload-timeout-sec") { Description = "Upload timeout (default: 1200)." };
        var pubNoCommit = new Option<bool>("--no-commit") { Description = "Draft only (inspect in Partner Center)." };
        var pubWait = new Option<bool>("--wait") { Description = "Poll certification after commit." };
        var pubStatusOnly = new Option<bool>("--status-only") { Description = "Print submission status and exit." };
        var pubDeleteDraft = new Option<bool>("--delete-draft") { Description = "Delete the pending draft and exit." };
        var pubDryRun = new Option<bool>("--dry-run") { Description = "Validate + preview, run nothing." };
        publish.Options.Add(pubMsix);
        publish.Options.Add(pubAppId);
        publish.Options.Add(pubFlight);
        publish.Options.Add(pubRollout);
        publish.Options.Add(pubPrice);
        publish.Options.Add(pubAllowMigration);
        publish.Options.Add(pubUploadTimeout);
        publish.Options.Add(pubNoCommit);
        publish.Options.Add(pubWait);
        publish.Options.Add(pubStatusOnly);
        publish.Options.Add(pubDeleteDraft);
        publish.Options.Add(pubDryRun);
        msstore.Subcommands.Add(publish);
        publish.SetAction((ParseResult pr) => RunMsstorePublish(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(pubMsix) ?? string.Empty,
            pr.GetValue(pubAppId) ?? string.Empty,
            pr.GetValue(pubFlight) ?? string.Empty,
            pr.GetValue(pubRollout),
            pr.GetValue(pubPrice) ?? string.Empty,
            pr.GetValue(pubAllowMigration),
            pr.GetValue(pubUploadTimeout),
            pr.GetValue(pubNoCommit),
            pr.GetValue(pubWait),
            pr.GetValue(pubStatusOnly),
            pr.GetValue(pubDeleteDraft),
            pr.GetValue(pubDryRun)));

        var vm = new Command("vm", "Hyper-V capture-VM lab (screenshot host). Host-safe: only deco's own RDP viewer is ever hidden/closed. E.g. deco vm capture --dry-run");
        root.Subcommands.Add(vm);
        var vmName = new Option<string>("--vm") { Description = "VM name (default: manifest capture.vmName or Formixa-Capture)." };
        var vmUser = new Option<string>("--user") { Description = "Guest local user (default: manifest capture.vmUser or cap)." };
        var vmPassword = new Option<string>("--password") { Description = "Guest password (else the DPAPI store; never logged)." };
        var vmDryRun = new Option<bool>("--dry-run") { Description = "Validate + print the plan, change nothing." };

        var credential = new Command("credential", "Verify the guest password, then store it DPAPI-encrypted. E.g. deco vm credential --vm Formixa-Capture");
        var cStoreDir = new Option<string>("--store-dir") { Description = "Credential store dir (default: %APPDATA%/<App>)." };
        credential.Options.Add(vmName);
        credential.Options.Add(vmUser);
        credential.Options.Add(vmPassword);
        credential.Options.Add(cStoreDir);
        credential.Options.Add(vmDryRun);
        vm.Subcommands.Add(credential);
        credential.SetAction((ParseResult pr) => RunVmCredential(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(vmUser) ?? string.Empty,
            pr.GetValue(vmPassword) ?? string.Empty,
            pr.GetValue(cStoreDir) ?? string.Empty,
            pr.GetValue(vmDryRun)));

        var resetPw = new Command("reset-password", "Rotate the guest + autologon + store password. E.g. deco vm reset-password --dry-run");
        var rCurrent = new Option<string>("--current") { Description = "Current password (else the DPAPI store)." };
        var rNew = new Option<string>("--new") { Description = "New password (default: random 20-char, never printed)." };
        resetPw.Options.Add(vmName);
        resetPw.Options.Add(vmUser);
        resetPw.Options.Add(rCurrent);
        resetPw.Options.Add(rNew);
        resetPw.Options.Add(vmDryRun);
        vm.Subcommands.Add(resetPw);
        resetPw.SetAction((ParseResult pr) => RunVmResetPassword(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(vmUser) ?? string.Empty,
            pr.GetValue(rCurrent) ?? string.Empty,
            pr.GetValue(rNew) ?? string.Empty,
            pr.GetValue(vmDryRun)));

        var waitUnlocked = new Command("wait-unlocked", "Poll until the user session is usable (exit 0) or timeout (exit 1). E.g. deco vm wait-unlocked --timeout-min 8");
        var vmWaitTimeout = new Option<int>("--timeout-min") { Description = "Poll timeout minutes (default: 6)." };
        waitUnlocked.Options.Add(vmName);
        waitUnlocked.Options.Add(vmUser);
        waitUnlocked.Options.Add(vmPassword);
        waitUnlocked.Options.Add(vmWaitTimeout);
        vm.Subcommands.Add(waitUnlocked);
        waitUnlocked.SetAction((ParseResult pr) => RunVmWaitUnlocked(
            pr.GetValue(projectOption),
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(vmUser) ?? string.Empty,
            pr.GetValue(vmPassword) ?? string.Empty,
            pr.GetValue(vmWaitTimeout),
            pr.GetValue(noAnsiOption)));

        var resolution = new Command("resolution", "Set the guest console resolution (needs a console desktop). E.g. deco vm resolution --width 1920 --height 1080");
        var resW = new Option<int>("--width") { Description = "Target width (default: 1920)." };
        var resH = new Option<int>("--height") { Description = "Target height (default: 1080)." };
        resolution.Options.Add(vmName);
        resolution.Options.Add(vmUser);
        resolution.Options.Add(vmPassword);
        resolution.Options.Add(resW);
        resolution.Options.Add(resH);
        resolution.Options.Add(vmDryRun);
        vm.Subcommands.Add(resolution);
        resolution.SetAction((ParseResult pr) => RunVmResolution(
            pr.GetValue(projectOption),
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(vmUser) ?? string.Empty,
            pr.GetValue(vmPassword) ?? string.Empty,
            pr.GetValue(resW),
            pr.GetValue(resH),
            pr.GetValue(vmDryRun)));

        var vmNew = new Command("new", "One-time build of the capture VM (ELEVATED). E.g. deco vm new --dry-run");
        var nPassword = new Option<string>("--password") { Description = "VMLocalPassword (required; travels on your command line only)." };
        var nDir = new Option<string>("--dir") { Description = "Hyper-V base dir (default: E:\\Hyper-V)." };
        var nMem = new Option<int>("--memory-gb") { Description = "Startup RAM GB (default: 8)." };
        var nCpu = new Option<int>("--cpu") { Description = "vCPU count (default: 4)." };
        var nDisk = new Option<int>("--disk-gb") { Description = "Dynamic VHD GB (default: 80)." };
        var nElevate = new Option<bool>("--elevate") { Description = "Re-launch elevated on UAC (one click)." };
        vmNew.Options.Add(nPassword);
        vmNew.Options.Add(vmName);
        vmNew.Options.Add(nDir);
        vmNew.Options.Add(nMem);
        vmNew.Options.Add(nCpu);
        vmNew.Options.Add(nDisk);
        vmNew.Options.Add(vmDryRun);
        vmNew.Options.Add(nElevate);
        vm.Subcommands.Add(vmNew);
        vmNew.SetAction((ParseResult pr) => RunVmNew(
            pr.GetValue(projectOption),
            pr.GetValue(nPassword) ?? string.Empty,
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(nDir) ?? @"E:\Hyper-V",
            pr.GetValue(nMem),
            pr.GetValue(nCpu),
            pr.GetValue(nDisk),
            pr.GetValue(vmDryRun),
            pr.GetValue(nElevate)));

        var protect = new Command("protect", "Harden the guest (WU, sleep, lock, timezone, unlimited autologon). E.g. deco vm protect --dry-run");
        protect.Options.Add(vmName);
        protect.Options.Add(vmUser);
        protect.Options.Add(vmPassword);
        protect.Options.Add(vmDryRun);
        vm.Subcommands.Add(protect);
        protect.SetAction((ParseResult pr) => RunVmProtect(
            pr.GetValue(projectOption),
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(vmUser) ?? string.Empty,
            pr.GetValue(vmPassword) ?? string.Empty,
            pr.GetValue(vmDryRun)));

        var provision = new Command("provision", "Run the guest provisioning script inside the VM. E.g. deco vm provision --dry-run");
        var pScript = new Option<string>("--guest-script") { Description = "Guest script path (default: <repo>/Scripts/vm/Install-GuestDeps.ps1)." };
        provision.Options.Add(vmName);
        provision.Options.Add(vmUser);
        provision.Options.Add(vmPassword);
        provision.Options.Add(pScript);
        provision.Options.Add(vmDryRun);
        vm.Subcommands.Add(provision);
        provision.SetAction((ParseResult pr) => RunVmProvision(
            pr.GetValue(projectOption),
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(vmUser) ?? string.Empty,
            pr.GetValue(vmPassword) ?? string.Empty,
            pr.GetValue(pScript) ?? string.Empty,
            pr.GetValue(vmDryRun)));

        var rdpTrust = new Command("rdp-trust", "One-time silent-RDP trust (ELEVATED). E.g. deco vm rdp-trust --dry-run");
        rdpTrust.Options.Add(vmDryRun);
        rdpTrust.Options.Add(nElevate);
        vm.Subcommands.Add(rdpTrust);
        rdpTrust.SetAction((ParseResult pr) => RunVmRdpTrust(
            pr.GetValue(projectOption),
            pr.GetValue(vmDryRun),
            pr.GetValue(nElevate)));

        var capture = new Command("capture", "Host-driven screenshot capture in the VM. E.g. deco vm capture --preflight-only");
        var capVersion = new Option<string>("--version") { Description = "Capture version (default: csproj InformationalVersion)." };
        var capPreflight = new Option<bool>("--preflight-only") { Description = "Probe readiness only (READY or throw)." };
        var capNoClean = new Option<bool>("--no-clean") { Description = "Keep a previous in-guest candidate." };
        var capNoReboot = new Option<bool>("--no-reboot") { Description = "Disable self-heal reboot." };
        var capSyncMin = new Option<int>("--sync-min") { Description = "Tree-sync timeout minutes (default: 15)." };
        var capBuildMin = new Option<int>("--build-min") { Description = "Guest-build timeout minutes (default: 20)." };
        var capCaptureMin = new Option<int>("--capture-min") { Description = "Capture timeout minutes (default: 30)." };
        capture.Options.Add(capVersion);
        capture.Options.Add(vmName);
        capture.Options.Add(vmUser);
        capture.Options.Add(vmPassword);
        capture.Options.Add(capPreflight);
        capture.Options.Add(capNoClean);
        capture.Options.Add(capNoReboot);
        capture.Options.Add(capSyncMin);
        capture.Options.Add(capBuildMin);
        capture.Options.Add(capCaptureMin);
        capture.Options.Add(vmDryRun);
        vm.Subcommands.Add(capture);
        capture.SetAction((ParseResult pr) => RunVmCapture(
            pr.GetValue(projectOption),
            pr.GetValue(verbosityOption) ?? "normal",
            pr.GetValue(formatOption) ?? "table",
            pr.GetValue(noAnsiOption),
            pr.GetValue(capVersion) ?? string.Empty,
            pr.GetValue(vmName) ?? string.Empty,
            pr.GetValue(vmUser) ?? string.Empty,
            pr.GetValue(vmPassword) ?? string.Empty,
            pr.GetValue(capPreflight),
            pr.GetValue(capNoClean),
            pr.GetValue(capNoReboot),
            pr.GetValue(capSyncMin),
            pr.GetValue(capBuildMin),
            pr.GetValue(capCaptureMin),
            pr.GetValue(vmDryRun)));

        var completions = new Command("completions", "Print a shell-completion script (powershell|bash|zsh). E.g. deco completions --shell powershell");
        var cmShell = new Option<string>("--shell") { Description = "Target shell (powershell|bash|zsh)." };
        completions.Options.Add(cmShell);
        root.Subcommands.Add(completions);
        completions.SetAction((ParseResult pr) => RunCompletions(root, pr.GetValue(cmShell) ?? "powershell"));

        // Colorized --help at every level (single choke point: root's
        // HelpOption action covers subcommands too). Layout stays 100%
        // System.CommandLine; only the tinting is ours (gray explanations,
        // cyan names/usage, yellow headers; byte-identical when plain).
        foreach (var helpOpt in root.Options.OfType<System.CommandLine.Help.HelpOption>())
        {
            helpOpt.Action = new ColorHelpAction(noAnsiOption);
        }

        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>Renders default help, then tints it. Never changes layout or exit codes.</summary>
    private sealed class ColorHelpAction : System.CommandLine.Invocation.SynchronousCommandLineAction
    {
        private static readonly bool DefaultClearsParseErrors = new System.CommandLine.Help.HelpAction().ClearsParseErrors;

        private readonly Option<bool> _noAnsi;

        public ColorHelpAction(Option<bool> noAnsi) => _noAnsi = noAnsi;

        public override bool ClearsParseErrors => DefaultClearsParseErrors;

        public override int Invoke(ParseResult pr)
        {
            bool noAnsi;
            try
            {
                noAnsi = pr.GetValue(_noAnsi);
            }
            catch
            {
                noAnsi = true;
            }

            var capture = new StringWriter();
            TextWriter saved = Console.Out;
            int code;
            try
            {
                Console.SetOut(capture);
                code = new System.CommandLine.Help.HelpAction().Invoke(pr);
            }
            finally
            {
                Console.SetOut(saved);
            }

            Console.Write(DevCli.Commands.HelpColorizer.Colorize(capture.ToString(), noAnsi));
            return code;
        }
    }

    private static bool ValidateGlobals(string verbosity, string format, out string error)
    {
        if (!CliConstants.ValidVerbosity.Contains(verbosity.Trim().ToLowerInvariant()))
        {
            error = $"Invalid --verbosity '{verbosity}'. Valid: {string.Join("|", CliConstants.ValidVerbosity)}.";
            return false;
        }

        if (!CliConstants.ValidFormats.Contains(format.Trim().ToLowerInvariant()))
        {
            error = $"Invalid --format '{format}'. Valid: {string.Join("|", CliConstants.ValidFormats)}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void EmitJson(object payload) =>
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));

    private static int RunCompletions(Command root, string shell)
    {
        try
        {
            var entries = DevCli.Commands.CompletionsService.BuildEntries(root);
            Console.Write(DevCli.Commands.CompletionsService.Emit(shell, CliConstants.ToolName, entries));
            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
    }

    private static int RunInfo(string? project, string verbosity, string format, bool noAnsi)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        verbosity = verbosity.Trim().ToLowerInvariant();
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

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "info",
                repoRoot = ctx.RepoRoot,
                appId = ctx.AppId,
                csproj = ctx.CsprojPath,
                versionCsproj = ctx.Version,
                informationalVersion = ctx.InformationalVersion,
                storeAppId = ctx.StoreAppId,
                manifestFound = ctx.IsManifestFound,
            });
            return ExitCodes.Success;
        }

        if (verbosity == "quiet")
        {
            Console.WriteLine(ctx.AppId);
            return ExitCodes.Success;
        }

        if (noAnsi)
        {
            Console.WriteLine($"Tool: {CliConstants.ToolName} {CliConstants.Version}");
            Console.WriteLine($"Repo: {ctx.RepoRoot}");
            Console.WriteLine($"App:  {ctx.AppId}");
            Console.WriteLine($"Csproj: {ctx.CsprojPath ?? "(none)"}");
            Console.WriteLine($"Version: {ctx.Version ?? "(none)"}  Informational: {ctx.InformationalVersion ?? "(none)"}");
            Console.WriteLine($"StoreAppId: {ctx.StoreAppId ?? "(none)"}  Manifest: {(ctx.IsManifestFound ? "found" : "not found (defaults used)")}");
            return ExitCodes.Success;
        }

        DevCli.Commands.CliConsole.KeyValues(
            [("Tool", $"{CliConstants.ToolName} {CliConstants.Version}"),
                ("Repo", ctx.RepoRoot),
                ("App", ctx.AppId),
                ("Csproj", ctx.CsprojPath ?? "(none)"),
                ("Version", ctx.Version ?? "(none)"),
                ("Informational", ctx.InformationalVersion ?? "(none)"),
                ("StoreAppId", ctx.StoreAppId ?? "(none)"),
                ("Manifest", ctx.IsManifestFound ? "found" : "not found (defaults used)")],
            noAnsi);

        if (verbosity == "detailed")
        {
            DevCli.Commands.CliConsole.Info(
                "Discovery: --project flag > .devcli.json walk-up > .git root > *.csproj heuristic.", noAnsi);
        }

        return ExitCodes.Success;
    }

    private static ProcessResult RunTool(
        IProcessRunner runner, string exe, List<string> args, string? workDir, TimeSpan timeout, string what)
    {
        ProcessResult r;
        try
        {
            r = runner.RunAsync(exe, args, workDir, timeout).GetAwaiter().GetResult();
        }
        catch (DecoException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DecoException($"{what} failed to start ({exe}): {ex.Message}", "tool-start-failed");
        }

        if (r.ExitCode != 0)
        {
            string tail = (r.StdErr + "\n" + r.StdOut).Trim();
            if (tail.Length > 2000)
            {
                tail = tail[^2000..];
            }

            throw new DecoException($"{what} failed (exit {r.ExitCode}).\n{tail}", "tool-failed");
        }

        return r;
    }

    private static bool IsCurrentUserAdmin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return IsWindowsAdmin();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private sealed record ShotProvenance(StoreShots.Provenance Provenance, string PlanSha, StoreShots.ShotPlan Plan);

    private static string StoreCred(string flag, params string[] envNames)
    {
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return flag;
        }

        foreach (string n in envNames)
        {
            string? v = Environment.GetEnvironmentVariable(n);
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return string.Empty;
    }

    private static readonly string[] ReleaseStageNames = ReleaseStages.All;

    /// <summary>Runs this same CLI as a child with live-streamed output + log file. Returns the exit code.</summary>
    private static int RunDecoChild(string[] childArgs, string workDir, TimeSpan timeout, string? logPath)
    {
        (string fileName, List<string> preArgs) = DevCli.Commands.SelfLaunch.LaunchPrefix();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            Console.Error.WriteLine("Error: Cannot determine own executable for stage delegation.");
            return ExitCodes.Error;
        }

        var argList = preArgs.Concat(childArgs).ToList();

        static string Q(string s) => "\"" + s + "\"";
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (string a in argList)
        {
            proc.StartInfo.ArgumentList.Add(a);
        }

        StreamWriter? log = null;
        try
        {
            if (logPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                log = new StreamWriter(logPath, append: false, Encoding.UTF8);
            }

            proc.Start();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                Console.WriteLine(e.Data);
                log?.WriteLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                Console.Error.WriteLine(e.Data);
                log?.WriteLine(e.Data);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            bool exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
            if (!exited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                Console.Error.WriteLine($"Stage timed out after {timeout.TotalSeconds}s: {Q(string.Join(" ", childArgs))}");
                return ExitCodes.Error;
            }

            return proc.ExitCode;
        }
        finally
        {
            log?.Dispose();
        }
    }

    private const string GuestProbePs =
        "$q = (quser 2>&1 | Out-String).Trim() -replace \"\\s+\", \" \"\n"
        + "$m = [regex]::Match($q, \"$env:DECO_VM_USER\\s+(console|rdp-tcp#\\d+)\\s+(\\d+)\\s+(\\w+)\")\n"
        + "$sid = $null; $sname = ''; $sstate = ''\n"
        + "if ($m.Success) { $sname = $m.Groups[1].Value; $sid = [int]$m.Groups[2].Value; $sstate = $m.Groups[3].Value }\n"
        + "$locked = $false; $explorer = $false\n"
        + "if ($null -ne $sid) {\n"
        + "  $locked = ($null -ne (Get-Process -Name logonui -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid }))\n"
        + "  $explorer = ($null -ne (Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid }))\n"
        + "}\n"
        + "$b = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')\n"
        + "$free = [math]::Round((Get-PSDrive C).Free / 1GB, 1)\n"
        + "$ddot = Join-Path $env:LOCALAPPDATA 'Microsoft\\dotnet\\dotnet.exe'\n"
        + "[pscustomobject]@{ Q = $q; Boot = $b; Sid = $sid; SName = $sname; State = $sstate; Locked = $locked; Explorer = $explorer; FreeGB = $free; HasDotnet = (Test-Path -LiteralPath $ddot) } | ConvertTo-Json -Compress";

    private sealed record GuestProbeResult(
        string Q, string Boot, int? Sid, string SessionName, string State,
        bool Locked, bool Explorer, double FreeGB, bool HasDotnet);

    private sealed record VmPreflight(string BootUtc, int SessionId, string Size);

    /// <summary>RDP session id + deco's viewer PID (0 when no viewer tracked).</summary>
    private sealed record VmSession(int Sid, int ViewerPid);

}

