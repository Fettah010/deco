using SkiaSharp;

namespace DevCli.Commands;

/// <summary>
/// Pure port of Scripts/set-app-icon.ps1 using SkiaSharp (MIT, cross-platform;
/// replaces the Windows-only System.Drawing path).
/// From one square source PNG (≥512px): trims transparent padding, renders
/// the hand-written multi-entry ICO {16,24,32,48,64,128,256} (PNG-compressed
/// entries, Vista+), theme copies, and Logo PNGs. Backs up replaced files to
/// .icon-backup/&lt;timestamp&gt;/ and read-back-verifies every output.
/// </summary>
public static class IconService
{
    public static readonly int[] IcoSizes = [16, 24, 32, 48, 64, 128, 256];
    public static readonly int[] SmallLogos = [64, 48, 32, 16];
    public const int LogoSize = 256;
    public const int MinSourceSize = 512;

    public sealed record Plan(
        int SourceWidth,
        int CropSize,
        int ContentWidth,
        int ContentHeight,
        string[] Writes,
        string BackupDir);

    public static void ValidateOptions(string source, int paddingPercent)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            throw new Core.DecoException(
                $"Source not found: {source}",
                "icon-source-missing",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (!string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.DecoException(
                $"Source must be a PNG file: {source}",
                "icon-source-not-png",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (paddingPercent is < 0 or > 25)
        {
            throw new Core.DecoException(
                $"ContentPaddingPercent must be 0-25, got {paddingPercent}.",
                "icon-bad-padding",
                Core.ExitCodes.UsageOrAuthNeeded);
        }
    }

    public static Plan BuildPlan(string assetsDir, int paddingPercent, int srcWidth, int contentW, int contentH, int cropSize)
    {
        var writes = new List<string>
        {
            Path.Combine(assetsDir, "app.ico"),
            Path.Combine(assetsDir, "app-light.ico"),
            Path.Combine(assetsDir, "app-dark.ico"),
            Path.Combine(assetsDir, "Logo.png"),
        };
        writes.AddRange(SmallLogos.Select(s => Path.Combine(assetsDir, $"Logo-{s}.png")));
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string backupDir = Path.Combine(Directory.GetParent(assetsDir)?.FullName ?? assetsDir, ".icon-backup", stamp);
        return new Plan(srcWidth, cropSize, contentW, contentH, [.. writes], backupDir);
    }

    /// <summary>Full run: load → validate → trim → write → verify. Throws DecoException with all failures.</summary>
    public static Plan Run(string source, string assetsDir, int paddingPercent, bool dryRun)
    {
        ValidateOptions(source, paddingPercent);
        if (!Directory.Exists(assetsDir))
        {
            throw new Core.DecoException(
                $"Assets dir not found: {assetsDir} (pass --assets-dir).",
                "icon-assets-missing");
        }

        using var src = SKBitmap.Decode(source);
        if (src is null)
        {
            throw new Core.DecoException($"Source is not a readable PNG: {source}", "icon-unreadable");
        }

        if (src.Width != src.Height)
        {
            throw new Core.DecoException(
                $"Source must be square, got {src.Width}x{src.Height}.",
                "icon-not-square");
        }

        if (src.Width < MinSourceSize)
        {
            throw new Core.DecoException(
                $"Source must be at least {MinSourceSize}x{MinSourceSize}, got {src.Width}x{src.Height}. Upscaling would blur every asset.",
                "icon-too-small");
        }

        (int minX, int minY, int maxX, int maxY) = FindVisibleBounds(src);
        int contentW = maxX - minX + 1;
        int contentH = maxY - minY + 1;
        int padding = Math.Max(1, (int)Math.Ceiling(Math.Max(contentW, contentH) * paddingPercent / 100.0));
        int cropSize = Math.Min(src.Width, Math.Max(contentW, contentH) + (2 * padding));
        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;
        int left = Math.Max(0, Math.Min(src.Width - cropSize, (int)Math.Round(centerX - (cropSize / 2.0))));
        int top = Math.Max(0, Math.Min(src.Height - cropSize, (int)Math.Round(centerY - (cropSize / 2.0))));

        Plan plan = BuildPlan(assetsDir, paddingPercent, src.Width, contentW, contentH, cropSize);
        if (dryRun)
        {
            return plan;
        }

        using var render = Crop(src, left, top, cropSize);
        BackupTargets(plan.Writes, plan.BackupDir);

        var pngBlobs = IcoSizes.Select(s => new { Size = s, Data = ResizeToPng(render, s) }).ToList();
        WriteIco(Path.Combine(assetsDir, "app.ico"), pngBlobs.Select(b => (b.Size, b.Data)).ToList());
        File.Copy(Path.Combine(assetsDir, "app.ico"), Path.Combine(assetsDir, "app-light.ico"), overwrite: true);
        File.Copy(Path.Combine(assetsDir, "app.ico"), Path.Combine(assetsDir, "app-dark.ico"), overwrite: true);

        using (var logo = Resize(render, LogoSize))
        {
            SavePng(logo, Path.Combine(assetsDir, "Logo.png"));
        }

        foreach (int s in SmallLogos)
        {
            using var small = Resize(render, s);
            SavePng(small, Path.Combine(assetsDir, $"Logo-{s}.png"));
        }

        VerifyOutputs(assetsDir);
        return plan;
    }

    internal static (int MinX, int MinY, int MaxX, int MaxY) FindVisibleBounds(SKBitmap img)
    {
        int minX = img.Width, minY = img.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                if (img.GetPixel(x, y).Alpha > 8)
                {
                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }
        }

        if (maxX < 0)
        {
            throw new Core.DecoException("Source contains no visible pixels.", "icon-empty");
        }

        return (minX, minY, maxX, maxY);
    }

    internal static SKBitmap Crop(SKBitmap src, int left, int top, int size)
    {
        var dst = new SKBitmap(size, size, src.ColorType, src.AlphaType);
        var subset = new SKRectI(left, top, left + size, top + size);
        if (!src.ExtractSubset(dst, subset))
        {
            dst.Dispose();
            throw new Core.DecoException("Could not crop the source image.", "icon-crop-failed");
        }

        return dst;
    }

    internal static SKBitmap Resize(SKBitmap src, int size)
    {
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        SKBitmap? dst = src.Resize(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul), sampling);
        if (dst is null)
        {
            throw new Core.DecoException($"Could not resize to {size}px.", "icon-resize-failed");
        }

        return dst;
    }

