using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class WeightCheckTests
{
    [Fact]
    public void Evaluate_NoBaseline_NeverJumps()
    {
        var e = WeightCheck.Evaluate(250_000_000, null);
        Assert.Equal(238.4, e.Mb);
        Assert.False(e.Jumped);
        Assert.Null(e.DeltaPct);
    }

    [Fact]
    public void Evaluate_JumpOverTenPercent()
    {
        var e = WeightCheck.Evaluate(1150, 1000);
        Assert.True(e.Jumped);
        Assert.Equal(15.0, e.DeltaPct);
    }

    [Fact]
    public void Evaluate_WithinGuardrail()
    {
        var e = WeightCheck.Evaluate(1050, 1000);
        Assert.False(e.Jumped);
        Assert.Equal(5.0, e.DeltaPct);
    }

    [Fact]
    public void SumDirectoryBytes_SumsFiles()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-w-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, "a.bin"), new byte[100]);
            Directory.CreateDirectory(Path.Combine(tmp, "sub"));
            File.WriteAllBytes(Path.Combine(tmp, "sub", "b.bin"), new byte[50]);
            Assert.Equal(150, WeightCheck.SumDirectoryBytes(tmp));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}

public sealed class IconServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _assets;

    public IconServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-icon-" + Guid.NewGuid().ToString("N"));
        _assets = Path.Combine(_tmp, "Assets");
        Directory.CreateDirectory(_assets);
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

    private string WriteSource(int size = 512, int contentMargin = 100)
    {
        // Transparent square with a centered opaque disc.
        string path = Path.Combine(_tmp, "source.png");
        using var bmp = new SkiaSharp.SKBitmap(size, size);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            using var paint = new SkiaSharp.SKPaint
            {
                Color = new SkiaSharp.SKColor(30, 120, 200, 255),
                IsAntialias = true,
            };
            float c = size / 2f;
            canvas.DrawCircle(c, c, c - contentMargin, paint);
        }

        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }

    [Fact]
    public void Run_WritesAllAssets_AndVerifies()
    {
        string src = WriteSource();
        var plan = IconService.Run(src, _assets, paddingPercent: 0, dryRun: false);

        Assert.Equal(8, plan.Writes.Length); // app.ico, light, dark, Logo + 4 small
        foreach (string f in plan.Writes)
        {
            Assert.True(File.Exists(f), f);
        }

        List<int> sizes = IconService.ReadIcoSizes(Path.Combine(_assets, "app.ico"));
        Assert.Equal([16, 24, 32, 48, 64, 128, 256], sizes);

        var info = SkiaSharp.SKBitmap.DecodeBounds(Path.Combine(_assets, "Logo.png"));
        Assert.Equal(256, info.Width);
        Assert.Equal(256, info.Height);
    }

    [Fact]
    public void Run_DryRun_WritesNothing()
    {
        string src = WriteSource();
        IconService.Run(src, _assets, paddingPercent: 0, dryRun: true);
        Assert.Empty(Directory.GetFiles(_assets));
    }

    [Fact]
    public void Run_RejectsNonSquare()
    {
        string path = Path.Combine(_tmp, "wide.png");
        using (var bmp = new SkiaSharp.SKBitmap(512, 256))
        {
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }

        Assert.Throws<Core.DecoException>(() => IconService.Run(path, _assets, 0, dryRun: true));
    }

    [Fact]
    public void Run_RejectsTooSmall()
    {
        string src = WriteSource(size: 256);
        Assert.Throws<Core.DecoException>(() => IconService.Run(src, _assets, 0, dryRun: true));
    }

    [Fact]
    public void ValidateOptions_RejectsBadPadding()
    {
        string src = WriteSource();
        Assert.Throws<Core.DecoException>(() => IconService.ValidateOptions(src, 26));
    }
}

public sealed class ShortcutServiceTests
{
    [Fact]
    public void Plan_BuildsTwoShortcuts()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-sc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string exe = Path.Combine(tmp, "App.exe");
            string script = Path.Combine(tmp, "run-app.ps1");
            File.WriteAllBytes(exe, [1, 2, 3]);
            File.WriteAllText(script, "# dev");
            string icon = Path.Combine(tmp, "app.ico");
            File.WriteAllBytes(icon, [0]);

            var plan = ShortcutService.Plan("Sample", exe, script, tmp, tmp, icon);
            Assert.Equal("Sample.lnk", plan.Launch.Name);
            Assert.Equal(exe, plan.Launch.Target);
            Assert.Equal("Sample (Dev).lnk", plan.Dev.Name);
            Assert.Contains("run-app.ps1", plan.Dev.Arguments);
            Assert.EndsWith("powershell.exe", plan.Dev.Target, StringComparison.OrdinalIgnoreCase);
            Assert.Equal($"{icon},0", plan.Launch.IconLocation);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Plan_RequiresExistingExe()
    {
        Assert.Throws<Core.DecoException>(() =>
            ShortcutService.Plan("Sample", "C:\\nope\\App.exe", "C:\\nope\\run.ps1", "C:\\nope", "C:\\nope", null));
    }

    [Fact]
    public void ResolveDevScript_PrefersRunApp()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-sc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "Scripts"));
        try
        {
            File.WriteAllText(Path.Combine(tmp, "Scripts", "run-app.ps1"), "#");
            File.WriteAllText(Path.Combine(tmp, "Scripts", "launch-dev.ps1"), "#");
            Assert.EndsWith("run-app.ps1", ShortcutService.ResolveDevScript(tmp, null));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
