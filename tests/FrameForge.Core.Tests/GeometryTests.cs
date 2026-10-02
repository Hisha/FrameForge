using FrameForge.Core.Examples;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using static FrameForge.Core.Tests.TestProject;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// The release-critical suite: absolute geometry resolution for every anchor point, every
/// offset sign, parent/child and sibling anchoring, and the robustness cases.
/// </summary>
/// <remarks>
/// Two bugs from the Electron v0.1 implementation are pinned here on purpose:
/// the anchor sign error (<see cref="ResolveFrameRect_UsesCorrectSignForBottomAnchors"/>)
/// and the broken rectangle union (<see cref="UnionCoversBothRectsangles"/>).
/// </remarks>
public class GeometryTests
{
    [Fact]
    public void ScreenRectIsCentredOnTheModelOriginWithPositiveYUp()
    {
        Assert.Equal(new FrameRect(-512, 384, 512, -384), LayoutResolver.ScreenRect(new Screen(1024, 768)));
    }

    [Theory]
    // A 300x150 rectangle whose left/top corner is (-100, 200).
    [InlineData(AnchorPoint.TOPLEFT, -100, 200)]
    [InlineData(AnchorPoint.TOP, 50, 200)]
    [InlineData(AnchorPoint.TOPRIGHT, 200, 200)]
    [InlineData(AnchorPoint.LEFT, -100, 125)]
    [InlineData(AnchorPoint.CENTER, 50, 125)]
    [InlineData(AnchorPoint.RIGHT, 200, 125)]
    [InlineData(AnchorPoint.BOTTOMLEFT, -100, 50)]
    [InlineData(AnchorPoint.BOTTOM, 50, 50)]
    [InlineData(AnchorPoint.BOTTOMRIGHT, 200, 50)]
    public void AnchorPositionResolvesEveryAnchorPoint(AnchorPoint point, double x, double y)
    {
        var rect = FrameRect.FromSize(-100, 200, 300, 150);
        var position = LayoutResolver.AnchorPosition(rect, point);

        Assert.Equal(x, position.X, 10);
        Assert.Equal(y, position.Y, 10);
    }

    [Fact]
    public void CenterToCenterWithZeroOffsetKeepsTheFrameCentred()
    {
        Assert.Equal(new FrameRect(-100, 50, 100, -50), Anchored(AnchorPoint.CENTER, AnchorPoint.CENTER, 0, 0, 200, 100));
    }

    [Fact]
    public void TopToTopWithZeroOffsetPutsTheFrameTopEdgeOnTheScreenTop()
    {
        Assert.Equal(new FrameRect(-100, 384, 100, 284), Anchored(AnchorPoint.TOP, AnchorPoint.TOP, 0, 0, 200, 100));
    }

    [Fact]
    public void BottomToBottomWithZeroOffsetPutsTheFrameBottomEdgeOnTheScreenBottom()
    {
        Assert.Equal(new FrameRect(-100, -284, 100, -384), Anchored(AnchorPoint.BOTTOM, AnchorPoint.BOTTOM, 0, 0, 200, 100));
    }

    [Fact]
    public void TopLeftToTopLeftWithZeroOffsetSitsInTheScreenCorner()
    {
        Assert.Equal(new FrameRect(-512, 384, -312, 284), Anchored(AnchorPoint.TOPLEFT, AnchorPoint.TOPLEFT, 0, 0, 200, 100));
    }

    [Fact]
    public void TopRightToTopRightWithZeroOffsetSitsOppositeTheCorner()
    {
        Assert.Equal(new FrameRect(312, 384, 512, 284), Anchored(AnchorPoint.TOPRIGHT, AnchorPoint.TOPRIGHT, 0, 0, 200, 100));
    }

    [Fact]
    public void BottomLeftToBottomLeftWithZeroOffsetSitsBelowTheScreenCorner()
    {
        Assert.Equal(new FrameRect(-512, -284, -312, -384), Anchored(AnchorPoint.BOTTOMLEFT, AnchorPoint.BOTTOMLEFT, 0, 0, 200, 100));
    }

    [Fact]
    public void BottomRightToBottomRightWithZeroOffsetSitsInTheOppositeScreenCorner()
    {
        Assert.Equal(new FrameRect(312, -284, 512, -384), Anchored(AnchorPoint.BOTTOMRIGHT, AnchorPoint.BOTTOMRIGHT, 0, 0, 200, 100));
    }

