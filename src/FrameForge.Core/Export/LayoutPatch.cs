using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FrameForge.Core.Import;
using FrameForge.Core.Models;

namespace FrameForge.Core.Export;

/// <summary>
/// The flattened primary-anchor geometry of one frame: exactly the values a layout-only patch
/// is allowed to change (size, primary anchor point, anchor target, and offsets).
/// </summary>
public sealed record LayoutGeometry(
    double Width,
    double Height,
    AnchorPoint Point,
    string? RelativeTo,
    AnchorPoint RelativePoint,
    double OffsetX,
    double OffsetY)
{
    /// <summary>The geometry a <see cref="FrameDef"/> currently carries.</summary>
    public static LayoutGeometry From(FrameDef frame) =>
        new(frame.Width, frame.Height, frame.Point, frame.RelativeTo, frame.RelativePoint,
            frame.OffsetX, frame.OffsetY);
}

/// <summary>One frame's geometry change, pinned to where that frame lives in the baseline document.</summary>
/// <param name="FrameName">Resolved FrameForge frame name.</param>
/// <param name="FrameLocation">
/// Line and column of the frame's start tag in the baseline document, exactly as the importer
/// recorded it. The applier refuses to touch a document where the frame no longer sits there.
/// </param>
/// <param name="Baseline">Geometry the baseline document must still declare before the patch may apply.</param>
/// <param name="Target">Geometry the patched document must declare afterwards.</param>
public sealed record LayoutPatchEntry(
    string FrameName,
    SourceLocation FrameLocation,
    LayoutGeometry Baseline,
    LayoutGeometry Target);

/// <summary>
/// A minimal, layout-only change set for one FrameXML document: nothing but geometry edits for
/// frames that already exist, each guarded by the location and baseline it was built from.
/// </summary>
public sealed record LayoutPatch(IReadOnlyList<LayoutPatchEntry> Entries);

/// <summary>Knobs for <see cref="LayoutPatchBuilder.Build"/>.</summary>
/// <param name="ProtectedFrames">
/// Frame names whose geometry must never appear in a patch (Lua-owned positioning and similar).
/// A geometry change to any of these is reported as an error rather than silently patched.
/// </param>
public sealed record LayoutPatchBuildOptions(IReadOnlySet<string>? ProtectedFrames = null);

