using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using FrameForge.Core;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Controls;
using FrameForge.Desktop.Rendering;
using FrameForge.Desktop.ViewModels;
using SkiaSharp;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// PIXEL-LEVEL proof that a StatusBar's declared BarTexture - once resolved and decoded - is what
/// paints the filled region. These tests drive the real production render path
/// (<see cref="RenderPipeline"/> over <see cref="VisualContentLayer"/>) into a bitmap and assert
/// on the actual painted pixels, not the model fraction.
/// </summary>
public sealed class StatusBarPixelRenderingTests
{
    /// <summary>The layout canvas behind a bar: the "empty" a StatusBar must leave where its fill is not.</summary>
    private static readonly SKColor Background = new((byte)0x0C, (byte)0x11, (byte)0x16);
    private static readonly SKColor Fallback = new(127, 178, 201);

    [Fact]
    public void ViewModel_end_to_end_paints_the_picked_texture_not_the_fallback()
    {
        // Runs the REAL app wiring: settings -> AssetRoots -> Load -> RefreshPresentation ->
        // vm.Assets resolver -> LayoutCanvas.PREVIEW render. Only the machine root gates it.
        var machineRoot = MachineAssetRoot();
        if (machineRoot is null)
            return;

        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frameforge-e2e-{Guid.NewGuid():N}.json");
        using var session = NewSession();
        MainWindowViewModel? vm = null;
        try
        {
            File.WriteAllText(settingsPath,
                $$"""{"assetRoots":["{{machineRoot.Replace("\\", "\\\\")}}"]}""");
            vm = OnUi(session, () => new MainWindowViewModel(settingsPath));

            var project = ProjectFactory.Create("e2e-bar", new[]
            {
                new FrameDef
                {
                    Name = "Hunt_Progress_Bar",
                    Kind = FrameKind.STATUSBAR,
                    Width = 250,
                    Height = 16,
                    Visual = new FrameVisual
                    {
                        StatusBar = new StatusBarVisual(0, 100, 50, "Interface/TargetingFrame/UI-StatusBar"),
                    },
                },
            });
            OnUi(session, () => { vm.Load(project, null, "test project"); return true; });
            OnUi(session, () => { vm.SetViewMode(CanvasViewMode.PREVIEW); return true; });

            var resolved = OnUi(session, () => vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar"));
            Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic.Message}");

            var render = RenderVmCanvas(session, vm);
            var variance = FillLuminanceRange(render);
            // The real BLP is a glossy vertical gradient; a flat fallback fill has range ~0.
            Assert.True(variance > 60,
                $"VM round-trip fill looks flat (luminance range {variance:0}); the texture did not paint");
        }
        finally
        {
            try { File.Delete(settingsPath); }
            catch (IOException) { }
            if (vm is not null)
                OnUi(session, () => { vm.Assets.Dispose(); return true; });
        }
    }

    [Fact]
    public void Resolved_texture_pixels_paint_the_fill_region()
    {
        using var session = NewSession();
        using var root = new AssetRoot(DecoratedBlp());
        using var resolver = new TextureAssetResolver();
        resolver.Configure(null, [root.Path]);

        var textured = RenderStatusBar(
            new StatusBarVisual(0, 100, 50, "Interface/PixelBar/SplitFill"), resolver, session);
        var withoutTexture = RenderStatusBar(new StatusBarVisual(0, 100, 50, null), null, session);

        // Fill region, top half and bottom half, well inside the bar and far from the fraction edge.
        AssertRed(textured, x: 68, y: 158, "textured fill's top half");
        AssertBlue(textured, x: 68, y: 228, "textured fill's bottom half");

        // The same position without a texture must be the uniform fallback fill, proving the
        // difference observed is the decoded texture and not something else.
        AssertFallbackFill(withoutTexture, x: 68, y: 158, "fallback fill without a BarTexture");

        // Pixels beyond the 50% fraction must be left empty: a StatusBar has no mandatory
        // background of its own (the canvas behind the bar shows through).
        AssertIsBackground(textured, x: 340, y: 158, "unfilled right half stays empty");
        AssertIsBackground(withoutTexture, x: 340, y: 158, "fallback fill also leaves the right half empty");

        // And the textured render really changed the pixels the fallback would have painted.
        var changed = CountPixelsWhere(textured, withoutTexture, (a, b) => !Close(a, b));
        Assert.True(changed > 20_000, $"expected the texture to repaint most of the fill; changed={changed}");
    }