    [Fact]
    public void LeftToLeftWithZeroOffsetPutsTheFrameLeftEdgeOnTheScreenLeftEdge()
    {
        Assert.Equal(new FrameRect(-512, 50, -412, -50), Anchored(AnchorPoint.LEFT, AnchorPoint.LEFT, 0, 0, 100, 100));
    }

    [Fact]
    public void RightToRightWithZeroOffsetPutsTheFrameRightEdgeOnTheScreenRightEdge()
    {
        Assert.Equal(new FrameRect(412, 50, 512, -50), Anchored(AnchorPoint.RIGHT, AnchorPoint.RIGHT, 0, 0, 100, 100));
    }

    [Fact]
    public void CenterToTopRightPlacesTheFrameCentreOnTheScreenCorner()
    {
        Assert.Equal(new FrameRect(412, 434, 612, 334), Anchored(AnchorPoint.CENTER, AnchorPoint.TOPRIGHT, 0, 0, 200, 100));
    }

    /// <summary>
    /// Pins the sign convention in <see cref="LayoutResolver.ResolveFrameRect"/>.
    /// </summary>
    /// <remarks>
    /// The Electron v0.1 implementation used <c>anchorY - (1 - unitY) * height</c>. With model
    /// +Y up that subtracted the height a second time for a bottom-anchored frame, so a
    /// BOTTOM-&gt;BOTTOM frame at the screen bottom came out 100 units too high and 200 tall
    /// instead of 100. The correct term is <c>anchorY + (1 - unitY) * height</c>.
    /// </remarks>
    [Fact]
    public void ResolveFrameRect_UsesCorrectSignForBottomAnchors()
    {
        var frame = Frame("F", point: AnchorPoint.BOTTOM, relativePoint: AnchorPoint.BOTTOM);
        var reference = FrameRect.FromEdges(-512, 384, 512, -384);

        var rect = LayoutResolver.ResolveFrameRect(frame, reference, null);

        // The screen's bottom edge sits at model y = -384, so the frame hangs below it.
        Assert.Equal(-384 + 100, rect.Top, 10);
        Assert.Equal(-384, rect.Bottom, 10);
        Assert.Equal(100, rect.Height, 10);

        // Under the old `anchorY - (1 - unitY) * height` sign this produced top = -484 and a
        // bottom edge of -384 - 200: 100 units too high and twice as tall.
        Assert.NotEqual(-484, rect.Top, 10);
        Assert.NotEqual(-584, rect.Bottom, 10);
    }

    [Fact]
    public void ApplyAPositiveXOffsetMovesTheFrameRight()
    {
        var rect = Anchored(AnchorPoint.TOPLEFT, AnchorPoint.TOPLEFT, 25, 0);

        Assert.Equal(-487, rect.Left, 10);
        Assert.Equal(-387, rect.Right, 10);
    }

    [Fact]
    public void ApplyANegativeXOffsetMovesTheFrameLeft()
    {
        Assert.Equal(-537, Anchored(AnchorPoint.TOPLEFT, AnchorPoint.TOPLEFT, -25, 0).Left, 10);
    }

    [Fact]
    public void ApplyAPositiveYOffsetMovesTheFrameUp()
    {
        var rect = Anchored(AnchorPoint.TOPLEFT, AnchorPoint.TOPLEFT, 0, 25);

        Assert.Equal(409, rect.Top, 10);
        Assert.Equal(309, rect.Bottom, 10);
    }

    [Fact]
    public void ApplyANegativeYOffsetMovesTheFrameDown()
    {
        var rect = Anchored(AnchorPoint.TOPLEFT, AnchorPoint.TOPLEFT, 0, -25);

        Assert.Equal(359, rect.Top, 10);
        Assert.Equal(259, rect.Bottom, 10);
    }

    [Fact]
    public void OffsetsAreRelativeToTheAnchorNotTheScreenOrigin()
    {
        // TOP -> BOTTOM of the screen with (0, 10): the frame hangs 10 units above it.
        var rect = Anchored(AnchorPoint.TOP, AnchorPoint.BOTTOM, 0, 10, 200, 100);

        Assert.Equal(-374, rect.Top, 10);
        Assert.Equal(-474, rect.Bottom, 10);
    }

