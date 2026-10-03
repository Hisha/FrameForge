using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// The importer's contract, exercised against the real Native Hunts document and against
/// small synthetic documents that isolate one FrameXML rule each.
/// </summary>
/// <remarks>
/// The synthetic cases are not redundant with the fixture: the real file has 48 single anchors
/// and no multi-anchor element at all, so the multi-anchor path cannot be covered by it. The
/// fixture cases are equally un-substitutable, because hand-written XML proves nothing about
/// the document users will actually open.
/// </remarks>
public class FrameXmlImportTests
{
    private const string FixtureResource = "FrameForge.Core.Tests.fixtures.NativeHuntsFrame.xml";

    /// <summary>The real Native Hunts XML, read from the embedded copy.</summary>
    private static readonly byte[] FixtureBytes = ReadFixture();

    private static readonly FrameXmlImportResult Real = FrameXmlImporter.Import(
        Encoding.UTF8.GetString(FixtureBytes),
        "NativeHuntsFrame.xml");

    private static readonly Project RealProject = Real.Project!;

    private static readonly LayoutResult RealLayout = LayoutResolver.Resolve(RealProject);

    private static FrameDef Frame(string name) =>
        RealProject.Frames.First(f => f.Name == name);

    private static FrameRect Rect(string name) => RealLayout.Rects[name];