    internal static byte[] ResizeToPng(SKBitmap src, int size)
    {
        using var bmp = Resize(src, size);
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static void SavePng(SKBitmap bmp, string path)
    {
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    internal static void WriteIco(string path, List<(int Size, byte[] Data)> blobs)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)blobs.Count);
        uint offset = (uint)(6 + (16 * blobs.Count));
        foreach ((int size, byte[] data) in blobs)
        {
            byte dim = size >= 256 ? (byte)0 : (byte)size;
            w.Write(dim);
            w.Write(dim);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)data.Length);
            w.Write(offset);
            offset += (uint)data.Length;
        }

        foreach ((_, byte[] data) in blobs)
        {
            w.Write(data);
        }
    }

    /// <summary>Reads the entry sizes from a PNG-compressed ICO (256 stored as byte 0).</summary>
    public static List<int> ReadIcoSizes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6)
        {
            return [];
        }

        int count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();
        for (int i = 0; i < count && 6 + (i * 16) < bytes.Length; i++)
        {
            byte w = bytes[6 + (i * 16)];
            sizes.Add(w == 0 ? 256 : w);
        }

        return sizes;
    }

    private static void BackupTargets(string[] targets, string backupDir)
    {
        foreach (string t in targets)
        {
            if (!File.Exists(t))
            {
                continue;
            }

            Directory.CreateDirectory(backupDir);
            File.Copy(t, Path.Combine(backupDir, Path.GetFileName(t)), overwrite: true);
        }
    }

    private static void VerifyOutputs(string assetsDir)
    {
        var failures = new List<string>();
        var checks = new Dictionary<string, int> { ["Logo.png"] = LogoSize };
        foreach (int s in SmallLogos)
        {
            checks[$"Logo-{s}.png"] = s;
        }

        foreach ((string name, int size) in checks)
        {
            try
            {
                SKImageInfo info = SKBitmap.DecodeBounds(Path.Combine(assetsDir, name));
                if (info.Width != size || info.Height != size)
                {
                    failures.Add($"{name} is {info.Width}x{info.Height}, expected {size}x{size}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{name} unreadable: {ex.Message}");
            }
        }

        List<int> icoSizes = ReadIcoSizes(Path.Combine(assetsDir, "app.ico"));
        foreach (int s in IcoSizes)
        {
            if (!icoSizes.Contains(s))
            {
                failures.Add($"app.ico is missing the {s}px entry (has {string.Join(",", icoSizes)})");
            }
        }

        if (failures.Count > 0)
        {
            throw new Core.DecoException(
                "Verification failed:\n  " + string.Join("\n  ", failures),
                "icon-verify-failed");
        }
    }
}