    [Fact]
    public void AChildInheritsTheParentWhenRelativeToIsNull()
    {
        var result = LayoutResolver.Resolve(Project(
            ParentFrame(),
            Frame("Child", parent: "Parent", width: 296, height: 406, point: AnchorPoint.TOP,
                relativePoint: AnchorPoint.TOP, offsetX: 12, offsetY: -44)));

        Assert.Equal(new FrameRect(-512, 384, -157, -116), result.Rects["Parent"]);
        var child = result.Rects["Child"];
        // Parent.TOP is (-334.5, 384); offset (12, -44) moves the child's top-centre there.
        Assert.Equal(340, child.Top, 10);
        Assert.Equal(-334.5 + 12 - 148, child.Left, 10);
        Assert.Equal(-334.5 + 12 + 148, child.Right, 10);
        Assert.Equal(340 - 406, child.Bottom, 10);
    }

    [Fact]
    public void AChildFollowsTheParentWhenTheParentMoves()
    {
        var result = LayoutResolver.Resolve(Project(
            ParentFrame(offsetX: 100),
            Frame("Child", parent: "Parent")));

        Assert.Equal(result.Rects["Parent"].Left, result.Rects["Child"].Left, 10);
        Assert.Equal(result.Rects["Parent"].Top, result.Rects["Child"].Top, 10);
    }

    [Fact]
    public void AChildFollowsTheParentWhenTheParentIsResized()
    {
        var result = LayoutResolver.Resolve(Project(
            ParentFrame(),
            Frame("Child", parent: "Parent", point: AnchorPoint.BOTTOMRIGHT, relativePoint: AnchorPoint.BOTTOMRIGHT)));

        Assert.Equal(result.Rects["Parent"].Right, result.Rects["Child"].Right, 10);
        Assert.Equal(result.Rects["Parent"].Bottom, result.Rects["Child"].Bottom, 10);
    }

    [Fact]
    public void AFrameCanBeAnchoredToAFrameThatIsNotItsParent()
    {
        var result = LayoutResolver.Resolve(Project(
            ParentFrame(),
            Frame("Other", parent: "Parent", width: 100, height: 40, point: AnchorPoint.BOTTOM,
                relativePoint: AnchorPoint.BOTTOM, offsetY: 10),
            Frame("Child", parent: "Parent", width: 50, height: 50, point: AnchorPoint.TOP,
                relativeTo: "Other", relativePoint: AnchorPoint.TOP)));

        Assert.Equal(result.Rects["Other"].Top, result.Rects["Child"].Top, 10);
        Assert.Equal(result.Rects["Other"].Left + 50, result.Rects["Child"].Left + 25, 10);
    }

    [Fact]
    public void ResolvesAChainOfDescendantsInOrder()
    {
        var result = LayoutResolver.Resolve(Project(
            Frame("A", width: 400, height: 400),
            Frame("B", parent: "A", width: 200, height: 200, point: AnchorPoint.TOP,
                relativePoint: AnchorPoint.TOP, offsetY: -20),
            Frame("C", parent: "B", width: 100, height: 100, point: AnchorPoint.LEFT,
                relativePoint: AnchorPoint.LEFT, offsetX: 10)));

        var a = result.Rects["A"];
        var b = result.Rects["B"];
        var c = result.Rects["C"];
        Assert.Equal(a.Top - 20, b.Top, 10);
        // B.LEFT is B's left edge at B's vertical centre, so C's centre lands there.
        Assert.Equal(b.Left + 10, c.Left, 10);
        Assert.Equal(b.Top - 50, c.Top, 10);
    }

    [Fact]
    public void SizeReferenceParentTreatsWidthAndHeightAsFractionsOfTheParent()
    {
        var result = LayoutResolver.Resolve(Project(
            Frame("Parent", width: 400, height: 200),
            Frame("Child", parent: "Parent", width: 0.5, height: 0.25, sizeReference: SizeReference.PARENT)));

        Assert.Equal(200, result.Rects["Child"].Width, 10);
        Assert.Equal(50, result.Rects["Child"].Height, 10);
    }

    [Fact]
    public void AFrameIsEffectivelyHiddenWhenAnAncestorIsHidden()
    {
        var result = LayoutResolver.Resolve(Project(
            Frame("Parent", visible: false),
            Frame("Child", parent: "Parent", visible: true)));

        Assert.False(result.Frames["Parent"].EffectiveVisible);
        Assert.True(result.Frames["Child"].OwnVisible);
        Assert.False(result.Frames["Child"].EffectiveVisible);
    }