    private static byte[] ReadFixture()
    {
        var assembly = typeof(FrameXmlImportTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n == FixtureResource);
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ---------------------------------------------------------------- real document: counts

    [Fact]
    public void RealDocumentImportsWithoutErrors()
    {
        Assert.True(Real.Ok);
        Assert.Empty(Real.Errors);
        Assert.Equal("NativeHuntsFrame", RealProject.Name);
        Assert.Equal("NativeHuntsFrame.xml", Real.SourceFileName);
    }

    [Fact]
    public void RealDocumentDiscoversEveryLayoutElement()
    {
        // 7 Frame + 2 Button + 1 StatusBar + 21 Texture + 20 FontString, counted by hand from
        // the document. Plus the LFDParentFrame stand-in, which is not a source element.
        Assert.Equal(51, Real.ElementsDiscovered);
        Assert.Equal(52, RealProject.Frames.Count);

        Assert.Equal(7, Count(FrameKind.FRAME));
        Assert.Equal(2, Count(FrameKind.BUTTON));
        Assert.Equal(20, Count(FrameKind.FONTSTRING));
        Assert.Equal(21, Count(FrameKind.TEXTURE));
        Assert.Equal(1, Count(FrameKind.STATUSBAR));

        static int Count(FrameKind kind) => Real.Elements.Count(e => e.Kind == kind);
    }

    [Fact]
    public void RealDocumentIsNotClaimedToBeComplete()
    {
        // 9 partials: NativeHuntsFrame (anchored to a stand-in), six unsized FontStrings, and
        // the two tab buttons whose size comes from an unresolved template.
        Assert.Equal(42, Real.FullySupported);
        Assert.Equal(9, Real.PartiallySupported);
        Assert.Equal(0, Real.Unsupported);
        Assert.False(Real.IsComplete);

        Assert.Equal(
            ["FontString#12", "FontString#20", "FontString#21", "FontString#22", "FontString#23",
             "LFDParentFrameTab1", "LFDParentFrameTab2",
             "NativeHuntsFrame", "NativeHuntsFrameContentPanelTitle"],
            Real.Elements.Where(e => e.Support == FrameXmlSupport.Partial)
                .Select(e => e.Frame).Order(StringComparer.Ordinal));
    }

    // ------------------------------------------------------- real document: hierarchy & rules

    [Fact]
    public void ExplicitParentAttributeBeatsXmlNesting()
    {
        // NativeHuntsFrame nests inside <Ui> but names LFDParentFrame, which is how it attaches
        // to Blizzard's group finder.
        Assert.Equal("LFDParentFrame", Frame("NativeHuntsFrame").Parent);
        Assert.Equal("NativeHuntsFrame", Frame("Texture#1").Parent);
        Assert.Equal("NativeHuntsFrame", Frame("NativeHuntsFrameContentPanel").Parent);
        Assert.Equal("NativeHuntsFrameContentPanel", Frame("NativeHuntsFrameContentPanelIdle").Parent);
    }

    [Fact]
    public void DollarParentInRelativeToExpandsAgainstTheEnclosingFrame()
    {
        Assert.Equal(
            "NativeHuntsFrameContentPanelIdentity",
            Frame("NativeHuntsFrameContentPanelHuntState").RelativeTo);

        Assert.Equal(
            "NativeHuntsFrameContentPanelIdleState",
            Frame("NativeHuntsFrameContentPanelIdleDescription").RelativeTo);

        Assert.DoesNotContain(
            RealProject.Frames,
            f => f.Name.Contains("$parent", StringComparison.Ordinal));
    }

    [Fact]
    public void UiParentIsTheScreenAndNotAnExternalFrame()
    {
        // NativeHuntsFrameInitializer declares parent="UIParent"; treating it as a missing frame
        // would invent a placeholder and drag the layout bounds to a corner of nowhere.
        Assert.Null(Frame("NativeHuntsFrameInitializer").Parent);
        Assert.Null(Frame("NativeHuntsFrameInitializer").RelativeTo);
        Assert.DoesNotContain(RealProject.Frames, f => f.Name == "UIParent");
        Assert.DoesNotContain(Real.Diagnostics, d => d.Frame == "UIParent");
    }

    [Fact]
    public void OmittedRelativePointBecomesTheAnchorsOwnPoint()
    {
        // <Anchor point="TOPLEFT"/> with no relativePoint: WoW substitutes TOPLEFT, so a
        // top-left corner texture stays in its corner instead of being centred.
        var texture = Frame("Texture#1");
        Assert.Equal(AnchorPoint.TOPLEFT, texture.Point);
        Assert.Equal(AnchorPoint.TOPLEFT, texture.RelativePoint);

        var title = Frame("FontString#12");
        Assert.Equal(AnchorPoint.TOP, title.Point);
        Assert.Equal(AnchorPoint.TOP, title.RelativePoint);

        Assert.Contains(Real.Diagnostics, d => d.Code == FrameXmlDiagnosticCodes.RelativePointDefaulted);
    }

    [Fact]
    public void SetAllPointsIsModelledAsAFillRelationship()
    {
        var root = Frame("NativeHuntsFrame");
        Assert.True(root.SetAllPoints);
        Assert.Equal(0, root.Width);
        Assert.Equal(0, root.Height);

        // The StatusBar's fill texture inherits the bar's rectangle exactly.
        var fill = Frame("Texture#16");
        Assert.True(fill.SetAllPoints);
        Assert.Equal(Rect("NativeHuntsFrameContentPanelHuntStateProgress"), Rect("Texture#16"));
    }

    [Fact]
    public void HiddenAndStratumSurviveTheImport()
    {
        Assert.False(Frame("NativeHuntsFrame").Visible);
        Assert.False(Frame("NativeHuntsFrameContentPanelIdentity").Visible);

        // FontString#12 lives in an ARTWORK layer, which FrameForge folds into MEDIUM and says
        // so. Texture#14 is in a real BACKGROUND layer and is left alone.
        Assert.Equal(Stratum.MEDIUM, Frame("FontString#12").StratumOrDefault);
        Assert.Equal(Stratum.BACKGROUND, Frame("Texture#14").StratumOrDefault);
        Assert.Contains(
            Real.Diagnostics,
            d => d.Code == FrameXmlDiagnosticCodes.LayerStratumMapped && d.Message.Contains("ARTWORK", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWholeRealLayoutResolvesWithoutFallbacks()
    {
        Assert.Empty(RealLayout.Issues);
        Assert.Equal(RealProject.Frames.Count, RealLayout.Rects.Count);
    }

    [Fact]
    public void AnonymousElementsGetStableNonCollidingNames()
    {
        var anonymous = RealProject.Frames.Where(f => f.Anonymous).ToArray();

        Assert.NotEmpty(anonymous);
        Assert.All(anonymous, f => Assert.True(
            f.Name.StartsWith("Texture#", StringComparison.Ordinal) ||
            f.Name.StartsWith("FontString#", StringComparison.Ordinal),
            $"{f.Name} is not a generated identity"));
        Assert.All(anonymous, f => Assert.Null(f.SourceName));

        // Re-importing the same document must produce the same identities.
        var again = FrameXmlImporter.Import(Encoding.UTF8.GetString(FixtureBytes), "NativeHuntsFrame.xml");
        Assert.Equal(
            anonymous.Select(f => f.Name),
            again.Project!.Frames.Where(f => f.Anonymous).Select(f => f.Name));

        Assert.Contains(Real.Diagnostics, d => d.Code == FrameXmlDiagnosticCodes.AnonymousElement);
    }

    [Fact]
    public void AnchorDataIsRetainedForVisualization()
    {
        var panel = RealLayout.Frames["NativeHuntsFrameContentPanel"];

        var anchor = Assert.Single(panel.Anchors);
        Assert.True(anchor.Primary);
        Assert.Equal("NativeHuntsFrame", anchor.Target);
        Assert.False(anchor.TargetIsPlaceholder);
        Assert.True(anchor.Resolved);
        Assert.Equal(new ModelPoint(0, 250), anchor.TargetPosition);
        Assert.Equal(new ModelPoint(12, 206), anchor.OwnPosition);
        Assert.Equal(12, anchor.OffsetX);
        Assert.Equal(-44, anchor.OffsetY);

        var placeholder = Assert.Single(RealLayout.Frames["NativeHuntsFrame"].Anchors);
        Assert.True(placeholder.TargetIsPlaceholder);
    }

    // ------------------------------------------------------------- real document: geometry

    /// <summary>
    /// The real document laid out on a 1024x768 screen with the documented 355x500 stand-in for
    /// LFDParentFrame, centred on the screen. Hand-derived: the frame fills its parent, the
    /// content panel hangs 44 below the frame top and 12 in from its centre, and the identity,
    /// state, idle and record panels hang off the content panel.
    /// </summary>
    [Theory]
    [InlineData("NativeHuntsFrame", -177.5, 250, 177.5, -250)]
    [InlineData("NativeHuntsFrameContentPanel", -136, 206, 160, -200)]
    [InlineData("NativeHuntsFrameContentPanelIdentity", -128, 180, 152, 92)]
    [InlineData("NativeHuntsFrameContentPanelHuntState", -128, 62, 152, -56)]
    [InlineData("NativeHuntsFrameContentPanelIdle", -128, 180, 152, -32)]
    [InlineData("NativeHuntsFrameContentPanelRecord", -128, -106, 152, -192)]
    [InlineData("NativeHuntsFrameContentPanelIdentityIcon", -115, 165, -57, 107)]
    [InlineData("Texture#1", -156.5, 94, 169.5, -220)]
    [InlineData("NativeHuntsFrameContentPanelHuntStateProgress", -79, 13, 105, 1)]
    [InlineData("LFDParentFrameTab1", -159.5, -277, -159.5, -277)]
    public void RealDocumentMatchesHandComputedBounds(string name, double left, double top, double right, double bottom)
    {
        var rect = Rect(name);

        Assert.Equal(left, rect.Left, 6);
        Assert.Equal(top, rect.Top, 6);
        Assert.Equal(right, rect.Right, 6);
        Assert.Equal(bottom, rect.Bottom, 6);
    }

    [Fact]
    public void ContentPanelStacksTheThreeStatePanelsInDocumentOrder()
    {
        // identity panel on top, 30px gap, then the hunt state panel, and the record panel
        // pinned 8px above the content panel's bottom edge.
        Assert.Equal(92, Rect("NativeHuntsFrameContentPanelIdentity").Bottom);
        Assert.Equal(62, Rect("NativeHuntsFrameContentPanelHuntState").Top);
        Assert.Equal(-106, Rect("NativeHuntsFrameContentPanelRecord").Top);
        Assert.Equal(-192, Rect("NativeHuntsFrameContentPanelRecord").Bottom);
    }

    [Fact]
    public void HiddenSubtreesAreMarkedNotDrawn()
    {
        Assert.True(RealLayout.Frames["NativeHuntsFrame"].OwnVisible is false);
        Assert.True(RealLayout.Frames["Texture#1"].EffectiveVisible is false);
        Assert.True(RealLayout.Frames["NativeHuntsFrameContentPanel"].EffectiveVisible is false);
    }

    // ------------------------------------------------------------ real document: placeholders

    [Fact]
    public void ExactlyOneStandInIsCreatedForTheExternalBlizzardFrame()
    {
        var standIn = Frame("LFDParentFrame");

        Assert.True(standIn.Placeholder);
        Assert.Equal(355, standIn.Width);
        Assert.Equal(500, standIn.Height);
        Assert.Null(standIn.Parent);
        Assert.Single(RealProject.Frames, f => f.Placeholder);

        Assert.Contains(
            Real.Diagnostics,
            d => d.Code == FrameXmlDiagnosticCodes.ExternalFramePlaceholder && d.Frame == "LFDParentFrame");

        // The root frame and the first tab anchor to it, so both are only partly known.
        Assert.Contains(
            Real.Diagnostics,
            d => d.Code == FrameXmlDiagnosticCodes.UnresolvedReference
                 && d.Message.Contains("NativeHuntsFrame", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaceholdersCanBeTurnedOff()
    {
        var options = FrameXmlImportOptions.Default with { CreateExternalFramePlaceholders = false };
        var result = FrameXmlImporter.Import(Encoding.UTF8.GetString(FixtureBytes), "NativeHuntsFrame.xml", options);

        Assert.DoesNotContain(result.Project!.Frames, f => f.Placeholder);
        Assert.NotEmpty(LayoutResolver.Resolve(result.Project).Issues);
    }

    // ------------------------------------------------------------ real document: diagnostics

    [Theory]
    [InlineData(FrameXmlDiagnosticCodes.ScriptFileNotExecuted)]
    [InlineData(FrameXmlDiagnosticCodes.ScriptsNotExecuted)]
    [InlineData(FrameXmlDiagnosticCodes.AutomaticSize)]
    [InlineData(FrameXmlDiagnosticCodes.SetAllPoints)]
    [InlineData(FrameXmlDiagnosticCodes.LayerStratumMapped)]
    [InlineData(FrameXmlDiagnosticCodes.AnonymousElement)]
    [InlineData(FrameXmlDiagnosticCodes.ExternalFramePlaceholder)]
    [InlineData(FrameXmlDiagnosticCodes.UnresolvedReference)]
    [InlineData(FrameXmlDiagnosticCodes.VisualRetainedNotRendered)]
    [InlineData(FrameXmlDiagnosticCodes.UnresolvedTemplate)]
    [InlineData(FrameXmlDiagnosticCodes.RelativePointDefaulted)]
    public void RealDocumentReportsEveryGapItActuallyHas(string code) =>
        Assert.Contains(Real.Diagnostics, d => d.Code == code);

    [Theory]
    [InlineData("Size")]
    [InlineData("Color")]
    [InlineData("TexCoords")]
    [InlineData("Anchors")]
    [InlineData("Anchor")]
    [InlineData("Offset")]
    [InlineData("AbsDimension")]
    [InlineData("BarTexture")]
    [InlineData("BarColor")]
    [InlineData("OnClick")]
    [InlineData("OnLoad")]
    [InlineData("OnEvent")]
    public void PaintAndDataElementsAreNotDressedUpAsProblems(string element)
    {
        // These carry no geometry and FrameForge read the ones that matter. Warning about them
        // would bury the fourteen warnings that do mean something.
        Assert.DoesNotContain(Real.Diagnostics, d => d.Element == element && d.Code == FrameXmlDiagnosticCodes.UnsupportedElement);
    }

    [Fact]
    public void TheSummaryCountsAndWarnsWithoutPretendingToBeComplete()
    {
        Assert.Contains("51 visual/layout elements", Real.SummaryText, StringComparison.Ordinal);
        Assert.Contains("42 fully supported", Real.SummaryText, StringComparison.Ordinal);
        Assert.Contains("9 partially supported", Real.SummaryText, StringComparison.Ordinal);
        Assert.Contains("warning", Real.WarningSummary, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ real document: source

    [Fact]
    public void ImportingNeverModifiesTheSource()
    {
        var path = Path.Combine(Path.GetTempPath(), $"frameforge-{Guid.NewGuid():N}.xml");
        File.WriteAllBytes(path, FixtureBytes);

        try
        {
            var before = File.ReadAllBytes(path);
            var result = FrameXmlImporter.ImportFile(path);
            var after = File.ReadAllBytes(path);

            Assert.True(result.Ok);
            Assert.Equal(before, after);
            Assert.Equal(SHA256.HashData(FixtureBytes), SHA256.HashData(after));
            Assert.Equal(Path.GetFileName(path), result.SourceFileName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheEmbeddedFixtureIsStillTheRealFile()
    {
        var digest = Convert.ToHexString(SHA256.HashData(FixtureBytes)).ToLowerInvariant();

        Assert.Equal(
            "42673d3fc3f2046315de8cb1cb630ad018769734cf383e5c5b0bdb9eafda47c0",
            digest);
    }

    [Fact]
    public void TheProjectRecordsThatItCameFromReadOnlyFrameXml()
    {
        Assert.NotNull(RealProject.Source);
        Assert.Equal(SourceTypes.WowFrameXml, RealProject.Source!.Type);
        Assert.Equal("NativeHuntsFrame.xml", RealProject.Source.FileName);
        Assert.True(RealProject.Source.ReadOnly);
    }

    // --------------------------------------------------------------------------- round trip

    [Fact]
    public void AnImportedProjectSurvivesASerializationRoundTrip()
    {
        var json = ProjectCodec.Serialize(RealProject);
        var parsed = ProjectCodec.Parse(json);
        Assert.Empty(parsed.Errors);
        var restored = parsed.Project!;

        Assert.Equal(RealProject, restored);
        Assert.Equal(RealProject.Frames.Count, restored.Frames.Count);

        var fill = restored.Frames.First(f => f.Name == "NativeHuntsFrameContentPanelHuntStateProgress");
        Assert.Equal(FrameKind.STATUSBAR, fill.Kind);

        var standIn = restored.Frames.First(f => f.Name == "LFDParentFrame");
        Assert.True(standIn.Placeholder);
        Assert.Equal(SourceTypes.WowFrameXml, restored.Source!.Type);
    }

    [Fact]
    public void AnImportedProjectKeepsItsGeometryAfterARoundTrip()
    {
        var restored = ProjectCodec.Parse(ProjectCodec.Serialize(RealProject)).Project!;
        var layout = LayoutResolver.Resolve(restored);

        Assert.Empty(layout.Issues);
        Assert.Equal(RealLayout.Rects.Count, layout.Rects.Count);
        Assert.Equal(
            RealLayout.Rects["NativeHuntsFrameContentPanelHuntState"].Top,
            layout.Rects["NativeHuntsFrameContentPanelHuntState"].Top,
            6);
    }

    // --------------------------------------------------------------------- synthetic: anchors

    [Fact]
    public void MultipleAnchorsAreAllKept()
    {
        var result = Import("""
            <Ui>
              <Frame name="Host">
                <Size x="400" y="200"/>
              </Frame>
              <Frame name="Stretched" parent="Host">
                <Size x="40" y="40"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                  <Anchor point="TOPRIGHT" relativePoint="TOPRIGHT"/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var frame = result.Project!.Frames.First(f => f.Name == "Stretched");
        Assert.True(frame.HasMultipleAnchors);
        Assert.Single(frame.ExtraAnchors);
        Assert.Equal(AnchorPoint.TOPRIGHT, frame.ExtraAnchors[0].Point);

        Assert.Contains(
            result.Diagnostics,
            d => d.Code == FrameXmlDiagnosticCodes.MultipleAnchors && d.Frame == "Stretched");

        // WoW's stretch: both edges of the frame are pinned to the same edges of a 400-wide host,
        // so the frame is 400 wide even though it declared 40. This is the reason imported stretch
        // frames are authored with <Size x="0" y="0"/>.
        var layout = LayoutResolver.Resolve(result.Project!);
        var rect = layout.Rects["Stretched"];
        Assert.Equal(400, rect.Width);
        Assert.Equal(layout.Rects["Host"].Left, rect.Left, 6);
        Assert.Equal(layout.Rects["Host"].Right, rect.Right, 6);
        Assert.True(layout.Frames["Stretched"].HasMultipleAnchors);
        Assert.All(layout.Frames["Stretched"].Anchors, a => Assert.True(a.Resolved));
        Assert.Empty(layout.Issues);
    }

    [Fact]
    public void AnyTwoPointsOfOneTargetFrameSolveTheFrameJustAsWowWould()
    {
        // TOPLEFT on A.TOPLEFT plus BOTTOMLEFT on A.BOTTOMLEFT: unusual as an idiom, but not
        // contradictory. WoW sizes a frame from any two of its own points against the target, and
        // so does FrameForge - here the width stays as declared and the height becomes A's.
        var result = Import("""
            <Ui>
              <Frame name="A">
                <Size x="100" y="100"/>
              </Frame>
              <Frame name="B" parent="A">
                <Size x="10" y="10"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                  <Anchor point="BOTTOMLEFT" relativePoint="BOTTOMLEFT"/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var layout = LayoutResolver.Resolve(result.Project!);
        var rect = layout.Rects["B"];

        Assert.Equal(10, rect.Width);
        Assert.Equal(100, rect.Height);
        Assert.All(layout.Frames["B"].Anchors, a => Assert.True(a.Resolved));
        Assert.Empty(layout.Issues);
    }

    [Fact]
    public void AnAnchorOntoADifferentTargetFrameIsReportedRatherThanGuessed()
    {
        var result = Import("""
            <Ui>
              <Frame name="A">
                <Size x="100" y="100"/>
              </Frame>
              <Frame name="C">
                <Size x="40" y="40"/>
              </Frame>
              <Frame name="B" parent="A">
                <Size x="10" y="10"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                  <Anchor point="TOPRIGHT" relativePoint="TOPRIGHT" relativeTo="C"/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var layout = LayoutResolver.Resolve(result.Project!);
        var anchors = layout.Frames["B"].Anchors;

        Assert.Equal(2, anchors.Count);
        Assert.True(anchors[0].Resolved);
        Assert.False(anchors[1].Resolved);
        Assert.Contains("different frames", anchors[1].Note!, StringComparison.Ordinal);
        Assert.Single(layout.Frames["B"].UnresolvedAnchorNotes);

        // The primary anchor still positions the frame, using its declared size.
        Assert.Equal(10, layout.Rects["B"].Width);
        Assert.Contains(layout.Issues, i => i.Frame == "B" && i.Message.Contains("different frames", StringComparison.Ordinal));
    }

    [Fact]
    public void AThirdAnchorThatContradictsTheFirstTwoIsFlaggedAndLoses()
    {
        var result = Import("""
            <Ui>
              <Frame name="Host">
                <Size x="400" y="200"/>
              </Frame>
              <Frame name="Stretched" parent="Host">
                <Size x="0" y="0"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                  <Anchor point="TOPRIGHT" relativePoint="TOPRIGHT"/>
                  <Anchor point="BOTTOMRIGHT" relativePoint="BOTTOMRIGHT">
                    <Offset>
                      <AbsDimension x="-50" y="0"/>
                    </Offset>
                  </Anchor>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var layout = LayoutResolver.Resolve(result.Project!);
        var anchors = layout.Frames["Stretched"].Anchors;

        Assert.Equal(400, layout.Rects["Stretched"].Width);
        Assert.True(anchors[0].Resolved);
        Assert.True(anchors[1].Resolved);
        Assert.False(anchors[2].Resolved);
        Assert.Contains("350", anchors[2].Note!, StringComparison.Ordinal);
        Assert.Contains("400", anchors[2].Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnchorWhoseEndsDisagreeIsPositionedButNotResolved()
    {
        var result = Import("""
            <Ui>
              <Frame name="Host">
                <Size x="400" y="200"/>
              </Frame>
              <Frame name="Stretched" parent="Host">
                <Size x="0" y="0"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                  <Anchor point="TOPRIGHT" relativePoint="TOPRIGHT"/>
                  <Anchor point="BOTTOMRIGHT" relativePoint="BOTTOMRIGHT">
                    <Offset>
                      <AbsDimension x="-50" y="0"/>
                    </Offset>
                  </Anchor>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var layout = LayoutResolver.Resolve(result.Project!);
        var anchors = layout.Frames["Stretched"].Anchors;

        // The third anchor's ends are both known - that is what lets the canvas draw the connector
        // and mark it as failing - but they disagree, so it must not be presented as honoured.
        // TargetPositioned is what separates "I know where it is and it is wrong" from "I do not
        // know where it is", and both cases default to the model origin.
        var third = anchors[2];
        Assert.True(third.TargetPositioned);
        Assert.False(third.Resolved);
        Assert.NotEqual(default, third.TargetPosition);
    }

    [Fact]
    public void EveryAnchorOfAResolvedFrameHasAKnownTarget()
    {
        // Resolve refuses to position a frame whose target it could not place, so an anchor that
        // reaches a caller always has both ends. That is what lets the canvas draw a connector
        // without checking the default position for meaning: ResolvedAnchor.TargetPosition is a
        // plain ModelPoint, and the model origin is a real, drawable place, so the type carries
        // this flag rather than leaving callers to infer "unknown" from a coordinate.
        var result = Import("""
            <Ui>
              <Frame name="Host">
                <Size x="400" y="200"/>
              </Frame>
              <Frame name="Child" parent="Host">
                <Size x="400" y="200"/>
                <Anchors>
                  <Anchor point="TOPLEFT" relativePoint="BOTTOMRIGHT">
                    <Offset>
                      <AbsDimension x="7" y="-3"/>
                    </Offset>
                  </Anchor>
                  <Anchor point="BOTTOM" relativePoint="BOTTOM"/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var layout = LayoutResolver.Resolve(result.Project!);
        var anchors = layout.Frames["Child"].Anchors;

        // Anchor 2 asks for a negative size and is refused; that is about resolution, not about
        // knowing where the target is, and the two must stay independent.
        Assert.Equal(2, anchors.Count);
        Assert.True(anchors[0].Resolved);
        Assert.False(anchors[1].Resolved);
        Assert.All(anchors, a =>
        {
            Assert.True(a.TargetPositioned);
            Assert.NotEqual(default, a.TargetPosition);
        });
    }

    [Fact]
    public void AnchoringToAnUndefinedFrameCreatesAMarkedStandIn()
    {
        var result = Import("""
            <Ui>
              <Frame name="Panel">
                <Size x="100" y="100"/>
                <Anchors>
                  <Anchor point="TOPLEFT" relativeTo="ExternalFrame"/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        Assert.True(result.Project!.Find("ExternalFrame") is { Placeholder: true });

        // The stand-in is positioned, so the connector is drawable - but the user has to be able to
        // tell that the second end is FrameForge's guess rather than something the file stated.
        var layout = LayoutResolver.Resolve(result.Project);
        var panelAnchor = Assert.Single(layout.Frames["Panel"].Anchors);
        Assert.True(panelAnchor.TargetPositioned);
        Assert.True(panelAnchor.Resolved);
        Assert.True(panelAnchor.TargetIsPlaceholder);

        // And the stand-in's own geometry is flagged in the diagnostics, not quietly assumed.
        Assert.Contains(result.Diagnostics, d =>
            d.Severity is FrameXmlSeverity.Warning &&
            d.Frame == "ExternalFrame" &&
            d.Message.Contains("APPROXIMATE", StringComparison.Ordinal));
    }

    [Fact]
    public void AbsoluteOffsetsAreReadAndScaleOffsetsAreRefused()
    {
        var result = Import("""
            <Ui>
              <Frame name="Host">
                <Size x="100" y="100"/>
              </Frame>
              <Frame name="Absolute" parent="Host">
                <Size x="10" y="10"/>
                <Anchors>
                  <Anchor point="TOPLEFT">
                    <Offset>
                      <AbsDimension x="12.5" y="-7"/>
                    </Offset>
                  </Anchor>
                </Anchors>
              </Frame>
              <Frame name="Scaled" parent="Host">
                <Size x="10" y="10"/>
                <Anchors>
                  <Anchor point="TOPLEFT">
                    <Offset>
                      <Scale x="0.5" y="0.5"/>
                    </Offset>
                  </Anchor>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var absolute = result.Project!.Frames.First(f => f.Name == "Absolute");
        Assert.Equal(12.5, absolute.OffsetX);
        Assert.Equal(-7, absolute.OffsetY);

        var scaled = result.Project.Frames.First(f => f.Name == "Scaled");
        Assert.Equal(0, scaled.OffsetX);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == FrameXmlDiagnosticCodes.ScaleOffsetUnsupported && d.Frame == "Scaled");
    }

    [Fact]
    public void AReferenceToAnUndefinedFrameIsNotSilentlyIgnored()
    {
        var result = Import("""
            <Ui>
              <Frame name="Orphan" parent="SomeBlizzardFrame">
                <Size x="10" y="10"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        Assert.True(result.Ok);
        Assert.Single(result.Project!.Frames, f => f.Name == "SomeBlizzardFrame");
        Assert.Single(result.Project.Frames, f => f.Placeholder);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == FrameXmlDiagnosticCodes.ExternalFramePlaceholder && d.Frame == "SomeBlizzardFrame");
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == FrameXmlDiagnosticCodes.UnresolvedReference
                 && d.Message.Contains("Orphan", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnresolvableReferenceIsReportedByTheGeometryEngine()
    {
        var result = Import(
            """
            <Ui>
              <Frame name="Orphan" parent="SomeBlizzardFrame">
                <Size x="10" y="10"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                </Anchors>
              </Frame>
            </Ui>
            """,
            FrameXmlImportOptions.Default with { CreateExternalFramePlaceholders = false });

        var layout = LayoutResolver.Resolve(result.Project!);
        Assert.Contains(layout.Issues, i => i.Frame == "Orphan");
        Assert.DoesNotContain("Orphan", layout.Rects.Keys);
    }

    // ---------------------------------------------------------------- synthetic: bad input

    [Theory]
    [InlineData("<Ui><Frame name=\"A\"></Ui>", FrameXmlDiagnosticCodes.MalformedXml)]
    [InlineData("<Layout><Frame name=\"A\"/></Layout>", FrameXmlDiagnosticCodes.MalformedXml)]
    [InlineData("<Ui xmlns=\"http://example.com/not-wow\"><Frame name=\"A\"/></Ui>", FrameXmlDiagnosticCodes.MalformedXml)]
    [InlineData("   ", FrameXmlDiagnosticCodes.MalformedXml)]
    public void UnusableDocumentsFailWithAnErrorAndNoProject(string xml, string code)
    {
        var result = FrameXmlImporter.Import(xml, "broken.xml");

        Assert.False(result.Ok);
        Assert.Null(result.Project);
        Assert.Contains(result.Errors, d => d.Code == code);
        Assert.Contains("failed", result.SummaryText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADocumentWithNoLayoutElementsSaysSo()
    {
        var result = Import("<Ui><Script file=\"Something.lua\"/></Ui>");

        Assert.True(result.Ok);
        Assert.Equal(0, result.ElementsDiscovered);
        Assert.Contains(result.Errors, d => d.Code == FrameXmlDiagnosticCodes.NoLayoutElements);
        Assert.Empty(result.Project!.Frames);
    }

    [Fact]
    public void AMissingFileFailsWithoutThrowing()
    {
        var result = FrameXmlImporter.ImportFile(Path.Combine(Path.GetTempPath(), "frameforge-not-here.xml"));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, d => d.Code == FrameXmlDiagnosticCodes.MalformedXml);
    }

    [Fact]
    public void UnparseableNumbersAndAnchorPointsAreReportedWithTheirFallbacks()
    {
        var result = Import("""
            <Ui>
              <Frame name="Odd">
                <Size x="wide" y="NaN"/>
                <Anchors>
                  <Anchor point="MIDDLE" relativePoint="NOWHERE">
                    <Offset>
                      <AbsDimension x="Infinity" y="3"/>
                    </Offset>
                  </Anchor>
                  <Anchor/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var frame = result.Project!.Frames.Single();
        Assert.Equal(0, frame.Width);
        Assert.Equal(ProjectFactory.DefaultPoint, frame.Point);
        Assert.Equal(ProjectFactory.DefaultPoint, frame.RelativePoint);

        // Size x="wide", Size y="NaN" and AbsDimension x="Infinity"; AbsDimension y="3" is fine.
        Assert.Equal(3, result.Warnings.Count(d => d.Code == FrameXmlDiagnosticCodes.BadNumber));
        // "MIDDLE", "NOWHERE", and the point of the bare <Anchor/> that has none.
        Assert.Equal(3, result.Warnings.Count(d => d.Code == FrameXmlDiagnosticCodes.UnknownAnchorPoint));
    }

    [Fact]
    public void TwoElementsThatResolveToOneNameAreBothKept()
    {
        var result = Import("""
            <Ui>
              <Frame name="Twin">
                <Size x="10" y="10"/>
              </Frame>
              <Frame name="Twin">
                <Size x="20" y="20"/>
              </Frame>
            </Ui>
            """);

        Assert.Equal(2, result.Project!.Frames.Count);
        Assert.Equal(2, result.Project.Frames.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(result.Warnings, d => d.Code == FrameXmlDiagnosticCodes.DuplicateName);
        Assert.Empty(LayoutResolver.Resolve(result.Project).Issues);
    }

    [Fact]
    public void ADollarParentNameAtTheTopLevelIsRenamedNotCollided()
    {
        var result = Import("""
            <Ui>
              <Frame name="$parent">
                <Size x="10" y="10"/>
              </Frame>
            </Ui>
            """);

        var frame = Assert.Single(result.Project!.Frames);
        Assert.NotEqual("UIParent", frame.Name);
        Assert.Contains(result.Warnings, d => d.Code == FrameXmlDiagnosticCodes.UnexpandedParentName);
    }

    // ------------------------------------------------------------ synthetic: not-a-layout stuff

    [Fact]
    public void IncludesAndTemplatesAreDeclaredAsNotRead()
    {
        var result = Import("""
            <Ui>
              <Include file="SomeSharedFrame.xml"/>
              <Template name="Shared">
                <Size x="1" y="1"/>
              </Template>
              <Frame name="User" inherits="Shared">
                <Size x="30" y="40"/>
              </Frame>
            </Ui>
            """);

        Assert.Contains(result.Warnings, d => d.Code == FrameXmlDiagnosticCodes.IncludeNotLoaded);
        Assert.Contains(result.Warnings, d => d.Code == FrameXmlDiagnosticCodes.TemplateDefinition);
        Assert.Contains(result.Warnings, d => d.Code == FrameXmlDiagnosticCodes.UnresolvedTemplate && d.Frame == "User");

        var user = result.Project!.Frames.Single();
        Assert.Equal("Shared", user.Inherits);
        Assert.Equal(30, user.Width);
        Assert.Equal(FrameXmlSupport.Partial, result.Elements.Single().Support);
    }

    [Fact]
    public void AFontTemplateOnASizedFontStringDoesNotCostItItsGeometry()
    {
        var result = Import("""
            <Ui>
              <FontString name="Label" inherits="GameFontNormalLarge" text="Hunts">
                <Size x="100" y="12"/>
                <Anchors>
                  <Anchor point="TOPLEFT"/>
                </Anchors>
              </FontString>
            </Ui>
            """);

        Assert.Equal(FrameXmlSupport.Full, result.Elements.Single().Support);
        Assert.Contains(
            result.Infos,
            d => d.Code == FrameXmlDiagnosticCodes.UnresolvedTemplate && d.Frame == "Label");
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CheckButtonsAreImportedAsButtons()
    {
        var result = Import("""
            <Ui>
              <CheckButton name="Toggle">
                <Size x="24" y="24"/>
              </CheckButton>
            </Ui>
            """);

        Assert.Equal(FrameKind.BUTTON, result.Elements.Single().Kind);
    }

    [Fact]
    public void TheBlizzardNamespaceIsAccepted()
    {
        var result = Import("""
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="Namespaced">
                <Size x="10" y="10"/>
                <Anchors>
                  <Anchor point="TOPLEFT" relativePoint="BOTTOMRIGHT"/>
                </Anchors>
              </Frame>
            </Ui>
            """);

        var frame = Assert.Single(result.Project!.Frames);
        Assert.Equal("Namespaced", frame.Name);
        Assert.Equal(AnchorPoint.BOTTOMRIGHT, frame.RelativePoint);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ImportOptionsDecideTheScreenAndTheStandInSize()
    {
        var options = FrameXmlImportOptions.Default with
        {
            Screen = new Screen(1920, 1080),
            ExternalFrameSize = new Screen(355, 440),
            ExternalFramePosition = new ModelPoint(100, 200),
        };

        var result = FrameXmlImporter.Import("""
            <Ui>
              <Frame name="Child" parent="Blizzard">
                <Size x="10" y="10"/>
              </Frame>
            </Ui>
            """, "Anything.xml", options);

        Assert.Equal(new Screen(1920, 1080), result.Project!.Screen);

        var standIn = result.Project.Frames.Single(f => f.Name == "Blizzard");
        Assert.Equal(355, standIn.Width);
        Assert.Equal(440, standIn.Height);
        Assert.Equal(new FrameRect(-77.5, 420, 277.5, -20), LayoutResolver.Resolve(result.Project!).Rects["Blizzard"]);
    }

    // ------------------------------------------------------------------------------- helpers

    private static FrameXmlImportResult Import(string xml, FrameXmlImportOptions? options = null) =>
        FrameXmlImporter.Import(xml, "Inline.xml", options);

    [Fact]
    public void DiagnosticCodesAreStable()
    {
        var declared = typeof(FrameXmlDiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        var emitted = Real.Diagnostics.Select(d => d.Code).Distinct(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(declared);
        Assert.All(emitted, code => Assert.Contains(code, declared));
        Assert.DoesNotContain(declared, string.IsNullOrWhiteSpace);
    }
}