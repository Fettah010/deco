using System.Security;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Pure port of the Scripts/build-msix.ps1 staging/pack logic: version
/// resolution, byte-preserving manifest patch, tile-art specs, .appinstaller
/// feed builder, staging checklist, makeappx/signtool arg builders.
/// Process execution (publish/makeappx/signtool) stays in the handler.
/// </summary>
public static partial class MsixPackaging
{
    public static readonly (string Name, int W, int H)[] TileSpecs =
    [
        ("StoreLogo.png", 50, 50),
        ("Square44x44Logo.png", 44, 44),
        ("Square150x150Logo.png", 150, 150),
        ("Wide310x150Logo.png", 310, 150),
        ("SplashScreen.png", 620, 300),
    ];

    [GeneratedRegex(@"<Identity\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityTag();

    public static string MsBuildPlatform(string platform) =>
        string.Equals(platform, "arm64", StringComparison.OrdinalIgnoreCase) ? "ARM64" : "x64";

    public static string Rid(string platform) =>
        string.Equals(platform, "arm64", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";

    public static string Arch(string platform) =>
        string.Equals(platform, "arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";

    /// <summary>Renders MSIX tile art from Logo.png (stretch-fit per spec, transparent bg).</summary>
    public static void RenderTileArt(string logoPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var src = SkiaSharp.SKBitmap.Decode(logoPath);
        if (src is null)
        {
            throw new Core.DecoException($"Logo is not a readable image: {logoPath}", "msix-logo-unreadable");
        }

        foreach ((string name, int w, int h) in TileSpecs)
        {
            using var dst = src.Resize(
                new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul),
                new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
            if (dst is null)
            {
                throw new Core.DecoException($"Could not render tile {name}.", "msix-tile-failed");
            }

            using var img = SkiaSharp.SKImage.FromBitmap(dst);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(Path.Combine(destDir, name));
            data.SaveTo(fs);
        }
    }

    public static void ValidatePlatform(string platform)
    {
        if (!string.Equals(platform, "x64", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(platform, "arm64", StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.DecoException(
                $"Platform must be x64|arm64, got '{platform}'.",
                "msix-bad-platform",
                Core.ExitCodes.UsageOrAuthNeeded);
        }
    }

    public static string ResolveVersion(string requested, Func<string> fromCsproj)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        string v = fromCsproj();
        if (string.IsNullOrWhiteSpace(v))
        {
            throw new Core.DecoException("No <Version> found in the csproj; pass --version.", "msix-no-version");
        }

        return v.Trim();
    }

    /// <summary>Byte-preserving patch of the staged manifest Identity (Version/Architecture/Publisher).</summary>
    public static string PatchManifestText(string manifestText, string version, string arch, string publisher)
    {
        Match m = IdentityTag().Match(manifestText);
        if (!m.Success)
        {
            throw new Core.DecoException("Manifest has no <Identity> element.", "msix-no-identity");
        }

        string tag = m.Value;
        tag = ReplaceAttr(tag, "Version", version);
        tag = ReplaceAttr(tag, "ProcessorArchitecture", arch);
        tag = ReplaceAttr(tag, "Publisher", publisher);
        return manifestText[..m.Index] + tag + manifestText[(m.Index + m.Length)..];
    }

    private static string ReplaceAttr(string tag, string name, string value)
    {
        string pattern = $@"{name}\s*=\s*""[^""]*""";
        if (Regex.IsMatch(tag, pattern))
        {
            return Regex.Replace(tag, pattern, $@"{name}=""{value}""");
        }

        return tag.Replace(">", $" {name}=\"{value}\">", StringComparison.Ordinal);
    }

    public static (string? Name, string? Publisher) ReadIdentity(string manifestText)
    {
        Match m = IdentityTag().Match(manifestText);
        if (!m.Success)
        {
            return (null, null);
        }

        return (Attr(m.Value, "Name"), Attr(m.Value, "Publisher"));
    }

    private static string? Attr(string tag, string name)
    {
        Match m = Regex.Match(tag, $@"{name}\s*=\s*""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string MsixFileName(string appId, string version, string arch) =>
        $"{appId}_{version}_{arch}.msix";

    /// <summary>2021-schema .appinstaller feed, XML-escaped + well-formedness-checked like the ps1.</summary>
    public static string BuildAppInstallerXml(string packageUri, string version, string packageName,
        string publisher, string arch, int hoursBetweenUpdateChecks)
    {
        string xml =
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <AppInstaller Uri="{SecurityElement.Escape(packageUri)}" Version="{SecurityElement.Escape(version)}" xmlns="http://schemas.microsoft.com/appx/appinstaller/2021">
               <MainPackage Name="{SecurityElement.Escape(packageName)}" Publisher="{SecurityElement.Escape(publisher)}" Version="{SecurityElement.Escape(version)}" ProcessorArchitecture="{SecurityElement.Escape(arch)}" Uri="{SecurityElement.Escape(packageUri)}" />
               <UpdateSettings>
                 <OnLaunch HoursBetweenUpdateChecks="{hoursBetweenUpdateChecks}" ShowPrompt="true" UpdateBlocksActivation="false" />
                 <AutomaticBackgroundTask />
               </UpdateSettings>
             </AppInstaller>
             """;
        xml = xml.TrimStart();
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
        }
        catch (Exception ex)
        {
            throw new Core.DecoException($"Generated .appinstaller is not well-formed XML: {ex.Message}", "msix-bad-feed");
        }

        return xml;
    }

    /// <summary>Structural staging checklist (manifest, exe, PRI pair, tiles). App extras via requiredExtras.</summary>
    public static List<string> RequiredStagingFiles(string exeName, string priName, string[] requiredExtras) =>
        [
            "AppxManifest.xml", exeName, priName, "resources.pri",
            @"Assets\StoreLogo.png", @"Assets\Square44x44Logo.png",
            @"Assets\Square150x150Logo.png", @"Assets\Wide310x150Logo.png",
            @"Assets\SplashScreen.png",
            .. requiredExtras,
        ];

    public static List<string> MissingStagingFiles(string stageAppDir, string exeName, string priName, string[] requiredExtras)
    {
        var missing = new List<string>();
        foreach (string rel in RequiredStagingFiles(exeName, priName, requiredExtras))
        {
            if (!File.Exists(Path.Combine(stageAppDir, rel)))
            {
                missing.Add(rel);
            }
        }

        return missing;
    }

    public static List<string> MakeAppxPackArgs(string appDir, string msix) =>
        ["pack", "/d", appDir, "/p", msix, "/nv"];

    public static List<string> MakeAppxBundleArgs(string bundleDir, string bundle) =>
        ["bundle", "/d", bundleDir, "/p", bundle];

    public static List<string> SignArgs(string certPath, string certPassword, string timestampUrl, string target)
    {
        var args = new List<string> { "sign", "/fd", "SHA256", "/f", certPath, "/tr", timestampUrl, "/td", "SHA256" };
        if (!string.IsNullOrWhiteSpace(certPassword))
        {
            args.AddRange(["/p", certPassword]);
        }

        args.Add(target);
        return args;
    }

    /// <summary>makepri priconfig: RESFILES for staged art, EMBEDFILES for XAML, PRI merge for frameworks.</summary>
    public static string PriconfigXml(string packListPath, string embedDir, string embedResfiles, string mergeListPath)
    {
        const string qualifiers = """
              <qualifier name="Language" value="en-US" />
              <qualifier name="Contrast" value="standard" />
              <qualifier name="Scale" value="200" />
              <qualifier name="HomeRegion" value="001" />
              <qualifier name="TargetSize" value="256" />
              <qualifier name="LayoutDirection" value="LTR" />
              <qualifier name="DXFeatureLevel" value="DX9" />
              <qualifier name="Configuration" value="" />
              <qualifier name="AlternateForm" value="" />
              <qualifier name="Platform" value="UAP" />
            """;
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <resources targetOsVersion="10.0.0" majorVersion="1">
              <index root="\" startIndexAt="{packListPath}">
                <default>{qualifiers}
                </default>
                <indexer-config type="RESFILES" qualifierDelimiter="." />
              </index>
              <index root="{embedDir}" startIndexAt="{embedResfiles}">
                <default>{qualifiers}
                </default>
                <indexer-config type="RESFILES" qualifierDelimiter="." />
                <indexer-config type="EMBEDFILES" />
              </index>
              <index root="\" startIndexAt="{mergeListPath}">
                <default>{qualifiers}
                </default>
                <indexer-config type="PRI" />
                <indexer-config type="RESFILES" qualifierDelimiter="." />
              </index>
            </resources>
            """;
    }
}
