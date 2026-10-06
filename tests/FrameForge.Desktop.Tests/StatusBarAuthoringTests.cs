using Avalonia;
using FrameForge.Core;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Rendering;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using SkiaSharp;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// StatusBar as a first-class DESIGN object: creation defaults, authorable Min/Max/Preview fields,
/// bar texture and colour, persistence, lock behaviour, and - crucially - that the fill the canvas
/// renders really does track the authored Preview value in pixels.
/// </summary>
public sealed class StatusBarAuthoringTests
{
    [Fact]
    public void Add_status_bar_creates_a_half_filled_authorable_bar()
    {
        var vm = NewViewModel();
        vm.NewObjectName = "Health Bar";

        vm.AddDesignStatusBar();

        Assert.Equal(FrameKind.STATUSBAR, vm.SelectedFrame?.Kind);
        Assert.Equal(vm.SelectedName, vm.SelectedFrame?.Name);
        Assert.Equal("Health Bar", vm.Project.Editor.DesignObjectFor(vm.SelectedName!)?.DisplayName);

        var bar = vm.SelectedFrame!.Visual!.StatusBar!;
        Assert.Equal(0, bar.MinValue);
        Assert.Equal(100, bar.MaxValue);
        Assert.Equal(50, bar.DefaultValue);
        Assert.Equal(0.5, bar.DefaultFraction!.Value, 9);
        Assert.Equal(120, vm.SelectedFrame.Width);
        Assert.Equal(16, vm.SelectedFrame.Height);

        Assert.Contains(vm.SelectedName!, vm.Project.Editor.EffectiveDesignOrder(vm.Project));
        Assert.True(vm.IsDesignStatusBarSelected);
        Assert.True(vm.CanEditDesignStatusBar);
        Assert.Equal("50", vm.StatusBarValueDraft);
    }

