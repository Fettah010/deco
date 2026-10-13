using SkiaSharp;

namespace DevCli.Commands;

/// <summary>
/// Port of Scripts/new-setup-splash.ps1 with SkiaSharp: the Velopack
/// Setup.exe splash (640x400) — soft gradient backdrop, inset rounded card +
/// shadow, centered logo, app name, auto-width version pill, channel/size
/// meta line, "Installing…" caption. --static renders the timeless fallback
/// (no version pill, no meta line). Dark default, Light available.
/// </summary>
public static class SplashService
{
    public const int Width = 640;
    public const int Height = 400;

    public sealed record Request(
        string AppName,
        string Version,
        string Channel,
        double SizeMB,
        string LogoPath,
        string OutputPath,
        bool Static,
        string Theme);

    public static void Validate(Request req)
    {
        if (string.IsNullOrWhiteSpace(req.AppName))
        {
            throw new Core.DecoException("App name must not be empty.", "splash-no-app", Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (!req.Static && string.IsNullOrWhiteSpace(req.Version))
        {
            throw new Core.DecoException("Version is required unless --static.", "splash-no-version", Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (!string.Equals(req.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(req.Theme, "Light", StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.DecoException(
                $"Theme must be Dark|Light, got '{req.Theme}'.", "splash-bad-theme", Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (!File.Exists(req.LogoPath))
        {
            throw new Core.DecoException($"Logo not found: {req.LogoPath}", "splash-logo-missing");
        }

        if (string.IsNullOrWhiteSpace(req.OutputPath))
        {
            throw new Core.DecoException("Output path must not be empty.", "splash-no-output", Core.ExitCodes.UsageOrAuthNeeded);
        }
    }

    public static string ChannelLabel(string channel) =>
        string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase) ? "Beta channel" : "Stable channel";

    public static string MetaLine(string channel, double sizeMB, bool statik)
    {
        string label = ChannelLabel(channel);
        if (statik || sizeMB <= 0)
        {
            return label;
        }

        return $"{label}  ·  ~{Math.Round(sizeMB)} MB";
    }

    public static void Render(Request req)
    {
        Validate(req);
        bool light = string.Equals(req.Theme, "Light", StringComparison.OrdinalIgnoreCase);

        var backTop = light ? new SKColor(251, 252, 254) : new SKColor(27, 32, 43);
        var backBottom = light ? new SKColor(230, 238, 247) : new SKColor(13, 17, 23);
        var card = light ? new SKColor(255, 255, 255) : new SKColor(43, 43, 43);
        var cardEdge = light ? new SKColor(220, 228, 238) : new SKColor(70, 70, 70);
        var ink = light ? new SKColor(27, 27, 27) : new SKColor(255, 255, 255);
        var muted = light ? new SKColor(97, 97, 97) : new SKColor(173, 173, 173);
        var accent = new SKColor(0, 120, 212);
        var paper = new SKColor(255, 255, 255);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(req.OutputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var bmp = new SKBitmap(Width, Height);
        using (var canvas = new SKCanvas(bmp))
        {
            using (var bg = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, Height),
                    [backTop, backBottom], [0f, 1f], SKShaderTileMode.Clamp),
            })
            {
                canvas.DrawRect(new SKRect(0, 0, Width, Height), bg);
            }

            var cardRect = new SKRect(44, 36, 44 + 552, 36 + 316);
            using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 28), IsAntialias = true })
            {
                canvas.DrawRoundRect(new SKRect(44, 42, 44 + 552, 42 + 316), 28, 28, shadow);
            }

            using (var fill = new SKPaint { Color = card, IsAntialias = true })
            {
                canvas.DrawRoundRect(cardRect, 28, 28, fill);
            }

            using (var edge = new SKPaint
            {
                Color = cardEdge, IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = 1,
            })
            {
                canvas.DrawRoundRect(cardRect, 28, 28, edge);
            }

            using (var logo = SKBitmap.Decode(req.LogoPath))
            {
                if (logo is null)
                {
                    throw new Core.DecoException($"Logo is not a readable image: {req.LogoPath}", "splash-logo-unreadable");
                }

                var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawBitmap(logo, new SKRect((Width - 100) / 2f, 58, (Width + 100) / 2f, 158), sampling);
            }

            using var nameFont = new SKFont(Typeface("Segoe UI", SKFontStyleWeight.SemiBold), 27);
            using var pillFont = new SKFont(Typeface("Segoe UI", SKFontStyleWeight.SemiBold), 14);
            using var metaFont = new SKFont(Typeface("Segoe UI", SKFontStyleWeight.Normal), 14);
            using var namePaint = new SKPaint { Color = ink, IsAntialias = true };
            using var pillPaint = new SKPaint { Color = paper, IsAntialias = true };
            using var metaPaint = new SKPaint { Color = muted, IsAntialias = true };
            DrawCentered(canvas, req.AppName, nameFont, namePaint, Width / 2f, 168 + 34);

            if (!req.Static)
            {
                string pillText = "v" + req.Version;
                float pillW = pillFont.MeasureText(pillText) + 28;
                float pillX = (Width - pillW) / 2;
                using (var pillFill = new SKPaint { Color = accent, IsAntialias = true })
                {
                    canvas.DrawRoundRect(new SKRect(pillX, 218, pillX + pillW, 248), 15, 15, pillFill);
                }

                DrawCentered(canvas, pillText, pillFont, pillPaint, Width / 2f, 218 + 21);
                DrawCentered(canvas, MetaLine(req.Channel, req.SizeMB, req.Static), metaFont, metaPaint, Width / 2f, 254 + 20);
            }

            DrawCentered(canvas, "Installing…", metaFont, metaPaint, Width / 2f, 296 + 20);
        }

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(req.OutputPath);
        data.SaveTo(fs);
    }

    private static SKTypeface Typeface(string family, SKFontStyleWeight weight)
    {
        try
        {
            return SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.Default;
        }
        catch
        {
            return SKTypeface.Default;
        }
    }

    private static void DrawCentered(SKCanvas canvas, string text, SKFont font, SKPaint paint, float cx, float baselineY)
    {
        float w = font.MeasureText(text);
        using var blob = SKTextBlob.Create(text, font);
        canvas.DrawText(blob, cx - (w / 2f), baselineY, paint);
    }
}
