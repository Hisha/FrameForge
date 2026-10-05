using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using Xunit;
using static FrameForge.Core.Tests.TestProject;

namespace FrameForge.Core.Tests;

/// <summary>
/// Multi-selection alignment, equal-gap distribution, and rigid translation.
/// </summary>
/// <remarks>
/// The behaviour under test is as much about what the arranger refuses to do as about what it
/// moves. Every assertion here that a frame kept its <c>Point</c>, <c>RelativePoint</c>, parent,
/// size, kind, and visual exists because a silent rewrite of any of those turns imported Blizzard
/// markup into markup that no longer matches the running game.
/// </remarks>
public class SelectionArrangeTests
{
    private static readonly string[] Row = ["A", "B", "C", "D"];

    /// <summary>The container every row hangs from, so offsets are readable as model numbers.</summary>
    private const double RowLeft = -500;
    private const double RowTop = 500;

    /// <summary>
    /// Four 100x100 objects inside a 1000x1000 container centred on the screen, at x = -500, -200,
    /// 0, 300. The gaps are deliberately uneven (200, 100, 200) so "equal gaps" has one answer.
    /// </summary>
    private static Project UnevenRow(double topOffset = 0) => Project(
        Frame("Root", width: 1000, height: 1000, point: AnchorPoint.CENTER, relativePoint: AnchorPoint.CENTER),
        Frame("A", parent: "Root", width: 100, height: 100, offsetY: topOffset),
        Frame("B", parent: "Root", width: 100, height: 100, offsetX: 300, offsetY: topOffset),
        Frame("C", parent: "Root", width: 100, height: 100, offsetX: 500, offsetY: topOffset),
        Frame("D", parent: "Root", width: 100, height: 100, offsetX: 800, offsetY: topOffset));

