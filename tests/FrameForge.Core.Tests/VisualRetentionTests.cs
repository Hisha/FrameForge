using System.Text;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// Phase 3's premise, made checkable: the paint facts in a FrameXML document are captured rather
/// than discarded.
/// </summary>
/// <remarks>
/// These tests exist because "we do not draw Blizzard artwork yet" is a gap in RENDERING, not in
/// CAPTURE. Phase 2 treated texture paths, texcoords, colours, literal text and status bar values
/// as ignorable, which meant they were gone from the model and had to be re-derived from
/// documentation later. They are now retained, and these tests pin down exactly what the real
/// Native Hunts document yields, counted from the file itself.
/// </remarks>
public class VisualRetentionTests
{
    private const string FixtureResource = "FrameForge.Core.Tests.fixtures.NativeHuntsFrame.xml";

    private static readonly Project Project = FrameXmlImporter.Import(
        Encoding.UTF8.GetString(ReadFixture()),
        "NativeHuntsFrame.xml").Project!;

    private static FrameDef Frame(string name) =>
        Project.Frames.First(f => f.Name == name);

    private static FrameDef AnonymousTexture(int ordinal) =>
        Project.Frames.First(f => f.Name == $"Texture#{ordinal}");

    private static byte[] ReadFixture()
    {
        var assembly = typeof(VisualRetentionTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            assembly.GetManifestResourceNames().Single(n => n == FixtureResource))!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ------------------------------------------------------------------ textures

    /// <summary>
    /// Twenty-one <c>&lt;Texture&gt;</c> elements in total. Nineteen of them name a file and every
    /// one must keep that path verbatim; the other two declare no paint at all and must keep
    /// nothing.
    /// </summary>
    [Fact]
    public void KeepsEveryTexturePathTheDocumentDeclared()
    {
        var paths = Project.Frames
            .Where(f => f.Kind == FrameKind.TEXTURE)
            .Select(f => f.Visual?.Texture?.File)
            .Where(p => p is { Length: > 0 })
            .ToList();

        Assert.Equal(19, paths.Count);
        Assert.All(paths, p => Assert.Contains("\\", p));

        // The eight frame borders all come out of ONE atlas file, distinguished only by texcoords.
        Assert.Equal(
            8,
            paths.Count(p => p == @"Interface\LFGFrame\UI-LFG-FRAME"));

        Assert.Contains(@"Interface\NativeHunts\hunt_divider.tga", paths);
        Assert.Contains(@"Interface\NativeHunts\hunt_trail_prints.tga", paths);
        Assert.Contains(@"Interface\NativeHunts\hunt_icon_turnin.tga", paths);
        Assert.Contains(@"Interface\NativeHunts\hunt_panel_state.tga", paths);
        Assert.Contains(@"Interface\NativeHunts\hunt_panel_idle.tga", paths);
        Assert.Contains(@"Interface\NativeHunts\hunt_panel_record.tga", paths);
    }

    /// <summary>
    /// The texcoords are load-bearing, not decoration: all eight borders share one file, so
    /// dropping them would collapse eight distinct elements into one indistinguishable blob.
    /// </summary>
    [Fact]
    public void KeepsTheAtlasSubRectangleThatDistinguishesTenBordersFromOneFile()
    {
        var border = AnonymousTexture(4);

        Assert.Equal(FrameKind.TEXTURE, border.Kind);
        Assert.Equal(@"Interface\LFGFrame\UI-LFG-FRAME", border.Visual!.Texture!.File);
        Assert.True(border.Visual.Texture.TexCoords.IsSubRectangle);
        Assert.Equal(0, border.Visual.Texture.TexCoords.Left);
        Assert.Equal(0.6953125, border.Visual.Texture.TexCoords.Right, 9);
        Assert.Equal(0, border.Visual.Texture.TexCoords.Top);
        Assert.Equal(0.3046875, border.Visual.Texture.TexCoords.Bottom, 9);
    }

    [Fact]
    public void TreatsATextureWithNoTexCoordsAsTheWholeFile()
    {
        var readyIcon = Frame("NativeHuntsFrameContentPanelHuntStateReadyIcon");

        Assert.Equal(TexCoords.Full, readyIcon.Visual!.Texture!.TexCoords);
        Assert.False(readyIcon.Visual.Texture.TexCoords.IsSubRectangle);

        // The file is kept even though the element also declares nothing else about itself.
        Assert.Equal(@"Interface\NativeHunts\hunt_icon_turnin.tga", readyIcon.Visual.Texture.File);
    }

    /// <summary>
    /// The trail prints carry <c>alpha="0.38"</c>. That single number is most of what makes them
    /// read as scuffs on the panel rather than as a solid image, so it must survive.
    /// </summary>
    [Fact]
    public void KeepsTheExplicitAlphaAttribute()
    {
        var prints = Frame("NativeHuntsFrameContentPanelHuntStateDecoration");

        Assert.Equal(0.38, prints.Visual!.Texture!.Alpha);
        Assert.True(prints.Visual.Texture.IsDimmed);
        Assert.Equal(0.38, prints.Visual.Texture.EffectiveAlpha);
    }

    /// <summary>
    /// Line 6 of the document: a 326x314 Texture whose only visual statement is a Color. It has no
    /// file because its template supplies one. Discarding it for want of a path would lose the
    /// only thing it actually said.
    /// </summary>
    [Fact]
    public void KeepsAColourOnATextureThatNamesNoFile()
    {
        var tint = AnonymousTexture(1);

        Assert.Equal(FrameKind.TEXTURE, tint.Kind);
        Assert.Null(tint.Visual!.Texture!.File);
        Assert.Equal(new ColorRgba(0.12, 0.07, 0.04, 1), tint.Visual.Texture.Color);
    }

    [Fact]
    public void KeepsTheStatusBarBackgroundTint()
    {
        var background = Frame("Texture#16");

        Assert.Equal(FrameKind.TEXTURE, background.Kind);
        Assert.Equal(new ColorRgba(0.08, 0.08, 0.08, 0.9), background.Visual!.Texture!.Color);
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// Five FontStrings in the document carry a literal <c>text</c> attribute. Those strings are
    /// knowable now and are kept.
    /// </summary>
    [Theory]
    [InlineData("NativeHuntsFrameContentPanelTitle", "Native Hunts")]
    [InlineData("FontString#12", "Player vs. Environment")]
    public void KeepsALiteralFontStringTextAndItsFontReference(string name, string? expectedText)
    {
        var frame = Frame(name);

        Assert.Equal(FrameKind.FONTSTRING, frame.Kind);
        Assert.NotNull(frame.Visual!.Text);
        Assert.Equal(expectedText, frame.Visual.Text.Text);
        Assert.StartsWith("GameFont", frame.Visual.Text.FontTemplate);
    }

    [Fact]
    public void KeepsEveryLiteralStringInTheDocument()
    {
        var literals = Project.Frames
            .Where(f => f.Kind == FrameKind.FONTSTRING)
            .Select(f => f.Visual?.Text)
            .Where(t => t is { HasLiteralText: true })
            .Select(t => t!.Text)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal(
            [
                "Elite Hunts",
                "Elite Today",
                "HUNT RECORD",
                "Huntmaster's Seals",
                "Native Hunts",
                "Player vs. Environment",
                "Standard Hunts",
            ],
            literals);
    }

    /// <summary>
    /// The remaining thirteen FontStrings have no <c>text</c> attribute at all: Lua assigns them at
    /// runtime. Recording those as empty strings would be a lie that looks like a rendering bug,
    /// so they are recorded as needing runtime text instead.
    /// </summary>
    [Fact]
    public void DistinguishesRuntimeTextFromEmptyText()
    {
        var runtime = Project.Frames
            .Where(f => f.Kind == FrameKind.FONTSTRING)
            .Select(f => f.Visual?.Text)
            .Where(t => t is { NeedsRuntimeText: true })
            .ToList();

        Assert.Equal(13, runtime.Count);
        Assert.All(runtime, t => Assert.Null(t!.Text));

        var tier = Frame("NativeHuntsFrameContentPanelIdentityTier").Visual!.Text!;
        Assert.True(tier.NeedsRuntimeText);
        Assert.Equal("GameFontNormalSmall", tier.FontTemplate);
        Assert.Equal("LEFT", tier.JustifyHorizontal);
    }

    [Fact]
    public void KeepsJustificationVerbatimIncludingBothAxes()
    {
        var primary = Frame("NativeHuntsFrameContentPanelHuntStatePrimary").Visual!.Text!;

        Assert.Equal("LEFT", primary.JustifyHorizontal);
        Assert.Equal("TOP", primary.JustifyVertical);
        Assert.True(primary.NeedsRuntimeText);
    }

    [Fact]
    public void KeepsBothButtonLabelsAndTheirTabIds()
    {
        var first = Frame("LFDParentFrameTab1");
        var second = Frame("LFDParentFrameTab2");

        Assert.Equal(FrameKind.BUTTON, first.Kind);
        Assert.Equal("Dungeon Finder", first.Visual!.Text!.Text);
        Assert.Equal(1, first.Visual.Id);
        Assert.Equal("Hunts", second.Visual!.Text!.Text);
        Assert.Equal(2, second.Visual.Id);

        // inherits is kept on the frame, where it belongs. Recording it as a FONT would invent a
        // font reference the document never mentions: CharacterFrameTabButtonTemplate is a frame
        // template, not a typeface.
        Assert.Equal("CharacterFrameTabButtonTemplate", first.Inherits);
        Assert.Null(first.Visual.Text.FontTemplate);
        Assert.Null(second.Visual.Text.FontTemplate);
    }

    // ------------------------------------------------------------------ status bar

    [Fact]
    public void KeepsStatusBarRangeFillAndBarAppearance()
    {
        var bar = Frame("NativeHuntsFrameContentPanelHuntStateProgress");

        Assert.Equal(FrameKind.STATUSBAR, bar.Kind);
        var visual = bar.Visual!.StatusBar!;
        Assert.Equal(0, visual.MinValue);
        Assert.Equal(100, visual.MaxValue);
        Assert.Equal(0, visual.DefaultValue);
        Assert.Equal(@"Interface\TargetingFrame\UI-StatusBar", visual.BarTexture);
        Assert.Equal(new ColorRgba(0.1, 0.75, 0.15, 1), visual.BarColor);
    }

    [Fact]
    public void ReportsAStatusBarFillFractionOnlyWhenTheRangeDefinesOne()
    {
        var visual = Frame("NativeHuntsFrameContentPanelHuntStateProgress").Visual!.StatusBar!;

        Assert.Equal(0, visual.DefaultFraction);

        // The bar's default is 0 of 0..100, so Preview can honestly draw it empty - not full, and
        // not at some invented mid-point.
        Assert.Equal(0, visual.DefaultFraction!.Value, 9);
        Assert.Null((visual with { MinValue = null }).DefaultFraction);
        Assert.Null((visual with { MaxValue = null }).DefaultFraction);
        Assert.Equal(0.5, (visual with { DefaultValue = 50 }).DefaultFraction!.Value, 9);
    }

    /// <summary>
    /// The bar overrides its enclosing Layer with <c>drawLayer="ARTWORK"</c>. WoW treats that as a
    /// concept distinct from the stratum, so folding it into Stratum would lose the fact that the
    /// document asked for something other than what its layer said.
    /// </summary>
    [Fact]
    public void KeepsDrawLayerSeparateFromTheLayerStratum()
    {
        var bar = Frame("NativeHuntsFrameContentPanelHuntStateProgress");

        Assert.Equal("ARTWORK", bar.Visual!.DrawLayer);

        // The bar sits in a <Frames> element with no enclosing <Layer>, so it has no stratum at
        // all. The document's own instruction survives only because drawLayer is stored verbatim;
        // there was nowhere to map it to.
        Assert.Null(bar.Stratum);
    }

    // ------------------------------------------------------------------ scope discipline

    /// <summary>
    /// Every retained fact must come from the file. A frame with no paint attributes in the
    /// document must carry no visual metadata, or "retained" stops meaning "retained".
    /// </summary>
    [Fact]
    public void InventsNothingForFramesTheDocumentDescribesNoPaintFor()
    {
        var window = Frame("NativeHuntsFrame");

        Assert.Equal(FrameKind.FRAME, window.Kind);
        Assert.Null(window.Visual);
    }

    /// <summary>
    /// The stand-in for Blizzard's LFDParentFrame is synthesized by FrameForge, so it has no
    /// visual facts to keep. It must not acquire any by being treated like a real element.
    /// </summary>
    [Fact]
    public void InventsNothingForTheSynthesizedStandIn()
    {
        var standIn = Frame("LFDParentFrame");

        Assert.True(standIn.Placeholder);
        Assert.Null(standIn.Visual);
    }

    [Fact]
    public void DescribesRetainedFactsForTheInspector()
    {
        var description = Frame("NativeHuntsFrameContentPanelHuntStateDecoration").Visual!.Describe();

        Assert.NotNull(description);
        Assert.Contains(@"texture Interface\NativeHunts\hunt_trail_prints.tga", description);
        Assert.Contains("texCoords 0,0.75 / 0,0.75", description);
        Assert.Contains("alpha 0.38", description);
    }

    // ------------------------------------------------------------------ round trip

    /// <summary>
    /// Retention is worthless if the saved project drops it. This is the check that caught an
    /// absent number returning as a declared zero, which would have quietly turned every
    /// undeclared StatusBar maximum into 0.
    /// </summary>
    [Fact]
    public void RetainedFactsSurviveASerializationRoundTrip()
    {
        var json = ProjectCodec.Serialize(Project);
        var parsed = ProjectCodec.Parse(json);

        Assert.Empty(parsed.Errors);
        var restored = parsed.Project!;
        Assert.Equal(Project.Frames, restored.Frames);

        foreach (var original in Project.Frames)
        {
            var again = restored.Frames.First(f => f.Name == original.Name);
            Assert.Equal(original.Visual, again.Visual);
        }
    }

    [Fact]
    public void UndeclaredNumbersStayUndeclaredAcrossARoundTrip()
    {
        var json = ProjectCodec.Serialize(Project);
        var restored = ProjectCodec.Parse(json).Project!;

        // The trail prints declare alpha; the ready icon, sharing the same shape, does not.
        var prints = restored.Frames.First(f => f.Name.EndsWith("Decoration")).Visual!.Texture!;
        var readyIcon = restored.Frames.First(f => f.Name.EndsWith("ReadyIcon")).Visual!.Texture!;

        Assert.Equal(0.38, prints.Alpha);
        Assert.Null(readyIcon.Alpha);
        Assert.Equal(1, readyIcon.EffectiveAlpha);
    }

    [Fact]
    public void AnEmptyVisualIsOmittedEntirely()
    {
        var project = TestProject.Project(TestProject.ParentFrame());
        var json = ProjectCodec.Serialize(project);

        Assert.DoesNotContain("\"visual\"", json);
        Assert.Null(ProjectCodec.Parse(json).Project!.Frames.First().Visual);
    }

    /// <summary>
    /// A hand-authored v0.1 file has no <c>visual</c> key at all and must still load. The new
    /// metadata is additive, not a new requirement on the format.
    /// </summary>
    [Fact]
    public void AProjectWrittenWithoutVisualMetadataStillLoads()
    {
        var legacy = """
        {
          "format": "frameforge-project",
          "version": 1,
          "name": "Legacy",
          "screen": { "width": 1024, "height": 768 },
          "frames": [
            { "name": "A", "parent": null, "width": 100, "height": 50, "point": "CENTER",
              "relativeTo": null, "relativePoint": "CENTER", "offsetX": 0, "offsetY": 0,
              "visible": true }
          ]
        }
        """;

        var parsed = ProjectCodec.Parse(legacy);

        Assert.True(parsed.Ok);
        Assert.Empty(parsed.Errors);
        Assert.Null(parsed.Project!.Frames.Single().Visual);
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// The old note said paint attributes were "ignored". That stopped being true, and leaving it
    /// would understate what the tool knows. What remains a genuine gap is narrower: the facts are
    /// kept, but Preview still substitutes a stand-in because BLP/TGA and Blizzard's fonts are not
    /// reproduced.
    /// </summary>
    [Fact]
    public void ReportsTheGapThatIsLeftRatherThanTheOneThatWasClosed()
    {
        var result = FrameXmlImporter.Import(
            Encoding.UTF8.GetString(ReadFixture()),
            "NativeHuntsFrame.xml");

        Assert.Contains(result.Diagnostics, d => d.Code == FrameXmlDiagnosticCodes.VisualRetainedNotRendered);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "paint-attribute-ignored");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "statusbar-value-ignored");
    }

    [Fact]
    public void GeometrySupportIsUnaffectedByRetainingPaint()
    {
        // Retention is about what the model knows, not about how completely the geometry came
        // through, so the Phase 2 support counts must not move.
        var result = FrameXmlImporter.Import(
            Encoding.UTF8.GetString(ReadFixture()),
            "NativeHuntsFrame.xml");

        Assert.Equal(51, result.ElementsDiscovered);
        Assert.Equal(42, result.Elements.Count(e => e.Support == FrameXmlSupport.Full));
        Assert.Equal(9, result.Elements.Count(e => e.Support == FrameXmlSupport.Partial));
        Assert.Equal(0, result.Elements.Count(e => e.Support == FrameXmlSupport.Unsupported));
    }
}