/// <summary>Outcome of building a patch. <see cref="Patch"/> is null when any error was reported.</summary>
public sealed record LayoutPatchBuildResult(
    bool Success,
    LayoutPatch? Patch,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

/// <summary>Outcome of applying a patch. <see cref="PatchedXml"/> is null when any error was reported.</summary>
public sealed record LayoutPatchApplyResult(
    bool Success,
    string? PatchedXml,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

/// <summary>
/// Diffs two projects into a <see cref="LayoutPatch"/> that rewrites only geometry attributes,
/// refusing (with an explicit diagnostic) every difference a layout-only patch cannot represent.
/// </summary>
/// <remarks>
/// The baseline is expected to be an import of the authoritative document and the edited project
/// the same import plus the user's changes. Structural differences - added, removed or reordered
/// frames, parent/kind/setAllPoints/size-reference changes, extra-anchor changes - are errors,
/// because a byte-level patch cannot create, delete or move elements. Non-layout differences
/// (visibility, stratum, level, visual, source identity) are warnings: the patch applies the
/// geometry it can and says out loud what it left behind.
/// </remarks>
public static class LayoutPatchBuilder
{
    private const double Tolerance = 1e-9;

    /// <summary>Builds a layout-only patch from <paramref name="baseline"/> to <paramref name="edited"/>.</summary>
    public static LayoutPatchBuildResult Build(
        Project baseline,
        Project edited,
        LayoutPatchBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);

        var diagnostics = new List<ExportDiagnostic>();
        var protectedFrames = options?.ProtectedFrames;
        var baselineByName = Index(baseline);
        var editedByName = Index(edited);

        foreach (var name in baselineByName.Keys.Except(editedByName.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            diagnostics.Add(new(ExportSeverity.Error, "FRAME_REMOVED",
                $"'{name}' was removed; a layout-only patch cannot remove frames from the document.", name));
        foreach (var name in editedByName.Keys.Except(baselineByName.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            diagnostics.Add(new(ExportSeverity.Error, "FRAME_ADDED",
                $"'{name}' does not exist in the baseline document; a layout-only patch cannot add frames.", name));

        var baselineCommon = baseline.Frames.Select(f => f.Name).Where(editedByName.ContainsKey).ToArray();
        var editedCommon = edited.Frames.Select(f => f.Name).Where(baselineByName.ContainsKey).ToArray();
        if (!baselineCommon.SequenceEqual(editedCommon, StringComparer.Ordinal))
            diagnostics.Add(new(ExportSeverity.Error, "FRAME_ORDER_UNSUPPORTED",
                "Frame order changed; a layout-only patch cannot reorder elements in the document."));

        var entries = new List<LayoutPatchEntry>();
        foreach (var before in baseline.Frames)
        {
            if (!editedByName.TryGetValue(before.Name, out var after))
                continue;

            if (!string.Equals(before.Parent, after.Parent, StringComparison.Ordinal))
                diagnostics.Add(new(ExportSeverity.Error, "PARENT_CHANGE_UNSUPPORTED",
                    "A parent change cannot be expressed by a layout-only patch.", before.Name));
            if (before.Kind != after.Kind)
                diagnostics.Add(new(ExportSeverity.Error, "KIND_CHANGE_UNSUPPORTED",
                    "A widget kind change cannot be expressed by a layout-only patch.", before.Name));
            if (before.SetAllPoints != after.SetAllPoints)
                diagnostics.Add(new(ExportSeverity.Error, "SET_ALL_POINTS_UNSUPPORTED",
                    "Toggling setAllPoints cannot be expressed by a layout-only patch.", before.Name));
            if (before.SizeReferenceOrDefault != after.SizeReferenceOrDefault)
                diagnostics.Add(new(ExportSeverity.Error, "SIZE_REFERENCE_UNSUPPORTED",
                    "Changing what the size is measured against cannot be expressed by a layout-only patch.", before.Name));
            if (!SameAnchors(before.ExtraAnchors, after.ExtraAnchors))
                diagnostics.Add(new(ExportSeverity.Error, "EXTRA_ANCHOR_UNSUPPORTED",
                    "Changing secondary anchors cannot be expressed by a layout-only patch.", before.Name));

            var baselineGeometry = LayoutGeometry.From(before);
            var targetGeometry = LayoutGeometry.From(after);
            if (!SameGeometry(baselineGeometry, targetGeometry))
            {
                if (protectedFrames is not null && protectedFrames.Contains(before.Name))
                    diagnostics.Add(new(ExportSeverity.Error, "PROTECTED_GEOMETRY",
                        "This frame is protected; a layout-only patch refuses to rewrite its geometry.", before.Name));
                else if (before.SourceLocation is null)
                    diagnostics.Add(new(ExportSeverity.Error, "LOCATION_UNKNOWN",
                        "The frame has no location in the baseline document (it is not declared in the source file), " +
                        "so a layout-only patch cannot find it.", before.Name));
                else
                    entries.Add(new(before.Name, before.SourceLocation, baselineGeometry, targetGeometry));
            }

            if (NonLayoutChanged(before, after))
                diagnostics.Add(new(ExportSeverity.Warning, "NON_LAYOUT_CHANGE_IGNORED",
                    "Non-layout changes (visibility, stratum, level, visual, source identity) are not represented " +
                    "in a layout-only patch.", before.Name));
        }

        var success = !diagnostics.Any(d => d.Severity == ExportSeverity.Error);
        return new(success, success ? new LayoutPatch([.. entries]) : null, diagnostics);
    }

    private static Dictionary<string, FrameDef> Index(Project project)
    {
        var byName = new Dictionary<string, FrameDef>(StringComparer.Ordinal);
        foreach (var frame in project.Frames)
            byName[frame.Name] = frame;
        return byName;
    }

    private static bool NonLayoutChanged(FrameDef before, FrameDef after) =>
        before.Visible != after.Visible ||
        before.Stratum != after.Stratum ||
        before.Level != after.Level ||
        !string.Equals(before.SourceName, after.SourceName, StringComparison.Ordinal) ||
        before.Anonymous != after.Anonymous ||
        !string.Equals(before.Inherits, after.Inherits, StringComparison.Ordinal) ||
        before.Placeholder != after.Placeholder ||
        before.Visual != after.Visual;

    private static bool SameGeometry(LayoutGeometry before, LayoutGeometry after) =>
        Nearly(before.Width, after.Width) &&
        Nearly(before.Height, after.Height) &&
        before.Point == after.Point &&
        string.Equals(before.RelativeTo, after.RelativeTo, StringComparison.Ordinal) &&
        before.RelativePoint == after.RelativePoint &&
        Nearly(before.OffsetX, after.OffsetX) &&
        Nearly(before.OffsetY, after.OffsetY);

    private static bool SameAnchors(IReadOnlyList<FrameAnchor> before, IReadOnlyList<FrameAnchor> after)
    {
        if (before.Count != after.Count)
            return false;
        for (var i = 0; i < before.Count; i++)
        {
            if (before[i].Point != after[i].Point ||
                before[i].RelativePoint != after[i].RelativePoint ||
                !string.Equals(before[i].RelativeTo, after[i].RelativeTo, StringComparison.Ordinal) ||
                !Nearly(before[i].OffsetX, after[i].OffsetX) ||
                !Nearly(before[i].OffsetY, after[i].OffsetY))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Nearly(double before, double after) => Math.Abs(before - after) < Tolerance;
}

/// <summary>
/// Applies a <see cref="LayoutPatch"/> to FrameXML text by splicing only the attribute spans it
/// changes, then verifies the result before handing it back.
/// </summary>
/// <remarks>
/// Nothing here writes to disk: <see cref="Apply"/> takes text and returns text, so the decision
/// to write a file - and the path it lands on - stays entirely with the caller.
/// <para>
/// Every entry is checked against the document first: the frame must still sit at the location
/// the patch was built from (<c>STALE_LOCATION</c>) and must still declare the baseline geometry
/// (<c>STALE_BASELINE</c>), so a patch can never be applied to a document that has drifted since
/// it was built. After splicing, the patched text is re-imported and compared with the baseline
/// frame by frame, and every line outside the edited spans must be byte-identical; any drift is
/// reported as <c>VERIFY_FAILED</c> and no patched text is returned.
/// </remarks>
public static class LayoutPatchApplier
{
    private const double Tolerance = 1e-9;

    private readonly record struct TextEdit(int Start, int Length, string Replacement);

    private readonly record struct AttrOp(string Name, string? Value);

    private readonly record struct AttributeSpan(int NameStart, int ValueStart, int ValueEnd, int End, int WhitespaceStart);

    private sealed record LocatedFrame(XElement Element, SourceLocation Location, string? EnclosingName);

    /// <summary>Applies <paramref name="patch"/> to <paramref name="xml"/>, returning verified patched text.</summary>
    public static LayoutPatchApplyResult Apply(string xml, LayoutPatch patch)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(patch);

        var diagnostics = new List<ExportDiagnostic>();
        if (patch.Entries.Count == 0)
            return new(true, xml, diagnostics);

        foreach (var duplicate in patch.Entries.GroupBy(e => e.FrameName, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            diagnostics.Add(new(ExportSeverity.Error, "PATCH_ENTRY_DUPLICATE",
                $"The patch contains more than one entry for '{duplicate.Key}'.", duplicate.Key));
        }

        if (diagnostics.Count > 0)
            return new(false, null, diagnostics);

        XDocument document;
        try
        {
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
            };
            using var reader = XmlReader.Create(new StringReader(xml), readerSettings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return Fail(diagnostics, "MALFORMED_XML",
                $"Line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message.TrimEnd('.')}.");
        }

        if (document.Root is not { } root)
            return Fail(diagnostics, "MALFORMED_XML", "The document has no root element.");

        var lineStarts = BuildLineStarts(xml);
        var frames = LocateFrames(root, lineStarts);
        var edits = new List<TextEdit>();

        foreach (var entry in patch.Entries)
        {
            if (!frames.TryGetValue(entry.FrameName, out var located))
            {
                diagnostics.Add(new(ExportSeverity.Error, "FRAME_NOT_FOUND",
                    $"The document declares no frame named '{entry.FrameName}'.", entry.FrameName));
                continue;
            }

            if (located.Location.Line != entry.FrameLocation.Line ||
                located.Location.Column != entry.FrameLocation.Column)
            {
                diagnostics.Add(new(ExportSeverity.Error, "STALE_LOCATION",
                    $"Frame '{entry.FrameName}' now sits at {located.Location} but the patch expects " +
                    $"{entry.FrameLocation}; the document changed since the patch was built.", entry.FrameName));
                continue;
            }

            var actual = ReadGeometry(located.Element, located.EnclosingName);
            if (!SameGeometry(actual, entry.Baseline))
            {
                diagnostics.Add(new(ExportSeverity.Error, "STALE_BASELINE",
                    $"Frame '{entry.FrameName}' no longer declares the baseline geometry the patch expects; " +
                    "the document changed since the patch was built.", entry.FrameName));
                continue;
            }

            ComputeEdits(located, entry, xml, lineStarts, edits, diagnostics);
        }

        if (diagnostics.Any(d => d.Severity == ExportSeverity.Error))
            return new(false, null, diagnostics);

        edits.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < edits.Count; i++)
        {
            if (edits[i].Start < edits[i - 1].Start + edits[i - 1].Length)
                return Fail(diagnostics, "EDIT_OVERLAP",
                    "Internal error: two layout edits overlap; no text was produced.");
        }

        var builder = new StringBuilder(xml.Length + 16);
        var cursor = 0;
        foreach (var edit in edits)
        {
            builder.Append(xml, cursor, edit.Start - cursor);
            builder.Append(edit.Replacement);
            cursor = edit.Start + edit.Length;
        }

        builder.Append(xml, cursor, xml.Length - cursor);
        var patched = builder.ToString();

        var editedLines = new HashSet<int>();
        foreach (var edit in edits)
        {
            var firstLine = LineOf(lineStarts, edit.Start);
            var lastLine = LineOf(lineStarts, edit.Start + (edit.Length > 0 ? edit.Length - 1 : 0));
            for (var line = firstLine; line <= lastLine; line++)
                editedLines.Add(line);
        }

        var verify = Verify(xml, patched, lineStarts, editedLines, patch);
        diagnostics.AddRange(verify);
        if (verify.Any(d => d.Severity == ExportSeverity.Error))
            return new(false, null, diagnostics);

        return new(true, patched, diagnostics);
    }

    private static LayoutPatchApplyResult Fail(List<ExportDiagnostic> diagnostics, string code, string message)
    {
        diagnostics.Add(new(ExportSeverity.Error, code, message));
        return new(false, null, diagnostics);
    }

    /// <summary>
    /// Decides which attribute spans an entry changes and records one merged edit per element.
    /// </summary>
    private static void ComputeEdits(
        LocatedFrame located,
        LayoutPatchEntry entry,
        string xml,
        int[] lineStarts,
        List<TextEdit> edits,
        List<ExportDiagnostic> diagnostics)
    {
        var frame = located.Element;
        var name = entry.FrameName;
        var before = entry.Baseline;
        var after = entry.Target;

        if (!Nearly(before.Width, after.Width) || !Nearly(before.Height, after.Height))
        {
            if (Child(frame, "Size") is not { } size)
            {
                diagnostics.Add(new(ExportSeverity.Error, "GEOMETRY_ELEMENT_MISSING",
                    $"Frame '{name}' changed size, but the document declares no <Size> element to rewrite.", name));
            }
            else
            {
                var carrier = Child(size, "AbsDimension") ?? size;
                var ops = new List<AttrOp>();
                if (!Nearly(before.Width, after.Width))
                    ops.Add(new("x", N(after.Width)));
                if (!Nearly(before.Height, after.Height))
                    ops.Add(new("y", N(after.Height)));
                TryAddElementEdit(xml, lineStarts, carrier, ops, name, diagnostics, edits);
            }
        }

        var offsetsChanged = !Nearly(before.OffsetX, after.OffsetX) || !Nearly(before.OffsetY, after.OffsetY);
        var anchorAttrsChanged = before.Point != after.Point ||
                                 before.RelativePoint != after.RelativePoint ||
                                 !string.Equals(before.RelativeTo, after.RelativeTo, StringComparison.Ordinal);

        var anchor = FirstAnchor(frame);
        if (anchor is null)
        {
            if (anchorAttrsChanged || offsetsChanged)
                diagnostics.Add(new(ExportSeverity.Error, "ANCHOR_ELEMENT_MISSING",
                    $"Frame '{name}' changed its anchor, but the document declares no <Anchor> element to rewrite.", name));
        }
        else
        {
            var ops = new List<AttrOp>();
            if (before.Point != after.Point)
                ops.Add(new("point", after.Point.ToString()));

            // An omitted relativePoint means "the anchor's own point". When the document omits it,
            // an explicit value is only needed if the patched point would otherwise substitute the
            // wrong one; when the document states one, rewrite it exactly like any other attribute.
            var relativePointNeedsWrite = HasAttribute(anchor, "relativePoint")
                ? before.RelativePoint != after.RelativePoint
                : after.RelativePoint != after.Point;
            if (relativePointNeedsWrite)
                ops.Add(new("relativePoint", after.RelativePoint.ToString()));

            if (!string.Equals(before.RelativeTo, after.RelativeTo, StringComparison.Ordinal))
                ops.Add(new("relativeTo", after.RelativeTo));

            if (ops.Count > 0)
                TryAddElementEdit(xml, lineStarts, anchor, ops, name, diagnostics, edits);

            if (offsetsChanged)
            {
                if (Child(anchor, "Offset") is not { } offset)
                {
                    diagnostics.Add(new(ExportSeverity.Error, "OFFSET_ELEMENT_MISSING",
                        $"Frame '{name}' changed its offset, but the document declares no <Offset> element to rewrite.", name));
                }
                else if (Child(offset, "AbsDimension") is not { } absolute)
                {
                    diagnostics.Add(new(ExportSeverity.Error, Child(offset, "Scale") is null
                            ? "OFFSET_ELEMENT_MISSING"
                            : "OFFSET_SCALE_UNSUPPORTED",
                        Child(offset, "Scale") is null
                            ? $"Frame '{name}' changed its offset, but <Offset> has no <AbsDimension> to rewrite."
                            : $"Frame '{name}' uses <Offset><Scale>, which is a fraction of the target's size; " +
                              "a layout-only patch rewrites absolute offsets only.", name));
                }
                else
                {
                    var offsetOps = new List<AttrOp>();
                    if (!Nearly(before.OffsetX, after.OffsetX))
                        offsetOps.Add(new("x", N(after.OffsetX)));
                    if (!Nearly(before.OffsetY, after.OffsetY))
                        offsetOps.Add(new("y", N(after.OffsetY)));
                    TryAddElementEdit(xml, lineStarts, absolute, offsetOps, name, diagnostics, edits);
                }
            }
        }
    }

    /// <summary>
    /// Rewrites one element's start tag by applying every op to the tag text, then records the
    /// whole tag as a single span edit so multi-attribute changes can never overlap.
    /// </summary>
    private static void TryAddElementEdit(
        string xml,
        int[] lineStarts,
        XElement element,
        List<AttrOp> ops,
        string frameName,
        List<ExportDiagnostic> diagnostics,
        List<TextEdit> edits)
    {
        var start = StartOffset(element, lineStarts);
        var tagEnd = FindTagEnd(xml, start);
        if (tagEnd < 0)
        {
            diagnostics.Add(new(ExportSeverity.Error, "EDIT_TARGET_UNREADABLE",
                $"Frame '{frameName}': the start tag of an element to rewrite could not be read.", frameName));
            return;
        }

        var tag = xml.Substring(start, tagEnd - start + 1);
        var working = tag;
        foreach (var op in ops)
        {
            if (op.Value is null)
            {
                if (!TryRemoveAttribute(ref working, op.Name))
                {
                    diagnostics.Add(new(ExportSeverity.Error, "EDIT_TARGET_UNREADABLE",
                        $"Frame '{frameName}': attribute '{op.Name}' to remove was not found.", frameName));
                    return;
                }
            }
            else if (TryFindAttribute(working, op.Name, out var span))
            {
                working = working[..span.ValueStart] + op.Value + working[span.ValueEnd..];
            }
            else if (!TryInsertAttribute(ref working, op.Name, op.Value))
            {
                diagnostics.Add(new(ExportSeverity.Error, "EDIT_TARGET_UNREADABLE",
                    $"Frame '{frameName}': attribute '{op.Name}' to insert could not be placed.", frameName));
                return;
            }
        }

        if (!string.Equals(working, tag, StringComparison.Ordinal))
            edits.Add(new(start, tag.Length, working));
    }

    /// <summary>
    /// Re-imports the patched text and compares it with the baseline frame by frame, and proves
    /// every line outside the edited spans is byte-identical.
    /// </summary>
    private static IReadOnlyList<ExportDiagnostic> Verify(
        string original,
        string patched,
        int[] lineStarts,
        HashSet<int> editedLines,
        LayoutPatch patch)
    {
        var diagnostics = new List<ExportDiagnostic>();

        var patchedStarts = BuildLineStarts(patched);
        if (patchedStarts.Length != lineStarts.Length)
        {
            diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                $"Patching changed the line count ({lineStarts.Length} to {patchedStarts.Length}); " +
                "a layout-only patch must preserve document structure."));
        }
        else
        {
            for (var i = 0; i < lineStarts.Length; i++)
            {
                if (editedLines.Contains(i + 1))
                    continue;
                var before = original[lineStarts[i]..(i + 1 < lineStarts.Length ? lineStarts[i + 1] : original.Length)];
                var after = patched[patchedStarts[i]..(i + 1 < patchedStarts.Length ? patchedStarts[i + 1] : patched.Length)];
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                        $"Line {i + 1} changed outside the patch's edit set; a layout-only patch must preserve " +
                        "unrelated content."));
                    break;
                }
            }
        }

        var beforeImport = FrameXmlImporter.Import(original);
        if (!beforeImport.Ok || beforeImport.Project is null)
        {
            diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                "The baseline document does not import, so the patch result cannot be verified."));
            return diagnostics;
        }

        var afterImport = FrameXmlImporter.Import(patched);
        if (!afterImport.Ok || afterImport.Project is null)
        {
            diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                "The patched document no longer imports: " +
                (afterImport.Errors.FirstOrDefault()?.Message ?? "unknown error.")));
            return diagnostics;
        }

        var beforeFrames = beforeImport.Project.Frames;
        var afterFrames = afterImport.Project.Frames;
        if (beforeFrames.Count != afterFrames.Count)
        {
            diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                $"The patched document has {afterFrames.Count} frames but the baseline has {beforeFrames.Count}."));
            return diagnostics;
        }

        var targets = new Dictionary<string, LayoutPatchEntry>(StringComparer.Ordinal);
        foreach (var entry in patch.Entries)
            targets[entry.FrameName] = entry;

        for (var i = 0; i < beforeFrames.Count; i++)
        {
            var before = beforeFrames[i];
            var after = afterFrames[i];
            if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
            {
                diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                    $"Frame order or identity changed at position {i} ('{before.Name}' became '{after.Name}')."));
                continue;
            }

            var target = targets.GetValueOrDefault(before.Name);
            if (target is not null && after.SourceLocation?.Line != target.FrameLocation.Line)
            {
                diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                    $"The line number of '{before.Name}' changed.", before.Name));
                continue;
            }

            var expectedGeometry = target is null ? LayoutGeometry.From(before) : target.Target;
            if (!FrameEquals(before, after, expectedGeometry))
                diagnostics.Add(new(ExportSeverity.Error, "VERIFY_FAILED",
                    $"Frame '{before.Name}' does not match the patch's expected result.", before.Name));
        }

        return diagnostics;
    }

    /// <summary>Field-by-field frame comparison, with geometry taken from <paramref name="geometry"/>.</summary>
    private static bool FrameEquals(FrameDef before, FrameDef after, LayoutGeometry geometry)
    {
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal) ||
            !string.Equals(before.Parent, after.Parent, StringComparison.Ordinal) ||
            !Nearly(geometry.Width, after.Width) ||
            !Nearly(geometry.Height, after.Height) ||
            geometry.Point != after.Point ||
            !string.Equals(geometry.RelativeTo, after.RelativeTo, StringComparison.Ordinal) ||
            geometry.RelativePoint != after.RelativePoint ||
            !Nearly(geometry.OffsetX, after.OffsetX) ||
            !Nearly(geometry.OffsetY, after.OffsetY) ||
            before.Visible != after.Visible ||
            before.SizeReference != after.SizeReference ||
            before.Stratum != after.Stratum ||
            before.Level != after.Level ||
            before.Kind != after.Kind ||
            before.SetAllPoints != after.SetAllPoints ||
            !before.ExtraAnchors.SequenceEqual(after.ExtraAnchors) ||
            !string.Equals(before.SourceName, after.SourceName, StringComparison.Ordinal) ||
            before.Anonymous != after.Anonymous ||
            !string.Equals(before.Inherits, after.Inherits, StringComparison.Ordinal) ||
            before.Placeholder != after.Placeholder ||
            before.Visual != after.Visual)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds the resolved-name map the patcher navigates by, mirroring the importer's naming
    /// rules exactly: <c>$parent</c> expansion, top-level renames, duplicate renames, and the
    /// shared anonymous-name counter, in document order.
    /// </summary>
    private static Dictionary<string, LocatedFrame> LocateFrames(XElement root, int[] lineStarts)
    {
        var located = new Dictionary<string, LocatedFrame>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var anonymousCounter = 0;

        string NextAnonymousName(FrameKind kind)
        {
            string candidate;
            do
            {
                candidate = $"{kind.TagName()}#{++anonymousCounter}";
            }
            while (declared.Contains(candidate));

            return candidate;
        }

        void Visit(XElement element, string? enclosingName)
        {
            var localName = element.Name.LocalName;

            if (localName is "Script" or "Include" or "Template")
                return;

            if (localName is "Scripts" or "Layer")
            {
                foreach (var child in element.Elements())
                    Visit(child, enclosingName);
                return;
            }

            if (LayoutKind(localName) is { } kind)
            {
                var rawName = element.Attribute("name")?.Value;
                string name;
                if (string.IsNullOrWhiteSpace(rawName))
                {
                    name = NextAnonymousName(kind);
                }
                else
                {
                    name = ExpandParentName(rawName.Trim(), enclosingName);
                    if (string.Equals(name, FrameXmlImporter.UiParentName, StringComparison.Ordinal))
                        name = NextAnonymousName(kind);
                    if (declared.Contains(name))
                        name = NextAnonymousName(kind);
                }

                declared.Add(name);
                var info = (IXmlLineInfo)element;
                located[name] = new(element, new SourceLocation(info.LineNumber, info.LinePosition), enclosingName);
                foreach (var child in element.Elements())
                    Visit(child, name);
                return;
            }

            if (IsContainer(localName))
            {
                foreach (var child in element.Elements())
                    Visit(child, enclosingName);
                return;
            }

            if (IsNonLayoutData(localName) || localName.StartsWith("On", StringComparison.Ordinal))
                return;

            foreach (var child in element.Elements())
                Visit(child, enclosingName);
        }

        foreach (var child in root.Elements())
            Visit(child, null);

        return located;
    }

    /// <summary>Reads a frame's geometry straight from the raw element, exactly as the importer does.</summary>
    private static LayoutGeometry ReadGeometry(XElement element, string? enclosingName)
    {
        double width = 0, height = 0;
        if (Child(element, "Size") is { } size)
        {
            var dimensions = Child(size, "AbsDimension") ?? size;
            width = ReadNumber(dimensions, "x");
            height = ReadNumber(dimensions, "y");
        }

        if (FirstAnchor(element) is not { } anchor)
            return new(width, height, ProjectFactory.DefaultPoint, null, ProjectFactory.DefaultPoint, 0, 0);

        var pointText = Attr(anchor, "point");
        var point = ParsePoint(pointText, ProjectFactory.DefaultPoint);
        var relativePointText = Attr(anchor, "relativePoint");
        var relativePoint = string.IsNullOrWhiteSpace(relativePointText) ? point : ParsePoint(relativePointText, point);

        double offsetX = 0, offsetY = 0;
        if (Child(anchor, "Offset") is { } offset && Child(offset, "AbsDimension") is { } absolute)
        {
            offsetX = ReadNumber(absolute, "x");
            offsetY = ReadNumber(absolute, "y");
        }

        var relativeTo = NormalizeReference(Attr(anchor, "relativeTo"), enclosingName);
        return new(width, height, point, relativeTo, relativePoint, offsetX, offsetY);
    }

    private static FrameKind? LayoutKind(string localName) => localName switch
    {
        "Frame" => FrameKind.FRAME,
        "Button" => FrameKind.BUTTON,
        "CheckButton" => FrameKind.BUTTON,
        "FontString" => FrameKind.FONTSTRING,
        "Texture" => FrameKind.TEXTURE,
        "StatusBar" => FrameKind.STATUSBAR,
        _ => null,
    };

    private static bool IsContainer(string localName) => localName is
        "Layers" or "Frames" or "Text" or "TitleText" or "HighlightText" or "StatusBarText" or
        "HitRectInsets" or "Dimensions" or "SpecialFrames";

    private static bool IsNonLayoutData(string localName) => localName is
        "Size" or "Anchors" or "Anchor" or "Offset" or "AbsDimension" or "Scale" or "Dimensions"
        or "Color" or "TexCoords" or "BarTexture" or "BarColor" or "Font" or "FontFile" or "FontHeight"
        or "Highlight" or "HighlightColor" or "Disabled" or "DisabledColor" or "Pushed" or "Normal"
        or "HighlightTexture" or "DisabledTexture" or "PushedTexture" or "NormalTexture"
        or "VertexColor" or "DrawLayer" or "TileMode" or "BlinkTime" or "Left" or "Right"
        or "Top" or "Bottom" or "Middle" or "Attributes" or "NormalTexture"
        or "Sequences" or "Sequence" or "Animations" or "Animation" or "Actions" or "Action"
        or "Condition" or "Load" or "Unload";

    private static string ExpandParentName(string raw, string? enclosingName)
    {
        if (!raw.StartsWith("$parent", StringComparison.Ordinal))
            return raw;

        var baseName = enclosingName ?? FrameXmlImporter.UiParentName;
        return baseName + raw["$parent".Length..];
    }

    private static string? NormalizeReference(string? raw, string? enclosingName)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var expanded = ExpandParentName(raw.Trim(), enclosingName);
        return string.Equals(expanded, FrameXmlImporter.UiParentName, StringComparison.Ordinal) ? null : expanded;
    }

    private static AnchorPoint ParsePoint(string? text, AnchorPoint fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        return AnchorPoints.TryParse(text.Trim().ToUpperInvariant(), out var point) ? point : fallback;
    }

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static XElement? FirstAnchor(XElement frame) =>
        Child(frame, "Anchors") is { } anchors
            ? anchors.Elements().FirstOrDefault(e => e.Name.LocalName == "Anchor")
            : null;

    private static string? Attr(XElement element, string name)
    {
        var attribute = element.Attribute(name) ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == name);
        var value = attribute?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool HasAttribute(XElement element, string name) =>
        element.Attributes().Any(a => a.Name.LocalName == name);

    private static double ReadNumber(XElement element, string attribute)
    {
        var text = Attr(element, attribute);
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               double.IsFinite(value)
            ? value
            : 0;
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
                starts.Add(i + 1);
        }

        return [.. starts];
    }

    private static int LineOf(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    /// <summary>Byte offset of an element's <c>&lt;</c>; its name starts one column after that.</summary>
    private static int StartOffset(XElement element, int[] lineStarts)
    {
        var info = (IXmlLineInfo)element;
        return lineStarts[info.LineNumber - 1] + info.LinePosition - 2;
    }

    /// <summary>Index of the <c>&gt;</c> closing the start tag beginning at <paramref name="start"/>, quote-aware.</summary>
    private static int FindTagEnd(string xml, int start)
    {
        var quote = '\0';
        for (var i = start; i < xml.Length; i++)
        {
            var c = xml[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '>')
                return i;
        }

        return -1;
    }

    /// <summary>Finds <paramref name="name"/> in a start tag, reporting the value and removal spans.</summary>
    private static bool TryFindAttribute(string tag, string name, out AttributeSpan span)
    {
        span = default;
        var i = 1;
        while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] is not ('>' or '/'))
            i++;

        while (i < tag.Length)
        {
            var whitespaceStart = i;
            while (i < tag.Length && char.IsWhiteSpace(tag[i]))
                i++;
            if (i >= tag.Length || tag[i] is '>' or '/')
                return false;

            var nameStart = i;
            while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] is not ('=' or '>' or '/'))
                i++;
            var attributeName = tag[nameStart..i];

            while (i < tag.Length && char.IsWhiteSpace(tag[i]))
                i++;
            if (i >= tag.Length)
                return false;

            if (tag[i] != '=')
                continue;

            i++;
            while (i < tag.Length && char.IsWhiteSpace(tag[i]))
                i++;
            if (i >= tag.Length)
                return false;

            if (tag[i] is '"' or '\'')
            {
                var quote = tag[i];
                var valueStart = ++i;
                var close = tag.IndexOf(quote, valueStart);
                if (close < 0)
                    return false;
                if (string.Equals(attributeName, name, StringComparison.Ordinal))
                {
                    span = new(nameStart, valueStart, close, close + 1, whitespaceStart);
                    return true;
                }

                i = close + 1;
            }
            else
            {
                var valueStart = i;
                while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] is not ('>' or '/'))
                    i++;
                if (string.Equals(attributeName, name, StringComparison.Ordinal))
                {
                    span = new(nameStart, valueStart, i, i, whitespaceStart);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Replaces or inserts an attribute; <paramref name="value"/> null removes it.</summary>
    private static bool TryApplyAttribute(string tag, string name, string? value, out string result)
    {
        result = tag;
        if (value is null)
        {
            if (!TryFindAttribute(tag, name, out var span))
                return false;
            var removeStart = span.WhitespaceStart < span.NameStart ? span.WhitespaceStart : span.NameStart;
            result = tag[..removeStart] + tag[span.End..];
            return true;
        }

        if (TryFindAttribute(tag, name, out var existing))
        {
            result = tag[..existing.ValueStart] + value + tag[existing.ValueEnd..];
            return true;
        }

        var close = tag.Length - 1;
        if (close < 0 || tag[close] != '>')
            return false;
        if (close > 0 && tag[close - 1] == '/')
            close--;
        result = tag[..close] + $" {name}=\"{value}\"" + tag[close..];
        return true;
    }

    private static bool TryRemoveAttribute(ref string tag, string name)
    {
        var removed = TryApplyAttribute(tag, name, null, out var result);
        if (removed)
            tag = result;
        return removed;
    }

    private static bool TryInsertAttribute(ref string tag, string name, string value)
    {
        // Attributes this applier inserts are always new, so insert rather than replace.
        if (TryFindAttribute(tag, name, out _))
        {
            tag = TryApplyAttribute(tag, name, value, out var replaced) ? replaced : tag;
            return true;
        }

        var inserted = TryApplyAttribute(tag, name, value, out var result);
        if (inserted)
            tag = result;
        return inserted;
    }

    private static bool SameGeometry(LayoutGeometry before, LayoutGeometry after) =>
        Nearly(before.Width, after.Width) &&
        Nearly(before.Height, after.Height) &&
        before.Point == after.Point &&
        string.Equals(before.RelativeTo, after.RelativeTo, StringComparison.Ordinal) &&
        before.RelativePoint == after.RelativePoint &&
        Nearly(before.OffsetX, after.OffsetX) &&
        Nearly(before.OffsetY, after.OffsetY);

    private static bool Nearly(double before, double after) => Math.Abs(before - after) < Tolerance;

    private static string N(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
}
