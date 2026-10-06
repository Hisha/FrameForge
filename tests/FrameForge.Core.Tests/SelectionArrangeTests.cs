using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
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
    public void OneMovableObjectWithALockedReferenceIsEnoughToAlign()
    {
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0),
            Frame("Locked", width: 100, height: 100, offsetX: 600)) with
        {
            Editor = new EditorMetadata { LockedElements = ["Locked"] },
        };

        Assert.True(SelectionArrange.CanRun(
            project, LayoutResolver.Resolve(project), ["A", "Locked"], SelectionArrangeCommand.AlignRight));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "Locked"], SelectionArrangeCommand.AlignRight);

        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(188, after["A"].Right, 6);
        Assert.Equal(600, outcome.Project.Find("Locked")!.OffsetX, 6);
        Assert.Equal(["Locked"], outcome.ExcludedLocked);
        Assert.Empty(outcome.Blocked);
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
    public void AligningNeedsTwoSelectedObjectsAndDistributingNeedsThreeMovableOnes(
        SelectionArrangeCommand command,
        int required)
    {
        Assert.Equal(required, SelectionArrange.RequiredCount(command));
    }

    /// <summary>
    /// The reported bug: a locked Blizzard label and an editable custom label selected together
    /// must align, because the locked one is reference geometry and only the editable one moves.
    /// </summary>
    /// <remarks>
    /// A 200x100 locked stock label, and a 120x40 editable one placed strictly INSIDE the stock
    /// frame's box on both axes. That containment is deliberate. It means every selection-bounds
    /// edge is established by the locked frame, so each of the six alignment commands has exactly
    /// one right answer - the stock frame's own edge - and a command that quietly ignored the
    /// locked extent would land the editable frame somewhere visibly wrong. It also means none of
    /// the six is accidentally a no-op, which a fixture with the frames side by side would produce
    /// for whichever axis the editable frame already happened to define.
    /// <para>
    /// Screen space is +Y up with the origin at the centre, so a 1024x768 screen puts the top-left
    /// anchor at (-512, 384): <c>left = -512 + offsetX</c> and <c>top = 384 + offsetY</c>.
    /// </para>
    /// </remarks>
    private static Project LockedAndEditable() => Project(
        Frame("Stock", width: 200, height: 100, offsetX: -300, offsetY: 200),
        Frame("Custom", width: 120, height: 40, offsetX: -278, offsetY: 176)) with
    {
        Editor = new EditorMetadata { LockedElements = ["Stock"] },
    };

    /// <summary>
    /// The exact bytes a single frame contributes to the serialized document.
    /// </summary>
    /// <remarks>
    /// Serializing the frame on its own keeps the same writer settings, field order and number
    /// formatting as a full document, so comparing two of these is a byte-level check on that frame
    /// that is unaffected by its neighbours. Record equality on <see cref="FrameDef"/> would cover
    /// the same ground more cheaply, but "the locked frame is byte-identical in the saved file" is
    /// the claim actually worth making about imported markup.
    /// </remarks>
    private static string SerializedFrame(Project project, string name) =>
        ProjectCodec.Serialize(project with { Frames = [project.Frames.Single(f => f.Name == name)] });

    /// <summary>
    /// Every field of a frame that alignment must never touch, as one comparable value.
    /// </summary>
    private static object GeometryFingerprint(FrameDef frame) =>
        (frame.Name, frame.Parent, frame.Width, frame.Height, frame.Point, frame.RelativeTo,
            frame.RelativePoint, frame.SetAllPoints, frame.SizeReference, frame.Stratum, frame.Level,
            frame.Kind, frame.Visual, frame.ExtraAnchors, frame.Visible, frame.SourceName,
            frame.Inherits, frame.Placeholder, frame.Anonymous);

    [Fact]
    public void Regression_LockedThenEditableAlignTopMovesTheEditableOneOntoTheLockedTop()
    {
        var project = LockedAndEditable();

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["Stock", "Custom"], SelectionArrangeCommand.AlignTop);

        Assert.True(outcome.Changed);
        var rects = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(rects["Stock"].Top, rects["Custom"].Top, 6);
    }

    [Fact]
    public void Regression_EditableThenLockedAlignTopProducesIdenticalGeometry()
    {
        var project = LockedAndEditable();

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["Custom", "Stock"], SelectionArrangeCommand.AlignTop);

        Assert.True(outcome.Changed);
        var rects = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(rects["Stock"].Top, rects["Custom"].Top, 6);
    }

    [Fact]
    public void Regression_SelectionOrderDoesNotChangeTheResult()
    {
        var project = LockedAndEditable();
        var layout = LayoutResolver.Resolve(project);

        var lockedFirst = SelectionArrange.Arrange(project, layout, ["Stock", "Custom"], SelectionArrangeCommand.AlignTop);
        var editableFirst = SelectionArrange.Arrange(project, layout, ["Custom", "Stock"], SelectionArrangeCommand.AlignTop);

        // Order is the user's click order, which carries no geometric meaning. If it leaked into the
        // target it would depend on Primary, and the same two frames would align differently
        // depending on which one the inspector happened to be describing.
        Assert.Equal(ProjectCodec.Serialize(lockedFirst.Project), ProjectCodec.Serialize(editableFirst.Project));
        Assert.Equal(lockedFirst.Moved.Order(), editableFirst.Moved.Order());
        Assert.Equal(["Custom"], lockedFirst.Moved);
        // Both orders really did change the document, so the equality above is not vacuous.
        Assert.NotEqual(ProjectCodec.Serialize(project), ProjectCodec.Serialize(lockedFirst.Project));
        Assert.NotEqual(ProjectCodec.Serialize(project), ProjectCodec.Serialize(editableFirst.Project));
    }

    [Theory]
    [InlineData(SelectionArrangeCommand.AlignLeft, "Left")]
    [InlineData(SelectionArrangeCommand.AlignRight, "Right")]
    [InlineData(SelectionArrangeCommand.AlignTop, "Top")]
    [InlineData(SelectionArrangeCommand.AlignBottom, "Bottom")]
    [InlineData(SelectionArrangeCommand.AlignCenterHorizontal, "CenterX")]
    [InlineData(SelectionArrangeCommand.AlignCenterVertical, "CenterY")]
    public void Regression_EveryAlignCommandWorksAgainstALockedReference(SelectionArrangeCommand command, string edge)
    {
        var project = LockedAndEditable();
        var layout = LayoutResolver.Resolve(project);

        var outcome = SelectionArrange.Arrange(project, layout, ["Custom", "Stock"], command);

        Assert.True(outcome.Changed, $"{command} did nothing for a locked + editable selection.");
        Assert.Empty(outcome.Blocked);
        Assert.Equal(["Stock"], outcome.ExcludedLocked);
        // Custom was primary and is the only thing that moved, so this is not the locked frame
        // being dragged along.
        Assert.Equal(["Custom"], outcome.Moved);

        var after = LayoutResolver.Resolve(outcome.Project).Rects;
        var stock = after["Stock"];
        var custom = after["Custom"];

        // The stock frame is the alignment reference, so the editable frame has to end up on its
        // edge, whatever the command. Comparing to the stock rect rather than to a literal also
        // keeps this honest if the fixture geometry changes.
        var delta = edge switch
        {
            "Left" => custom.Left - stock.Left,
            "Right" => custom.Right - stock.Right,
            "Top" => custom.Top - stock.Top,
            "Bottom" => custom.Bottom - stock.Bottom,
            "CenterX" => custom.CenterX - stock.CenterX,
            _ => custom.CenterY - stock.CenterY,
        };
        Assert.Equal(0, delta, 6);
    }

    [Fact]
    public void Regression_LockedReferenceReceivesZeroModelMutation()
    {
        var project = LockedAndEditable();

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["Stock", "Custom"], SelectionArrangeCommand.AlignTop);

        // Not merely unmoved: byte-identical in the document, and structurally identical as a model.
        Assert.Equal(SerializedFrame(project, "Stock"), SerializedFrame(outcome.Project, "Stock"));
        Assert.Equal(project.Find("Stock"), outcome.Project.Find("Stock"));
        Assert.Equal(GeometryFingerprint(project.Find("Stock")!), GeometryFingerprint(outcome.Project.Find("Stock")!));
        Assert.Equal(-300, outcome.Project.Find("Stock")!.OffsetX, 9);
        Assert.Equal(200, outcome.Project.Find("Stock")!.OffsetY, 9);

        // The editable frame is what changed, which is what makes this a real assertion about
        // "the locked one stayed put" rather than "nothing happened".
        Assert.NotEqual(SerializedFrame(project, "Custom"), SerializedFrame(outcome.Project, "Custom"));
    }

    [Fact]
    public void Regression_EditableAnchorsSurviveAlignmentAgainstALockedReference()
    {
        var project = Project(
            Frame("Stock", width: 200, height: 100, offsetX: -300, offsetY: 200),
            Frame("Custom", width: 120, height: 40, point: AnchorPoint.CENTER, relativePoint: AnchorPoint.BOTTOMRIGHT,
                offsetX: 180, offsetY: -150)) with
        {
            Editor = new EditorMetadata { LockedElements = ["Stock"] },
        };

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["Stock", "Custom"], SelectionArrangeCommand.AlignTop);

        var custom = outcome.Project.Find("Custom")!;
        Assert.Equal(AnchorPoint.CENTER, custom.Point);
        Assert.Equal(AnchorPoint.BOTTOMRIGHT, custom.RelativePoint);
        Assert.Equal(project.Find("Custom")!.RelativeTo, custom.RelativeTo);
        Assert.Equal(120, custom.Width, 9);
        Assert.Equal(40, custom.Height, 9);
        // Positioning stayed relative; only the offset changed.
        Assert.NotEqual(project.Find("Custom")!.OffsetY, custom.OffsetY);
    }

    [Fact]
    public void Regression_OneLockedAndSeveralEditableAllMove()
    {
        var project = Project(
            Frame("Stock", width: 200, height: 100, offsetX: -300, offsetY: 300),
            Frame("A", width: 60, height: 20, offsetX: 0, offsetY: 0),
            Frame("B", width: 60, height: 20, offsetX: 100, offsetY: -50),
            Frame("C", width: 60, height: 20, offsetX: 200, offsetY: -100)) with
        {
            Editor = new EditorMetadata { LockedElements = ["Stock"] },
        };

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B", "C", "Stock"], SelectionArrangeCommand.AlignTop);

        Assert.True(outcome.Changed);
        Assert.Equal(["A", "B", "C"], outcome.Moved);
        var rects = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.All(["A", "B", "C"], name => Assert.Equal(rects["Stock"].Top, rects[name].Top, 6));
    }

    [Fact]
    public void Regression_SeveralLockedAndOneEditableMovesOnlyTheEditable()
    {
        var project = Project(
            Frame("L1", width: 200, height: 100, offsetX: -400, offsetY: 320),
            Frame("L2", width: 200, height: 100, offsetX: -400, offsetY: -320),
            Frame("Editable", width: 60, height: 20, offsetX: 120, offsetY: 0)) with
        {
            Editor = new EditorMetadata { LockedElements = ["L1", "L2"] },
        };

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["L1", "Editable", "L2"], SelectionArrangeCommand.AlignTop);

        Assert.True(outcome.Changed);
        Assert.Equal(["Editable"], outcome.Moved);
        Assert.Equal(["L1", "L2"], outcome.ExcludedLocked);
        var rects = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(rects["L1"].Top, rects["Editable"].Top, 6);
    }

    [Fact]
    public void Regression_AllLockedSelectionCannotChangeAnything()
    {
        var project = Project(
            Frame("L1", width: 200, height: 100, offsetX: -400, offsetY: 320),
            Frame("L2", width: 200, height: 100, offsetX: -400, offsetY: -320)) with
        {
            Editor = new EditorMetadata { LockedElements = ["L1", "L2"] },
        };
        var layout = LayoutResolver.Resolve(project);

        foreach (var command in Enum.GetValues<SelectionArrangeCommand>().Where(c => !SelectionArrange.IsDistribution(c)))
        {
            Assert.False(SelectionArrange.CanRun(project, layout, ["L1", "L2"], command));

            var outcome = SelectionArrange.Arrange(project, layout, ["L1", "L2"], command);
            Assert.False(outcome.Changed);
            // Reference equality, not just equal geometry: nothing may even be copied.
            Assert.Same(project, outcome.Project);
            Assert.Equal(ProjectCodec.Serialize(project), ProjectCodec.Serialize(outcome.Project));
            Assert.Contains("locked", outcome.Message);
        }
    }

    [Fact]
    public void Regression_AllEditableAlignmentIsUnchanged()
    {
        // The correction must not disturb the ordinary case: two editable frames align to their own
        // combined bounds exactly as before.
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0, offsetY: 0),
            Frame("B", width: 100, height: 100, offsetX: 300, offsetY: 200));

        var outcome = SelectionArrange.Arrange(
            project, LayoutResolver.Resolve(project), ["A", "B"], SelectionArrangeCommand.AlignLeft);

        Assert.True(outcome.Changed);
        // A already sits at the selection's left edge, so only B has anywhere to go. Asserting the
        // end state rather than the moved list keeps this a test of alignment, not of which side
        // of the union each frame happened to start on.
        Assert.Empty(outcome.ExcludedLocked);
        Assert.Empty(outcome.Blocked);
        var rects = LayoutResolver.Resolve(outcome.Project).Rects;
        Assert.Equal(rects["A"].Left, rects["B"].Left, 6);
        // A defines the union's left edge, so A is untouched and B is pulled onto it: B's left
        // goes from -212 to -512, i.e. its offset moves from 300 to 0.
        Assert.Equal(0, outcome.Project.Find("A")!.OffsetX, 6);
        Assert.Equal(0, outcome.Project.Find("B")!.OffsetX, 6);
    }

    [Fact]
    public void Regression_CommandEnablementRequiresTwoSelectedAndOneEditable()
    {
        var lockedAndEditable = LockedAndEditable();
        var layout = LayoutResolver.Resolve(lockedAndEditable);

        // One locked + one editable: the reported bug. This must be offered.
        Assert.True(SelectionArrange.CanRun(lockedAndEditable, layout, ["Stock", "Custom"], SelectionArrangeCommand.AlignTop));

        // One object alone is still not a selection.
        Assert.False(SelectionArrange.CanRun(lockedAndEditable, layout, ["Custom"], SelectionArrangeCommand.AlignTop));

        // Everything locked has nothing to move.
        var allLocked = Project(
            Frame("L1", width: 100, height: 100),
            Frame("L2", width: 100, height: 100, offsetX: 300)) with
        {
            Editor = new EditorMetadata { LockedElements = ["L1", "L2"] },
        };
        Assert.False(SelectionArrange.CanRun(allLocked, LayoutResolver.Resolve(allLocked), ["L1", "L2"], SelectionArrangeCommand.AlignTop));
    }

    [Fact]
    public void Regression_UnresolvableSelectionMemberDoesNotCountTowardsTheAlignGate()
    {
        var project = Project(
            Frame("Custom", width: 100, height: 100, offsetX: 0),
            Frame("Dangling", width: 100, height: 100, relativeTo: "Nope")) with
        {
            Editor = new EditorMetadata { LockedElements = ["Dangling"] },
        };
        var layout = LayoutResolver.Resolve(project);

        // The locked member resolves nowhere, so it contributes no bounds and there is no edge to
        // align against. Offering the command here would move Custom to a target nobody can see.
        Assert.False(SelectionArrange.CanRun(project, layout, ["Custom", "Dangling"], SelectionArrangeCommand.AlignTop));
        Assert.Contains(SelectionArrange.Arrange(project, layout, ["Custom", "Dangling"], SelectionArrangeCommand.AlignTop).Blocked,
            b => b.Reason.Contains("at least 2"));
    }

    [Fact]
    public void Regression_ProjectIdentityIsPreservedWhenNothingMoves()
    {
        // The dirty flag is driven off this, so a refused align must not hand back a copy.
        var project = Project(
            Frame("A", width: 100, height: 100, offsetX: 0, offsetY: 0),
            Frame("B", width: 100, height: 100, offsetX: 0, offsetY: 200));
        var layout = LayoutResolver.Resolve(project);

        // Same left edge already, so there is nothing to do...
        var alreadyAligned = SelectionArrange.Arrange(project, layout, ["A", "B"], SelectionArrangeCommand.AlignLeft);

        Assert.False(alreadyAligned.Changed);
        Assert.Same(project, alreadyAligned.Project);

        // ...whereas a command that does have work returns a different instance.
        var withWork = SelectionArrange.Arrange(project, layout, ["A", "B"], SelectionArrangeCommand.AlignBottom);
        Assert.True(withWork.Changed);
        Assert.NotSame(project, withWork.Project);
    }
}