    [Fact]
    public void GeometryIsStillResolvedForHiddenFrames()
    {
        var result = LayoutResolver.Resolve(Project(Frame("F", visible: false)));

        Assert.True(result.Rects.ContainsKey("F"));
    }

    [Fact]
    public void ReportsAnAnchorCycleInsteadOfHanging()
    {
        var result = LayoutResolver.Resolve(Project(
            Frame("A", parent: "B"),
            Frame("B", parent: "A")));

        Assert.Empty(result.Rects);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void ReportsACycleFormedByRelativeToAlone()
    {
        // parent is structurally valid, so only the anchor relationship is cyclic.
        var result = LayoutResolver.Resolve(Project(
            Frame("A", parent: "B", relativeTo: "B"),
            Frame("B", parent: "A", relativeTo: "A")));

        Assert.Empty(result.Rects);
        Assert.Contains(result.Issues, i => i.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsADanglingAnchorReference()
    {
        var result = LayoutResolver.Resolve(Project(Frame("F", relativeTo: "Nope")));

        Assert.False(result.Rects.ContainsKey("F"));
        Assert.Contains(result.Issues, i => i.Frame == "F" && i.Message.Contains("unknown frame", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFrameWithNoAnchorTargetSitsAtTheScreenTopLeft()
    {
        var rect = LayoutResolver.Resolve(
            Project(Frame("F", width: 200, height: 100, offsetX: 10, offsetY: -5))).Rects["F"];

        Assert.Equal(-502, rect.Left, 10);
        Assert.Equal(379, rect.Top, 10);
    }

    [Fact]
    public void ResolutionIsPureAndDeterministic()
    {
        var project = Project(
            ParentFrame(),
            Frame("Child", parent: "Parent", point: AnchorPoint.TOP, relativePoint: AnchorPoint.TOP, offsetY: -10));

        var first = LayoutResolver.Resolve(project);
        var second = LayoutResolver.Resolve(project);

        Assert.Equal(first.Rects, second.Rects);
        Assert.Equal(first.PaintOrder, second.PaintOrder);
        Assert.Equal(first.Bounds, second.Bounds);
    }

    [Fact]
    public void ResolutionDoesNotMutateTheProject()
    {
        var project = Project(ParentFrame(), Frame("Child", parent: "Parent"));
        var before = FrameForge.Core.Serialization.ProjectCodec.Serialize(project);

        LayoutResolver.Resolve(project);

        Assert.Equal(before, FrameForge.Core.Serialization.ProjectCodec.Serialize(project));
    }

    [Fact]
    public void AZeroSizeFrameDoesNotCollapseOrInvert()
    {
        var rect = LayoutResolver.ResolveFrameRect(
            Frame("F", width: 0, height: 0), FrameRect.FromEdges(0, 0, 10, -10), null);

        Assert.Equal(new FrameRect(0, 0, 0, 0), rect);
    }

    /// <summary>
    /// Pins the union bug from the Electron v0.1 implementation.
    /// </summary>
    /// <remarks>
    /// The original <c>union(a, b)</c> paired <c>a.left</c> with <c>b.right</c> and
    /// <c>a.top</c> with <c>b.bottom</c>, which SHRINKS the result whenever one rectangle
    /// contains the other. That silently broke "fit layout to window" for every realistic
    /// frame tree, where children are nested inside parents.
    /// </remarks>
    [Fact]
    public void UnionCoversBothRectsangles()
    {
        var big = FrameRect.FromSize(0, 400, 1000, 800);
        var small = FrameRect.FromSize(100, 100, 50, 50);

        Assert.Equal(big, big.Union(small));
        Assert.Equal(big, small.Union(big));
        Assert.Equal(
            FrameRect.FromEdges(0, 100, 110, -10),
            FrameRect.FromSize(0, 0, 10, 10).Union(FrameRect.FromSize(100, 100, 10, 10)));
    }

    [Fact]
    public void LayoutBoundsIsTheUnionOfEveryResolvedFrame()
    {
        var result = LayoutResolver.Resolve(NativeHuntsExample.CreateProject());

        // MainWindow is the largest frame, so the union must equal it exactly.
        Assert.Equal(result.Rects["MainWindow"], result.Bounds);
    }

    [Fact]
    public void LayoutBoundsIsNullWhenNothingResolves()
    {
        Assert.Null(LayoutResolver.Resolve(ProjectFactory.Create("empty", [])).Bounds);
    }
}