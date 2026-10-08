using FrameForge.Core.Export;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class LayoutPatchTests
{
    private const string RecordFrame = "NativeHuntsFrameContentPanelRecord";

    private static string FixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "NativeHuntsFrame.xml"));

    private static (string Xml, Project Project) LoadFixture()
    {
        var xml = File.ReadAllText(FixturePath);
        var import = FrameXmlImporter.Import(xml, Path.GetFileName(FixturePath));
        Assert.True(import.Ok, import.SummaryText);
        return (xml, import.Project!);
    }

    private static Project Move(Project project, string name, double? offsetX = null, double? offsetY = null) =>
        project with
        {
            Frames =
            [
                .. project.Frames.Select(frame => frame.Name == name
                    ? frame with { OffsetX = offsetX ?? frame.OffsetX, OffsetY = offsetY ?? frame.OffsetY }
                    : frame),
            ],
        };

    [Fact]
    public void Build_produces_an_empty_patch_when_nothing_changed()
    {
        var (_, project) = LoadFixture();

        var result = LayoutPatchBuilder.Build(project, project);

        Assert.True(result.Success, string.Join(" ", result.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(result.Patch);
        Assert.Empty(result.Patch!.Entries);
    }

    [Fact]
    public void Build_reports_frames_added_removed_and_reordered()
    {
        var baseline = new Project
        {
            Frames = [new FrameDef { Name = "A" }, new FrameDef { Name = "B" }, new FrameDef { Name = "C" }],
        };
        var edited = new Project
        {
            Frames = [new FrameDef { Name = "B" }, new FrameDef { Name = "A" }, new FrameDef { Name = "D" }],
        };

        var result = LayoutPatchBuilder.Build(baseline, edited);

        Assert.False(result.Success);
        Assert.Null(result.Patch);
        Assert.Contains(result.Diagnostics, d => d.Code == "FRAME_REMOVED" && d.FrameName == "C");
        Assert.Contains(result.Diagnostics, d => d.Code == "FRAME_ADDED" && d.FrameName == "D");
        Assert.Contains(result.Diagnostics, d => d.Code == "FRAME_ORDER_UNSUPPORTED");
    }

    [Fact]
    public void Build_rejects_structural_changes_a_layout_patch_cannot_represent()
    {
        var baseline = new Project
        {
            Frames =
            [
                new FrameDef
                {
                    Name = "A",
                    ExtraAnchors = [FrameAnchor.Create(AnchorPoint.BOTTOM, "B", AnchorPoint.TOP, 4, 5)],
                },
                new FrameDef { Name = "B" },
                new FrameDef { Name = "C", SetAllPoints = false },
                new FrameDef { Name = "D", Width = 10 },
            ],
        };
        var edited = new Project
        {
            Frames =
            [
                new FrameDef { Name = "A" },
                new FrameDef { Name = "B", Parent = "A" },
                new FrameDef { Name = "C", SetAllPoints = true },
                new FrameDef { Name = "D", Width = 99 },
            ],
        };

        var result = LayoutPatchBuilder.Build(baseline, edited, new LayoutPatchBuildOptions(
            ProtectedFrames: new HashSet<string>(StringComparer.Ordinal) { "D" }));

        Assert.False(result.Success);
        Assert.Null(result.Patch);
        Assert.Contains(result.Diagnostics, d => d.Code == "EXTRA_ANCHOR_UNSUPPORTED" && d.FrameName == "A");
        Assert.Contains(result.Diagnostics, d => d.Code == "PARENT_CHANGE_UNSUPPORTED" && d.FrameName == "B");
        Assert.Contains(result.Diagnostics, d => d.Code == "SET_ALL_POINTS_UNSUPPORTED" && d.FrameName == "C");
        Assert.Contains(result.Diagnostics, d => d.Code == "PROTECTED_GEOMETRY" && d.FrameName == "D");
    }

    [Fact]
    public void Build_reports_geometry_changes_on_frames_with_no_source_location()
    {
        var baseline = new Project { Frames = [new FrameDef { Name = "Authored", Width = 10 }] };
        var edited = new Project { Frames = [new FrameDef { Name = "Authored", Width = 50 }] };

        var result = LayoutPatchBuilder.Build(baseline, edited);

        Assert.False(result.Success);
        Assert.Null(result.Patch);
        Assert.Contains(result.Diagnostics, d => d.Code == "LOCATION_UNKNOWN" && d.FrameName == "Authored");
    }

    [Fact]
    public void Build_rejects_non_layout_changes_instead_of_silently_ignoring_them()
    {
        var baseline = new Project
        {
            Frames = [new FrameDef { Name = "A", OffsetY = 8, Visible = true }],
        };
        var edited = new Project
        {
            Frames = [new FrameDef { Name = "A", OffsetY = 8, Visible = false }],
        };

        var result = LayoutPatchBuilder.Build(baseline, edited);

        Assert.False(result.Success);
        Assert.Null(result.Patch);
        var error = Assert.Single(result.Diagnostics, d => d.Code == "NON_LAYOUT_CHANGE_UNSUPPORTED");
        Assert.Equal(ExportSeverity.Error, error.Severity);
        Assert.Equal("A", error.FrameName);
    }

    [Fact]
    public void Functional_export_requires_imported_source_identity_and_preserves_unmodified_xml()
    {
        var (xml, imported) = LoadFixture();
        var eligible = imported with
        {
            Source = imported.Source! with { ReferencePath = FixturePath },
        };

        Assert.True(FunctionalLayoutExporter.IsEligible(eligible));
        Assert.False(FunctionalLayoutExporter.IsEligible(ProjectFactory.Blank()));

        var result = FunctionalLayoutExporter.Prepare(eligible, xml);

        Assert.True(result.Success, string.Join(" ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(0, result.ModifiedFrameCount);
        Assert.Equal(xml, result.PatchedXml);
    }

    [Fact]
    public void Preview_metadata_does_not_leak_into_layout_export()
    {
        var (xml, imported) = LoadFixture();
        var edited = imported with
        {
            Source = imported.Source! with { ReferencePath = FixturePath },
            Editor = imported.Editor with { PreviewStateId = "idle" },
        };

        var result = FunctionalLayoutExporter.Prepare(edited, xml);

        Assert.True(result.Success, string.Join(" ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(0, result.ModifiedFrameCount);
        Assert.Equal(xml, result.PatchedXml);
    }

    [Fact]
    public void Functional_export_rejects_source_hash_mismatch()
    {
        var (xml, imported) = LoadFixture();
        var eligible = imported with
        {
            Source = imported.Source! with { ReferencePath = FixturePath },
        };

        var result = FunctionalLayoutExporter.Prepare(eligible, xml + "<!-- drift -->");

        Assert.False(result.Success);
        Assert.Null(result.PatchedXml);
        Assert.Contains(result.Diagnostics, d => d.Code == "SOURCE_HASH_MISMATCH");
    }

    [Fact]
    public void Functional_export_rejects_detectable_inline_lua_geometry_control()
    {
        const string xml = """
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="A"><Size x="10" y="10"/><Anchors><Anchor point="TOPLEFT"><Offset><AbsDimension x="0" y="0"/></Offset></Anchor></Anchors><Scripts><OnShow>self:SetPoint("CENTER");</OnShow></Scripts></Frame>
            </Ui>
            """;
        var imported = FrameXmlImporter.Import(xml, "lua.xml").Project!;
        var eligible = imported with
        {
            Source = imported.Source! with { ReferencePath = "lua.xml" },
            Frames = [imported.Frames[0] with { OffsetX = 12 }],
        };

        var result = FunctionalLayoutExporter.Prepare(eligible, xml);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "LUA_GEOMETRY_CONFLICT" && d.FrameName == "A");
    }

    [Fact]
    public void Build_rejects_ambiguous_identity_and_geometry_on_multiple_anchors()
    {
        var located = new SourceLocation(2, 3);
        var duplicateBaseline = new Project
        {
            Frames = [new FrameDef { Name = "A", SourceLocation = located }, new FrameDef { Name = "A", SourceLocation = located }],
        };
        var duplicate = LayoutPatchBuilder.Build(duplicateBaseline, duplicateBaseline);
        Assert.False(duplicate.Success);
        Assert.Contains(duplicate.Diagnostics, d => d.Code == "DUPLICATE_FRAME_IDENTITY");

        var baseline = new Project
        {
            Frames =
            [
                new FrameDef
                {
                    Name = "B", SourceLocation = located,
                    ExtraAnchors = [FrameAnchor.Create(AnchorPoint.BOTTOM, null, AnchorPoint.BOTTOM, 0, 0)],
                },
            ],
        };
        var edited = baseline with { Frames = [baseline.Frames[0] with { OffsetX = 10 }] };
        var multiple = LayoutPatchBuilder.Build(baseline, edited);
        Assert.False(multiple.Success);
        Assert.Contains(multiple.Diagnostics, d => d.Code == "MULTIPLE_ANCHORS_UNSUPPORTED");
    }

    [Fact]
    public void Apply_moves_the_record_panel_and_changes_only_its_geometry_line()
    {
        var (xml, baseline) = LoadFixture();
        var edited = Move(baseline, RecordFrame, offsetX: 24, offsetY: 48);

        var built = LayoutPatchBuilder.Build(baseline, edited);
        Assert.True(built.Success, string.Join(" ", built.Diagnostics.Select(d => d.Message)));
        var entry = Assert.Single(built.Patch!.Entries);
        Assert.Equal(RecordFrame, entry.FrameName);
        Assert.Equal(71, entry.FrameLocation.Line);
        Assert.True(entry.FrameLocation.Column > 0);
        Assert.Equal(0, entry.Baseline.OffsetX);
        Assert.Equal(8, entry.Baseline.OffsetY);
        Assert.Equal(280, entry.Baseline.Width);
        Assert.Equal(AnchorPoint.BOTTOM, entry.Baseline.Point);
        Assert.Null(entry.Baseline.RelativeTo);
        Assert.Equal(24, entry.Target.OffsetX);
        Assert.Equal(48, entry.Target.OffsetY);

        var applied = LayoutPatchApplier.Apply(xml, built.Patch!);
        Assert.True(applied.Success, string.Join(" ", applied.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(applied.PatchedXml);

        var before = xml.Split('\n');
        var after = applied.PatchedXml!.Split('\n');
        Assert.Equal(before.Length, after.Length);
        var changedIndex = Assert.Single(Enumerable.Range(0, before.Length), i => before[i] != after[i]);
        Assert.Equal(72, changedIndex + 1);
        Assert.Equal(
            before[changedIndex].Replace("x=\"0\" y=\"8\"", "x=\"24\" y=\"48\""),
            after[changedIndex]);
        Assert.Contains("x=\"280\" y=\"86\"", after[changedIndex]);
        Assert.Contains("point=\"BOTTOM\"", after[changedIndex]);

        var roundTrip = FrameXmlImporter.Import(applied.PatchedXml);
        Assert.True(roundTrip.Ok, roundTrip.SummaryText);
        var moved = roundTrip.Project!.Find(RecordFrame)!;
        Assert.Equal(24, moved.OffsetX);
        Assert.Equal(48, moved.OffsetY);
        Assert.Equal(280, moved.Width);
        Assert.Equal(86, moved.Height);
        Assert.Equal(AnchorPoint.BOTTOM, moved.Point);
        Assert.Null(moved.RelativeTo);
        Assert.Contains("name=\"$parentRecord\"", applied.PatchedXml);

        var untouched = roundTrip.Project.Find("LFDParentFrameTab1")!;
        var original = baseline.Find("LFDParentFrameTab1")!;
        Assert.Equal(original.OffsetX, untouched.OffsetX);
        Assert.Equal(original.OffsetY, untouched.OffsetY);
        Assert.Equal(original.Point, untouched.Point);
        Assert.Equal(original.AllAnchors, untouched.AllAnchors);
    }

    [Fact]
    public void Apply_rewrites_size_point_and_offset_attributes()
    {
        const string xml = """
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="A"><Size x="10" y="10"/><Anchors><Anchor point="TOPLEFT"><Offset><AbsDimension x="1" y="2"/></Offset></Anchor></Anchors></Frame>
              <Frame name="B"><Size x="10" y="10"/><Anchors><Anchor point="TOPLEFT"/></Anchors></Frame>
            </Ui>
            """;
        var baseline = FrameXmlImporter.Import(xml, "synthetic.xml").Project!;
        var edited = baseline with
        {
            Frames =
            [
                baseline.Frames[0] with { Width = 150, Point = AnchorPoint.CENTER, OffsetX = 9 },
                baseline.Frames[1],
            ],
        };

        var built = LayoutPatchBuilder.Build(baseline, edited);
        Assert.True(built.Success, string.Join(" ", built.Diagnostics.Select(d => d.Message)));
        var applied = LayoutPatchApplier.Apply(xml, built.Patch!);
        Assert.True(applied.Success, string.Join(" ", applied.Diagnostics.Select(d => d.Message)));

        var lines = applied.PatchedXml!.Split('\n');
        Assert.Contains("x=\"150\" y=\"10\"", lines[1]);
        Assert.Contains("point=\"CENTER\"", lines[1]);
        Assert.Contains("x=\"9\" y=\"2\"", lines[1]);
        Assert.Equal("  <Frame name=\"B\"><Size x=\"10\" y=\"10\"/><Anchors><Anchor point=\"TOPLEFT\"/></Anchors></Frame>", lines[2]);

        var roundTrip = FrameXmlImporter.Import(applied.PatchedXml).Project!;
        Assert.Equal(150, roundTrip.Find("A")!.Width);
        Assert.Equal(AnchorPoint.CENTER, roundTrip.Find("A")!.Point);
        Assert.Equal(9, roundTrip.Find("A")!.OffsetX);
        Assert.Equal(10, roundTrip.Find("B")!.Width);
        Assert.Equal(AnchorPoint.TOPLEFT, roundTrip.Find("B")!.Point);
    }

    [Fact]
    public void Apply_inserts_attributes_the_layout_introduces()
    {
        const string xml = """
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="A"><Size x="10"/><Anchors><Anchor/></Anchors></Frame>
              <Frame name="B"><Size x="10" y="10"/></Frame>
            </Ui>
            """;
        var baseline = FrameXmlImporter.Import(xml, "synthetic.xml").Project!;
        var edited = baseline with
        {
            Frames =
            [
                baseline.Frames[0] with { Height = 20, Point = AnchorPoint.BOTTOM, RelativeTo = "B" },
                baseline.Frames[1],
            ],
        };

        var built = LayoutPatchBuilder.Build(baseline, edited);
        Assert.True(built.Success, string.Join(" ", built.Diagnostics.Select(d => d.Message)));
        var applied = LayoutPatchApplier.Apply(xml, built.Patch!);
        Assert.True(applied.Success, string.Join(" ", applied.Diagnostics.Select(d => d.Message)));

        var lines = applied.PatchedXml!.Split('\n');
        Assert.Contains("x=\"10\" y=\"20\"", lines[1]);
        Assert.Contains("point=\"BOTTOM\"", lines[1]);
        Assert.Contains("relativeTo=\"B\"", lines[1]);
        Assert.Contains("relativePoint=\"TOPLEFT\"", lines[1]);
        Assert.Equal("  <Frame name=\"B\"><Size x=\"10\" y=\"10\"/></Frame>", lines[2]);

        var roundTrip = FrameXmlImporter.Import(applied.PatchedXml).Project!;
        var inserted = roundTrip.Find("A")!;
        Assert.Equal(20, inserted.Height);
        Assert.Equal(AnchorPoint.BOTTOM, inserted.Point);
        Assert.Equal("B", inserted.RelativeTo);
        Assert.Equal(AnchorPoint.TOPLEFT, inserted.RelativePoint);
    }

    [Fact]
    public void Apply_removes_relative_to_when_the_target_inherits_the_parent()
    {
        const string xml = """
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="A"><Size x="10" y="10"/><Anchors><Anchor point="TOP" relativeTo="B"/></Anchors></Frame>
              <Frame name="B"><Size x="10" y="10"/></Frame>
            </Ui>
            """;
        var baseline = FrameXmlImporter.Import(xml, "synthetic.xml").Project!;
        var edited = baseline with
        {
            Frames = [baseline.Frames[0] with { RelativeTo = null }, baseline.Frames[1]],
        };

        var built = LayoutPatchBuilder.Build(baseline, edited);
        Assert.True(built.Success, string.Join(" ", built.Diagnostics.Select(d => d.Message)));
        var applied = LayoutPatchApplier.Apply(xml, built.Patch!);
        Assert.True(applied.Success, string.Join(" ", applied.Diagnostics.Select(d => d.Message)));

        Assert.DoesNotContain("relativeTo", applied.PatchedXml);
        var roundTrip = FrameXmlImporter.Import(applied.PatchedXml!).Project!;
        Assert.Null(roundTrip.Find("A")!.RelativeTo);
        Assert.Equal(AnchorPoint.TOP, roundTrip.Find("A")!.Point);
    }

    [Fact]
    public void Apply_reports_missing_geometry_elements()
    {
        const string xml = """
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="A"/>
            </Ui>
            """;
        var baseline = FrameXmlImporter.Import(xml, "synthetic.xml").Project!;
        var edited = baseline with { Frames = [baseline.Frames[0] with { Width = 50, OffsetX = 12 }] };

        var built = LayoutPatchBuilder.Build(baseline, edited);
        Assert.True(built.Success, string.Join(" ", built.Diagnostics.Select(d => d.Message)));
        var applied = LayoutPatchApplier.Apply(xml, built.Patch!);

        Assert.False(applied.Success);
        Assert.Null(applied.PatchedXml);
        Assert.Contains(applied.Diagnostics, d => d.Code == "GEOMETRY_ELEMENT_MISSING" && d.FrameName == "A");
        Assert.Contains(applied.Diagnostics, d => d.Code == "ANCHOR_ELEMENT_MISSING" && d.FrameName == "A");
    }

    [Fact]
    public void Apply_rejects_a_patch_built_from_a_different_document()
    {
        var (xml, baseline) = LoadFixture();
        var built = LayoutPatchBuilder.Build(baseline, Move(baseline, RecordFrame, offsetY: 48));
        Assert.True(built.Success);

        var shifted = "\n" + xml;
        var applied = LayoutPatchApplier.Apply(shifted, built.Patch!);

        Assert.False(applied.Success);
        Assert.Null(applied.PatchedXml);
        var diagnostic = Assert.Single(applied.Diagnostics, d => d.Severity == ExportSeverity.Error);
        Assert.Equal("STALE_LOCATION", diagnostic.Code);
        Assert.Equal(RecordFrame, diagnostic.FrameName);
    }

    [Fact]
    public void Apply_rejects_a_patch_whose_baseline_no_longer_matches()
    {
        var (xml, baseline) = LoadFixture();
        var built = LayoutPatchBuilder.Build(baseline, Move(baseline, RecordFrame, offsetY: 48));
        Assert.True(built.Success);

        var drifted = xml.Replace("<AbsDimension x=\"0\" y=\"8\"/>", "<AbsDimension x=\"0\" y=\"99\"/>");
        Assert.NotEqual(xml, drifted);
        var applied = LayoutPatchApplier.Apply(drifted, built.Patch!);

        Assert.False(applied.Success);
        Assert.Null(applied.PatchedXml);
        var diagnostic = Assert.Single(applied.Diagnostics, d => d.Severity == ExportSeverity.Error);
        Assert.Equal("STALE_BASELINE", diagnostic.Code);
        Assert.Equal(RecordFrame, diagnostic.FrameName);
    }

    [Fact]
    public void Apply_rejects_unknown_frames_and_malformed_xml()
    {
        var geometry = new LayoutGeometry(10, 10, AnchorPoint.TOPLEFT, null, AnchorPoint.TOPLEFT, 0, 0);
        var unknown = new LayoutPatch([new LayoutPatchEntry("NoSuchFrame", new SourceLocation(1, 2), geometry, geometry)]);
        var missingResult = LayoutPatchApplier.Apply("<Ui/>", unknown);
        Assert.False(missingResult.Success);
        Assert.Null(missingResult.PatchedXml);
        Assert.Contains(missingResult.Diagnostics, d => d.Code == "FRAME_NOT_FOUND" && d.FrameName == "NoSuchFrame");

        var malformedResult = LayoutPatchApplier.Apply("<Ui><Frame></Ui>", unknown);
        Assert.False(malformedResult.Success);
        Assert.Null(malformedResult.PatchedXml);
        Assert.Contains(malformedResult.Diagnostics, d => d.Code == "MALFORMED_XML");
    }

    [Fact]
    public void Apply_with_no_entries_returns_the_document_unchanged()
    {
        var (xml, _) = LoadFixture();

        var applied = LayoutPatchApplier.Apply(xml, new LayoutPatch([]));

        Assert.True(applied.Success);
        Assert.Equal(xml, applied.PatchedXml);
        Assert.Empty(applied.Diagnostics);
    }
}