    [Fact]
    public void Real_ui_statusbar_blp_renders_a_textured_not_a_uniform_fill()
    {
        var machineRoot = MachineAssetRoot();
        if (machineRoot is null)
            return;

        using var session = NewSession();
        using var resolver = new TextureAssetResolver();
        resolver.Configure(null, [machineRoot]);
        var resolved = resolver.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic.Message}");
        Assert.Equal(TextureFileFormat.Blp, resolved.Format);

        var textured = RenderStatusBar(
            new StatusBarVisual(0, 100, 50, "Interface/TargetingFrame/UI-StatusBar"), resolver, session);
        var fallback = RenderStatusBar(new StatusBarVisual(0, 100, 50, null), null, session);

        var variance = FillLuminanceRange(textured);
        var nopaint = FillLuminanceRange(fallback);
        Assert.True(variance > 60, $"real BLP fill has no meaningful luminance variation ({variance:0}); texture not visible");
        Assert.True(nopaint <= 12, $"fallback fill should be flat, got range {nopaint:0}");
        var changed = CountPixelsWhere(textured, fallback, (a, b) => !Close(a, b));
        Assert.True(changed > 20_000, $"real BLP did not repaint the fill from the fallback; changed={changed}");
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.50, 0.50)]
    [InlineData(0.75, 0.75)]
    [InlineData(1.00, 1.00)]
    [InlineData(0.70, 0.70)]
    public void Fill_fraction_is_visible_as_textured_width_not_model_math(double fraction, double expected)
    {
        using var session = NewSession();
        using var root = new AssetRoot(DecoratedBlp());
        using var resolver = new TextureAssetResolver();
        resolver.Configure(null, [root.Path]);

        var render = RenderStatusBar(
            new StatusBarVisual(0, 100, fraction * 100, "Interface/PixelBar/SplitFill"), resolver, session);
        var texturedColumns = CountTexturedColumns(render);

        var expectedColumns = render.Bar.Width * expected;
        var tolerance = render.Bar.Width * 0.02 + 2;
        Assert.True(Math.Abs(texturedColumns - expectedColumns) <= tolerance,
            $"fraction {fraction:0.00}: textured width {texturedColumns:0} vs {expectedColumns:0} (bar {render.Bar.Width:0}px)");
    }

    [Fact]
    public void Zero_fraction_paints_no_texture_and_no_unfilled_fill()
    {
        using var session = NewSession();
        using var root = new AssetRoot(DecoratedBlp());
        using var resolver = new TextureAssetResolver();
        resolver.Configure(null, [root.Path]);

        var render = RenderStatusBar(
            new StatusBarVisual(0, 100, 0, "Interface/PixelBar/SplitFill"), resolver, session);

        Assert.Equal(0, CountTexturedColumns(render));
        AssertAllInBarAreBackground(render, "0% fill must leave the bar entirely unpainted by the fill");
    }

    private static HeadlessUnitTestSession NewSession() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    private static T OnUi<T>(HeadlessUnitTestSession session, Func<T> action) =>
        session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    private static double CountTexturedColumns(StatusBarBitmap render)
    {
        var sampleY = (int)Math.Round(render.Bar.Y + render.Bar.Height * 0.25);
        var columns = 0;
        for (var x = render.Bar.X; x < render.Bar.Right; x++)
            if (IsRed(render.Pixel(x, sampleY)))
                columns++;
        return columns;
    }

    private static void AssertAllInBarAreBackground(StatusBarBitmap render, string message)
    {
        var y = (int)Math.Round(render.Bar.Y + render.Bar.Height * 0.5);
        var offenders = 0;
        for (var x = render.Bar.X + 2; x < render.Bar.Right - 2; x++)
            if (!Close(render.Pixel(x, y), Background))
                offenders++;
        Assert.True(offenders == 0, message);
    }

    private static double FillLuminanceRange(StatusBarBitmap render)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var endX = (int)Math.Round(render.Bar.X + render.Bar.Width * 0.5); // fill = first half at fraction 0.5
        for (var y = render.Bar.Y + 2; y < render.Bar.Bottom - 2; y++)
        for (var x = render.Bar.X + 2; x < endX - 2; x++)
        {
            var px = render.Pixel(x, y);
            if (Close(px, Background))
                continue;
            var lum = (px.Red + px.Green + px.Blue) / 3d;
            min = Math.Min(min, lum);
            max = Math.Max(max, lum);
        }
        return max - min;
    }

    private static int CountPixelsWhere(StatusBarBitmap first, StatusBarBitmap second, Func<SKColor, SKColor, bool> differs)
    {
        var count = 0;
        for (var y = first.Bar.Y; y < first.Bar.Bottom; y++)
        for (var x = first.Bar.X; x < first.Bar.Right; x++)
            if (differs(first.Pixel(x, y), second.Pixel(x, y)))
                count++;
        return count;
    }

    private static void AssertRed(StatusBarBitmap render, int x, int y, string where) =>
        Assert.True(IsRed(render.Pixel(x, y)),
            $"{where}: expected red at ({x},{y}) but got {render.Pixel(x, y)}");

    private static void AssertBlue(StatusBarBitmap render, int x, int y, string where) =>
        Assert.True(IsBlue(render.Pixel(x, y)),
            $"{where}: expected blue at ({x},{y}) but got {render.Pixel(x, y)}");

    private static bool IsRed(SKColor c) => c.Red > 150 && c.Green < 90 && c.Blue < 90;
    private static bool IsBlue(SKColor c) => c.Blue > 150 && c.Red < 90 && c.Green < 90;

    private static void AssertFallbackFill(StatusBarBitmap render, int x, int y, string where)
    {
        var c = render.Pixel(x, y);
        Assert.True(Close(c, Fallback), $"{where}: expected the #7FB2C9 fallback fill at ({x},{y}) but got {c}");
    }

    private static void AssertIsBackground(StatusBarBitmap render, int x, int y, string where)
    {
        var c = render.Pixel(x, y);
        Assert.True(Close(c, Background),
            $"{where}: expected the {Background} canvas to show through at ({x},{y}) but got {c}");
    }

    private static bool Close(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) <= 6 && Math.Abs(a.Green - b.Green) <= 6 && Math.Abs(a.Blue - b.Blue) <= 6;

    private static string? MachineAssetRoot()
    {
        var root = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_ASSET_ROOT");
        if (Directory.Exists(root))
            return root!;
        var known = "/home/smithkt/WoW-335a-Interface-assets";
        return Directory.Exists(known) ? known : null;
    }

    /// <summary>A 64x8 BLP2/DXT1 whose top half decodes red and bottom half blue.</summary>
    private static byte[] DecoratedBlp()
    {
        var blocksPerRow = 16; // 64 / 4
        var topRow = Enumerable.Range(0, blocksPerRow).Select(_ => Dxt1Block(0xf800, 0xf800, 0));
        var bottomRow = Enumerable.Range(0, blocksPerRow).Select(_ => Dxt1Block(0x001f, 0x001f, 0));
        return Blp2(64, 8, 0, 0, topRow.SelectMany(b => b).Concat(bottomRow.SelectMany(b => b)).ToArray());
    }

    private static byte[] Blp2(int width, int height, byte alphaDepth, byte alphaEncoding, byte[] mip, byte encoding = 2)
    {
        var bytes = new byte[BlpTextureDecoder.HeaderSize + BlpTextureDecoder.PaletteSize + mip.Length];
        "BLP2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
        bytes[8] = encoding;
        bytes[9] = alphaDepth;
        bytes[10] = alphaEncoding;
        bytes[11] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4),
            BlpTextureDecoder.HeaderSize + BlpTextureDecoder.PaletteSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), (uint)mip.Length);
        mip.CopyTo(bytes, BlpTextureDecoder.HeaderSize + BlpTextureDecoder.PaletteSize);
        return bytes;
    }

    private static byte[] Dxt1Block(ushort color0, ushort color1, byte indices)
    {
        var block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), color0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2, 2), color1);
        block[4] = block[5] = block[6] = block[7] = indices;
        return block;
    }

    private sealed class AssetRoot : IDisposable
    {
        public AssetRoot(byte[] blp)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frameforge-pixel-bar-{Guid.NewGuid():N}");
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Interface", "PixelBar"));
            File.WriteAllBytes(System.IO.Path.Combine(Path, "Interface", "PixelBar", "SplitFill.blp"), blp);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }

    private const double BarWidth = 120;
    private const double BarHeight = 28;
    private const double CanvasWidth = 560; // fit-to-content margin gives exactly zoom 4.0 with a 120px bar.
    private const double CanvasHeight = 400;

    private static StatusBarBitmap RenderStatusBar(StatusBarVisual bar, ITextureAssetResolver? resolver,
        HeadlessUnitTestSession session)
    {
        var project = ProjectFactory.Create("pixel-bar", new[]
        {
            new FrameDef
            {
                Name = "PixelBar",
                Kind = FrameKind.STATUSBAR,
                Width = BarWidth,
                Height = BarHeight,
                Visual = new FrameVisual { StatusBar = bar },
            },
        });
        var layout = LayoutResolver.Resolve(project);
        return CaptureCanvas(session, project, layout, layout.Rects["PixelBar"], resolver);
    }

    private static StatusBarBitmap RenderVmCanvas(HeadlessUnitTestSession session, MainWindowViewModel vm)
    {
        var project = vm.PresentationProject;
        var layout = vm.Layout;
        var resolver = vm.Assets;
        var frameRect = layout.Rects.Values.First(rect => rect.Width > 0 && rect.Height > 0);
        return CaptureCanvas(session, project, layout, frameRect, resolver);
    }

    private static StatusBarBitmap CaptureCanvas(HeadlessUnitTestSession session, Project project,
        LayoutResult layout, FrameRect frameRect, ITextureAssetResolver? resolver)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var result = OnUi(session, () =>
            {
                var canvas = new LayoutCanvas
                {
                    Project = project,
                    Layout = layout,
                    AssetResolver = resolver,
                    Mode = CanvasViewMode.PREVIEW,
                };
                var window = new Window
                {
                    Content = canvas,
                    Width = CanvasWidth,
                    Height = CanvasHeight,
                    ShowActivated = false,
                };
                try
                {
                    window.Show();
                    window.CaptureRenderedFrame(); // first pass: layout + initial paint
                    canvas.FitToContent();
                    window.CaptureRenderedFrame();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    using (var frame = window.CaptureRenderedFrame())
                    {
                        if (frame is null)
                            return null;
                        var origin = new CanvasOrigin(canvas.Bounds.Width / 2d, canvas.Bounds.Height / 2d);
                        var box = canvas.Viewport.RectToCanvas(frameRect, origin);
                        return CopyPixels(frame, box);
                    }
                }
                finally
                {
                    window.Close();
                }
            });

            if (result is not null)
                return result;
            System.Threading.Thread.Sleep(8); // let the headless render timer become due
        }

        Assert.Fail("Headless window rendered no frame after retries.");
        throw new InvalidOperationException();
    }

    private static StatusBarBitmap CopyPixels(WriteableBitmap frame, CanvasBox box)
    {
        using var locked = frame.Lock();
        var width = locked.Size.Width;
        var height = locked.Size.Height;
        var rowBytes = width * 4;
        // The headless Skia framebuffer is Rgba8888 in memory (byte 0 = red), with premultiplied
        // alpha. Reading it as anything else swaps R and B for every sample.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new SKBitmap(info);
        var dst = pixels.GetPixels();
        var src = locked.Address;
        var row = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(IntPtr.Add(src, y * locked.RowBytes), row, 0, rowBytes);
            Marshal.Copy(row, 0, IntPtr.Add(dst, y * rowBytes), rowBytes);
        }
        return new StatusBarBitmap(pixels, new IntBox(
            (int)Math.Round(box.X), (int)Math.Round(box.Y),
            (int)Math.Round(box.Width), (int)Math.Round(box.Height)));
    }

    private sealed record IntBox(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    private sealed record StatusBarBitmap(SKBitmap Pixels, IntBox Bar)
    {
        public SKColor Pixel(int x, int y) => Pixels.GetPixel(x, y);
    }
}