    [Fact]
    public void AlignLeftPutsEveryLeftEdgeOnTheSelectionLeftEdge()
    {
        var project = UnevenRow();
        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), Row, SelectionArrangeCommand.AlignLeft);

        Assert.True(outcome.Changed);
        var after = LayoutResolver.Resolve(outcome.Project);
        Assert.All(Row, name => Assert.Equal(RowLeft, after.Rects[name].Left, 6));
        Assert.Equal(RowLeft, SelectionArrange.Bounds(after, Row)!.Value.Left, 6);
    }

    [Fact]
    public void AlignRightLinesUpDifferentWidthsWithoutResizingThem()
    {
        var project = Project(
            Frame("A", width: 100, height: 50, offsetX: 0),
            Frame("B", width: 300, height: 20, offsetX: 50));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B"], SelectionArrangeCommand.AlignRight);

        var rects = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(-162, rects["A"].Right, 6);
        Assert.Equal(-162, rects["B"].Right, 6);
        Assert.Equal(100, outcome.Project.Find("A")!.Width, 6);
        Assert.Equal(300, outcome.Project.Find("B")!.Width, 6);
        Assert.Equal(20, outcome.Project.Find("B")!.Height, 6);
    }

    [Fact]
    public void CenterAlignmentUsesBoundingBoxesNotOrigins()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("B", width: 300, height: 100, offsetX: 400));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B"], SelectionArrangeCommand.AlignCenterHorizontal);

        // The union of [-512, -412] and [-112, 188] is centred on -162, which is NOT the average of
        // the two individual centres (-212): aligning to the union is what makes the group line up
        // as one shape rather than two shapes that happen to share a middle.
        var rects = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(-162, rects["A"].CenterX, 6);
        Assert.Equal(-162, rects["B"].CenterX, 6);
    }

    [Fact]
    public void DistributionMakesEveryGapEqualAndLeavesTheEndpointsAlone()
    {
        var project = UnevenRow();
        var before = LayoutResolver.Resolve(project).Rects;

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), Row, SelectionArrangeCommand.DistributeHorizontal);

        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        var first = after["B"].Left - after["A"].Right;
        var second = after["C"].Left - after["B"].Right;
        Assert.Equal(500.0 / 3, first, 6);
        Assert.Equal(first, second, 6);
        Assert.Equal(before["A"].Left, after["A"].Left, 6);
        Assert.Equal(before["D"].Right, after["D"].Right, 6);
        Assert.Equal(before["D"].Left, after["D"].Left, 6);
    }

    [Fact]
    public void DistributionKeepsUnevenWidthsAndSpacesBetweenBoxesNotCentres()
    {
        var project = Project(
            Frame("A", width: 40, height: 100, offsetX: 0),
            Frame("B", width: 200, height: 100, offsetX: 100),
            Frame("C", width: 60, height: 100, offsetX: 500));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B", "C"], SelectionArrangeCommand.DistributeHorizontal);

        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(40, after["A"].Width, 6);
        Assert.Equal(200, after["B"].Width, 6);
        Assert.Equal(60, after["C"].Width, 6);
        var gap = after["B"].Left - after["A"].Right;
        Assert.Equal(130, gap, 6);
        Assert.Equal(gap, after["C"].Left - after["B"].Right, 6);
    }

    [Fact]
    public void VerticalDistributionRunsTopToBottomInModelSpace()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0, offsetY: 500),
            Frame("B", width: 100, height: 100, offsetX: 0, offsetY: 300),
            Frame("C", width: 100, height: 100, offsetX: 0, offsetY: 0));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B", "C"], SelectionArrangeCommand.DistributeVertical);

        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        var upper = after["A"].Bottom - after["B"].Top;
        var lower = after["B"].Bottom - after["C"].Top;
        Assert.Equal(150, upper, 6);
        Assert.Equal(upper, lower, 6);
        Assert.Equal(884, after["A"].Top, 6);
        Assert.Equal(384, after["C"].Top, 6);
    }

    [Fact]
    public void AlignRefusesWithFewerThanTwoMovableObjectsAndSaysWhy()
    {
        var project = UnevenRow();
        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A"], SelectionArrangeCommand.AlignLeft);

        Assert.False(outcome.Changed);
        Assert.Same(project, outcome.Project);
        Assert.Contains(outcome.Blocked, b => b.Reason.Contains("at least 2"));
        Assert.Contains("0 objects moved", outcome.Message);
    }

    [Fact]
    public void DistributeRefusesWithFewerThanThreeMovableObjects()
    {
        var project = UnevenRow();
        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B"], SelectionArrangeCommand.DistributeHorizontal);

        Assert.False(outcome.Changed);
        Assert.Contains(outcome.Blocked, b => b.Reason.Contains("at least 3"));
    }

    [Fact]
    public void LockedFramesAreExcludedFromAlignmentAndReported()
    {
        var project = UnevenRow() with
        {
            Editor = new EditorMetadata { LockedElements = ["C"] },
        };

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), Row, SelectionArrangeCommand.AlignLeft);

        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(0, after["C"].Left, 6);
        Assert.Equal(500, outcome.Project.Find("C")!.OffsetX, 6);
        Assert.Equal(RowLeft, after["A"].Left, 6);
        Assert.Equal(["C"], outcome.ExcludedLocked);
        Assert.Contains("locked", outcome.Message);
    }

    [Fact]
    public void ALockedFrameStillCountsTowardsTheSelectionBounds()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("B", width: 100, height: 100, offsetX: 300),
            Frame("Locked", width: 100, height: 100, offsetX: 600)) with
        {
            Editor = new EditorMetadata { LockedElements = ["Locked"] },
        };

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B", "Locked"], SelectionArrangeCommand.AlignRight);

        // Locked's right edge (188) is the right edge of the selection, so the editable objects move
        // onto it. Ignoring the locked extent would align them to -112 instead, which is a
        // different and equally plausible-looking answer the user did not ask for.
        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(88, after["A"].Left, 6);
        Assert.Equal(88, after["B"].Left, 6);
        Assert.Equal(600, outcome.Project.Find("Locked")!.OffsetX, 6);
        Assert.Equal(["Locked"], outcome.ExcludedLocked);
    }

    [Fact]
    public void OneMovableObjectIsNotEnoughToAlign()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Locked", width: 100, height: 100, offsetX: 600)) with
        {
            Editor = new EditorMetadata { LockedElements = ["Locked"] },
        };

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "Locked"], SelectionArrangeCommand.AlignRight);

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.Project.Find("A")!.OffsetX, 6);
        Assert.Contains(outcome.Blocked, b => b.Reason.Contains("at least 2"));
    }

    [Fact]
    public void SetAllPointsIsBlockedRatherThanSilentlyUnmovable()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Fill", parent: "A", width: 0, height: 0, setAllPoints: true));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "Fill"], SelectionArrangeCommand.AlignLeft);

        Assert.Contains(outcome.Blocked, b => b.Name == "Fill" && b.Reason.Contains("SetAllPoints"));
        var fill = outcome.Project.Find("Fill")!;
        Assert.True(fill.SetAllPoints);
        Assert.Equal(0, fill.OffsetX, 6);
        Assert.Equal(0, fill.OffsetY, 6);
        Assert.Equal(-512, LayoutResolver.Resolve(outcome.Project).Rects["A"].Left, 6);
    }

    [Fact]
    public void AMultiAnchorStretchFrameIsBlockedBecauseItsSizeIsDerived()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Stretch", width: 0, height: 100, offsetX: 400, extraAnchors:
            [
                new FrameAnchor { Point = AnchorPoint.TOPRIGHT, RelativePoint = AnchorPoint.TOPRIGHT },
            ]));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "Stretch"], SelectionArrangeCommand.AlignLeft);

        Assert.Contains(outcome.Blocked, b => b.Name == "Stretch" && b.Reason.Contains("more than one anchor"));
        Assert.Equal(-112, LayoutResolver.Resolve(outcome.Project).Rects["Stretch"].Left, 6);
    }

    [Fact]
    public void AFrameWhoseGeometryCannotBeResolvedIsBlocked()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Dangling", width: 100, height: 100, relativeTo: "Missing"));

        var layout = LayoutResolver.Resolve(project);
        Assert.False(layout.Rects.ContainsKey("Dangling"));

        var outcome = SelectionArrange.Arrange(
            project, layout, ["A", "Dangling"], SelectionArrangeCommand.AlignLeft);

        Assert.Contains(outcome.Blocked, b => b.Name == "Dangling" && b.Reason.Contains("could not be resolved"));
    }

    [Fact]
    public void ArrangingLeavesEveryUnselectedFrameExactlyWhereItWas()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("B", width: 100, height: 100, offsetX: 300),
            Frame("Untouched", width: 220, height: 90, offsetX: 900, offsetY: -40));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B"], SelectionArrangeCommand.AlignRight);

        var before = LayoutResolver.Resolve(project).Rects["Untouched"];
        var after = LayoutResolver.Resolve(outcome.Project).Rects["Untouched"];
        Assert.Equal(before, after);
        Assert.Equal(900, outcome.Project.Find("Untouched")!.OffsetX, 6);
        Assert.Equal(-40, outcome.Project.Find("Untouched")!.OffsetY, 6);
    }

    [Fact]
    public void ArrangingPreservesAnchorRelationshipAndEverythingThatDescribesTheFrame()
    {
        var project = Project(
            Frame("Parent", width: 400, height: 300, point: AnchorPoint.CENTER,
                relativePoint: AnchorPoint.CENTER, offsetX: -200, offsetY: 0),
            Frame("A", parent: "Parent", width: 100, height: 100, point: AnchorPoint.CENTER,
                relativePoint: AnchorPoint.CENTER, offsetX: -150, offsetY: 20),
            Frame("B", parent: "Parent", width: 100, height: 100, point: AnchorPoint.BOTTOMLEFT,
                relativePoint: AnchorPoint.BOTTOMRIGHT, offsetX: 60, offsetY: -80));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B"], SelectionArrangeCommand.AlignTop);

        var a = outcome.Project.Find("A")!;
        var b = outcome.Project.Find("B")!;
        Assert.Equal(AnchorPoint.CENTER, a.Point);
        Assert.Equal(AnchorPoint.CENTER, a.RelativePoint);
        Assert.Equal("Parent", a.Parent);
        Assert.Equal(100, a.Width, 6);
        Assert.Equal(AnchorPoint.BOTTOMLEFT, b.Point);
        Assert.Equal(AnchorPoint.BOTTOMRIGHT, b.RelativePoint);
        Assert.Equal("Parent", b.Parent);
        Assert.Empty(b.ExtraAnchors);

        var after = LayoutResolver.Resolve(outcome.Project);
        Assert.Equal(70, after.Rects["A"].Top, 6);
        Assert.Equal(after.Rects["A"].Top, after.Rects["B"].Top, 6);
        Assert.All(after.Frames["A"].Anchors, anchor => Assert.True(anchor.Resolved));
        Assert.All(after.Frames["B"].Anchors, anchor => Assert.True(anchor.Resolved));
    }

    [Fact]
    public void ArrangingAContainerAndItsChildLandsBothWhereTheUserAsked()
    {
        // Inner is anchored to Box, so moving Box moves Inner as well. A naive implementation shifts
        // the child once for itself and once for its parent and overshoots by the parent's delta.
        var project = Project(
            Frame("Box", width: 400, height: 400, offsetX: 0),
            Frame("Inner", parent: "Box", width: 100, height: 100, offsetX: -500, offsetY: -20));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["Box", "Inner"], SelectionArrangeCommand.AlignLeft);

        Assert.Contains("Box", outcome.Moved);
        Assert.Contains("Inner", outcome.Moved);
        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(-1012, after["Box"].Left, 6);
        Assert.Equal(-1012, after["Inner"].Left, 6);
        Assert.Equal(100, outcome.Project.Find("Inner")!.Width, 6);
    }

    [Fact]
    public void MovingTranslatesTheWholeSelectionByOneDelta()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0, offsetY: 0),
            Frame("B", width: 100, height: 100, offsetX: 300, offsetY: 0));

        var outcome = SelectionArrange.Move(project, LayoutResolver.Resolve(project), ["A", "B"], 25, -40);

        Assert.Equal(2, outcome.Moved.Count);
        Assert.Equal(25, outcome.Project.Find("A")!.OffsetX, 6);
        Assert.Equal(-40, outcome.Project.Find("A")!.OffsetY, 6);
        Assert.Equal(325, outcome.Project.Find("B")!.OffsetX, 6);
        Assert.Equal(-40, outcome.Project.Find("B")!.OffsetY, 6);
    }

    [Fact]
    public void MovingAContainerAndItsChildDoesNotShiftTheChildTwice()
    {
        var project = Project(
            Frame("Box", width: 400, height: 400, offsetX: 0),
            Frame("Inner", parent: "Box", width: 100, height: 100, offsetX: 20));

        var before = LayoutResolver.Resolve(project).Rects;
        var outcome = SelectionArrange.Move(project, LayoutResolver.Resolve(project), ["Box", "Inner"], 100, 0);

        Assert.Equal(["Box"], outcome.Moved);
        Assert.Equal(20, outcome.Project.Find("Inner")!.OffsetX, 6);
        Assert.Equal(before["Inner"].Left + 100, LayoutResolver.Resolve(outcome.Project).Rects["Inner"].Left, 6);
    }

    [Fact]
    public void MovingLeavesFramesThatWereNotSelectedAlone()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Other", width: 100, height: 100, offsetX: 0));

        var outcome = SelectionArrange.Move(project, LayoutResolver.Resolve(project), ["A"], 50, 50);

        Assert.Equal(["A"], outcome.Moved);
        Assert.Equal(0, outcome.Project.Find("Other")!.OffsetX, 6);
        Assert.Equal(0, outcome.Project.Find("Other")!.OffsetY, 6);
    }

    [Fact]
    public void MovingSkipsLockedFramesAndSaysWhich()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Locked", width: 100, height: 100, offsetX: 200)) with
        {
            Editor = new EditorMetadata { LockedElements = ["Locked"] },
        };

        var outcome = SelectionArrange.Move(project, LayoutResolver.Resolve(project), ["A", "Locked"], 50, 0);

        Assert.Equal(["A"], outcome.Moved);
        Assert.Equal(["Locked"], outcome.ExcludedLocked);
        Assert.Equal(200, outcome.Project.Find("Locked")!.OffsetX, 6);
        Assert.Contains("locked", outcome.Message);
    }

    [Fact]
    public void MovingWithNothingToDoReturnsTheOriginalProjectInstance()
    {
        var project = UnevenRow();
        var outcome = SelectionArrange.Move(project, LayoutResolver.Resolve(project), ["A"], 0, 0);

        Assert.False(outcome.Changed);
        Assert.Same(project, outcome.Project);
    }

    [Fact]
    public void SelectionBoundsIgnoreNamesThatCannotBeResolved()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Dangling", width: 100, height: 100, relativeTo: "Nope"));

        var bounds = SelectionArrange.Bounds(LayoutResolver.Resolve(project), ["A", "Dangling", "Missing"]);

        Assert.NotNull(bounds);
        Assert.Equal(-512, bounds!.Value.Left, 6);
        Assert.Equal(-412, bounds!.Value.Right, 6);
    }

    [Theory]
    [InlineData(SelectionArrangeCommand.AlignLeft, 2)]
    [InlineData(SelectionArrangeCommand.AlignCenterVertical, 2)]
    [InlineData(SelectionArrangeCommand.DistributeHorizontal, 3)]
    [InlineData(SelectionArrangeCommand.DistributeVertical, 3)]
    public void AligningNeedsTwoMovableObjectsAndDistributingNeedsThree(
        SelectionArrangeCommand command,
        int required)
    {
        Assert.Equal(required, SelectionArrange.RequiredCount(command));
    }
}