    [Fact]
    public void Min_max_and_preview_value_are_editable_and_change_the_model_fraction()
    {
        var vm = NewViewModel();
        vm.AddDesignStatusBar();

        vm.StatusBarMinDraft = "0";
        Assert.True(vm.CommitStatusBarField("statusBarMin"));
        vm.StatusBarMaxDraft = "10";
        Assert.True(vm.CommitStatusBarField("statusBarMax"));
        vm.StatusBarValueDraft = "7";
        Assert.True(vm.CommitStatusBarField("statusBarValue"));

        var bar = vm.SelectedFrame!.Visual!.StatusBar!;
        Assert.Equal(0, bar.MinValue);
        Assert.Equal(10, bar.MaxValue);
        Assert.Equal(7, bar.DefaultValue);
        Assert.Equal(0.7, bar.DefaultFraction!.Value, 9);

        vm.StatusBarValueDraft = "3";
        Assert.True(vm.CommitStatusBarField("statusBarValue"));
        Assert.Equal(0.3, vm.SelectedFrame!.Visual!.StatusBar!.DefaultFraction!.Value, 9);
        Assert.Contains("30%", vm.SelectedStatusBarFractionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Bar_colour_and_texture_commit_to_the_model()
    {
        var vm = NewViewModel();
        vm.AddDesignStatusBar();

        vm.StatusBarColorDraft = "#FF0000";
        Assert.True(vm.CommitStatusBarField("statusBarColor"));
        Assert.Equal(new ColorRgba(1, 0, 0, 1), vm.SelectedFrame!.Visual!.StatusBar!.BarColor);

        vm.StatusBarTextureDraft = @"Interface\TargetingFrame\UI-StatusBar";
        Assert.True(vm.CommitStatusBarField("statusBarTexture"));
        Assert.Equal(@"Interface\TargetingFrame\UI-StatusBar", vm.SelectedFrame!.Visual!.StatusBar!.BarTexture);
    }

    [Fact]
    public void A_bad_number_is_reported_and_never_touches_the_model()
    {
        var vm = NewViewModel();
        vm.AddDesignStatusBar();

        vm.StatusBarMinDraft = "abc";
        Assert.False(vm.CommitStatusBarField("statusBarMin"));

        Assert.NotEmpty(vm.StatusBarValidation);
        Assert.Equal(0, vm.SelectedFrame!.Visual!.StatusBar!.MinValue);
    }

    [Fact]
    public void A_locked_status_bar_is_not_editable()
    {
        var vm = NewViewModel();
        vm.AddDesignStatusBar();
        var name = vm.SelectedName!;
        vm.SetElementLocked(name, true);

        Assert.False(vm.CanEditDesignStatusBar);

        vm.StatusBarMaxDraft = "999";
        Assert.False(vm.CommitStatusBarField("statusBarMax"));
        Assert.Equal(100, vm.Project.Find(name)!.Visual!.StatusBar!.MaxValue);
    }

    [Fact]
    public void Authored_StatusBar_fields_survive_the_project_round_trip()
    {
        var vm = NewViewModel();
        vm.AddDesignStatusBar();
        vm.StatusBarMaxDraft = "10";
        vm.CommitStatusBarField("statusBarMax");
        vm.StatusBarValueDraft = "7";
        vm.CommitStatusBarField("statusBarValue");
        vm.StatusBarColorDraft = "#0D2F6D";
        vm.CommitStatusBarField("statusBarColor");
        vm.StatusBarTextureDraft = @"Interface\TargetingFrame\UI-StatusBar";
        vm.CommitStatusBarField("statusBarTexture");

        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(vm.Project));
        Assert.True(reopened.Ok, reopened.ErrorText);

        var restored = reopened.Project!.Find(vm.SelectedName!)!.Visual!.StatusBar!;
        Assert.Equal(10, restored.MaxValue);
        Assert.Equal(7, restored.DefaultValue);
        var barColor = restored.BarColor!.Value;
        Assert.Equal(0.051, barColor.R, 3);
        Assert.Equal(0.184, barColor.G, 3);
        Assert.Equal(0.427, barColor.B, 3);
        Assert.Equal(1, barColor.A, 3);
        Assert.Equal(@"Interface\TargetingFrame\UI-StatusBar", restored.BarTexture);
        Assert.Equal(0.7, restored.DefaultFraction!.Value, 9);
    }

    /// <summary>
    /// The focused render assertion: the fill rectangle the canvas paints - through the shared
    /// <see cref="StatusBarRendering"/> helper - stretches with the authored Preview value, drawn
    /// for real into a raster and read back as pixels.
    /// </summary>
    [Fact]
    public void Rendered_fill_width_tracks_the_preview_value_in_pixels()
    {
        var vm = NewViewModel();
        vm.AddDesignStatusBar();

        const int barWidth = 200;
        const int barHeight = 16;
        var box = new Rect(0, 0, barWidth, barHeight);

        var wide = RenderFillPixels(vm, "70");
        var narrow = RenderFillPixels(vm, "30");

        var expectedWide = (int)Math.Round(barWidth * 0.7);
        var expectedNarrow = (int)Math.Round(barWidth * 0.3);

        Assert.InRange(wide, expectedWide - 2, expectedWide + 2);
        Assert.InRange(narrow, expectedNarrow - 2, expectedNarrow + 2);
        Assert.True(wide > narrow, "A bigger Preview value must paint a wider fill.");

        static int RenderFillPixels(MainWindowViewModel vm, string preview)
        {
            vm.StatusBarValueDraft = preview;
            Assert.True(vm.CommitStatusBarField("statusBarValue"));
            var restored = vm.SelectedFrame!.Visual!.StatusBar!;
            var fraction = restored.DefaultFraction!.Value;

            var fillRect = StatusBarRendering.FillRect(new Rect(0, 0, barWidth, barHeight), fraction);

            using var bitmap = new SKBitmap(barWidth, barHeight);
            using var canvas = new SKCanvas(bitmap);
            using var track = new SKPaint { Color = new SKColor(0x1B, 0x24, 0x2B), IsAntialias = false };
            using var fill = new SKPaint { Color = new SKColor(0x7F, 0xB2, 0xC9), IsAntialias = false };
            canvas.DrawRect(SKRect.Create(0, 0, barWidth, barHeight), track);
            canvas.DrawRect(SKRect.Create((float)fillRect.X, (float)fillRect.Y,
                (float)fillRect.Width, (float)fillRect.Height), fill);

            var columns = 0;
            for (var x = 0; x < barWidth; x++)
            {
                for (var y = 0; y < barHeight; y++)
                {
                    if (bitmap.GetPixel(x, y) == new SKColor(0x7F, 0xB2, 0xC9))
                    {
                        columns++;
                        break;
                    }
                }
            }

            return columns;
        }
    }

    private static MainWindowViewModel NewViewModel()
    {
        var vm = new MainWindowViewModel(
            Path.Combine(Path.GetTempPath(), $"frameforge-statusbar-{Guid.NewGuid():N}.json"),
            stockTemplates: new PassThroughStockTemplates());
        vm.Load(ProjectFactory.Empty(), null, "Loaded.");
        return vm;
    }

    private sealed class PassThroughStockTemplates : IStockTemplateResolver
    {
        public int Generation => 1;
        public IReadOnlyList<StockDefinitionDiagnostic> Diagnostics => [];
        public IReadOnlyList<AssetMaterializationResult> MaterializeRequired(WowClientValidation client) => [];
        public void Reload() { }
        public Project ApplyEffectiveGeometry(Project declaredProject) => declaredProject;
        public StockFontStyle? ResolveFont(string? name) => null;
        public StockButtonStyle? ResolveButton(string? name) => null;
        public StockExternalFrameStyle? ResolveExternalFrame(string? name) => null;
        public IReadOnlyList<string> Describe(FrameDef frame) => [];
        public IReadOnlyList<string> ExpandedChildNames(string instanceName, StockButtonStyle style) => [];
        public double MeasureText(StockFontStyle style, string text) => text.Length * 6;
    }
}