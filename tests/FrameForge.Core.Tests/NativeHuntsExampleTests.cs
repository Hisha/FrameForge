using FrameForge.Core.Examples;
using FrameForge.Core.Geometry;
using FrameForge.Core.Serialization;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// Golden geometry for the Native Hunts example, derived by hand from
/// mod-native-hunts -&gt; Interface/FrameXML/NativeHuntsFrame.xml at a 1024x768 screen
/// (UIParent = x -512..512, y -384..384).
/// </summary>
/// <remarks>
/// <code>
///   MainWindow  355x500  TOPLEFT of screen
///   Content     296x406  TOP     -&gt; MainWindow.TOP    (12, -44)
///   Identity    280x88   TOP     -&gt; Content.TOP       (0, -26)
///   State       280x118  TOP     -&gt; Identity.BOTTOM   (0, -30)
///   Idle        280x212  TOP     -&gt; Identity.TOP      (0, 0)
///   Record      280x86   BOTTOM  -&gt; Content.BOTTOM    (0, 8)
/// </code>
/// </remarks>
public class NativeHuntsExampleTests
{
    private static readonly LayoutResult Layout = LayoutResolver.Resolve(NativeHuntsExample.CreateProject());

    [Theory]
    [InlineData("MainWindow", -512, 384, -157, -116)]
    [InlineData("Content", -470.5, 340, -174.5, -66)]
    [InlineData("Identity", -462.5, 314, -182.5, 226)]
    [InlineData("State", -462.5, 196, -182.5, 78)]
    [InlineData("Idle", -462.5, 314, -182.5, 102)]
    [InlineData("Record", -462.5, 28, -182.5, -58)]
    public void MatchesTheHandComputedBounds(string name, double left, double top, double right, double bottom)
    {
        var rect = Layout.Rects[name];

        Assert.Equal(left, rect.Left, 6);
        Assert.Equal(top, rect.Top, 6);
        Assert.Equal(right, rect.Right, 6);
        Assert.Equal(bottom, rect.Bottom, 6);
    }

    [Theory]
    [InlineData("Identity")]
    [InlineData("State")]
    [InlineData("Idle")]
    [InlineData("Record")]
    public void KeepsIdentityStateIdleAndRecordOnTheSameHorizontalBand(string name)
    {
        var rect = Layout.Rects[name];

        Assert.Equal(-462.5, rect.Left, 6);
        Assert.Equal(-182.5, rect.Right, 6);
    }

    /// <summary>
    /// The overlap is the point of the example and must not be "fixed": Idle shares
    /// Identity's TOP and is taller, so it completely covers Identity, while State hangs 30
    /// units below Identity's BOTTOM.
    /// </summary>
    [Fact]
    public void LeavesTheDeliberateIdentityStateIdleOverlapIntact()
    {
        var identity = Layout.Rects["Identity"];
        var state = Layout.Rects["State"];
        var idle = Layout.Rects["Idle"];

        Assert.Equal(identity.Top, idle.Top, 6);
        Assert.True(idle.Bottom < identity.Bottom, "Idle should extend below Identity");
        Assert.Equal(identity.Bottom - 30, state.Top, 6);
    }

    [Fact]
    public void PlacesRecordEightUnitsAboveTheBottomOfContent()
    {
        Assert.Equal(Layout.Rects["Content"].Bottom + 8, Layout.Rects["Record"].Bottom, 6);
    }

    [Fact]
    public void KeepsEveryFrameInsideMainWindow()
    {
        var main = Layout.Rects["MainWindow"];

        foreach (var (name, rect) in Layout.Rects)
        {
            if (name == "MainWindow")
                continue;

            Assert.True(main.Contains(rect), $"{name} ({rect}) escapes MainWindow ({main})");
        }
    }

    [Fact]
    public void PaintsParentsBeforeTheirChildren()
    {
        var order = Layout.PaintOrder.ToList();

        Assert.True(order.IndexOf("MainWindow") < order.IndexOf("Content"));
        Assert.True(order.IndexOf("Content") < order.IndexOf("Identity"));
        Assert.True(order.IndexOf("Identity") < order.IndexOf("Idle"));
    }

    [Fact]
    public void ResolvesWithoutAnyLayoutProblems()
    {
        Assert.Empty(Layout.Issues);
        Assert.Equal(6, Layout.Rects.Count);
    }

    [Fact]
    public void ShipsAsAnEmbeddedResourceAndMatchesTheBuiltInExample()
    {
        // examples/native-hunts.fforge.json is embedded in the test assembly so this
        // comparison cannot drift from a stale copy on disk.
        using var stream = typeof(NativeHuntsExampleTests).Assembly
            .GetManifestResourceStream("FrameForge.Core.Tests.examples.native-hunts.fforge.json");

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var onDisk = reader.ReadToEnd();

        Assert.Equal(ProjectCodec.Serialize(NativeHuntsExample.CreateProject()), onDisk);
        Assert.True(ProjectCodec.Parse(onDisk).Ok);
    }
}