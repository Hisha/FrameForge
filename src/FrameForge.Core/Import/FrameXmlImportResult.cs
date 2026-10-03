using FrameForge.Core.Geometry;
using FrameForge.Core.Models;

namespace FrameForge.Core.Import;

/// <summary>Knobs for <see cref="FrameXmlImporter"/>. Every default is a documented decision.</summary>
public sealed record FrameXmlImportOptions
{
    /// <summary>A FrameXML importer with the documented defaults.</summary>
    public static FrameXmlImportOptions Default { get; } = new();

    /// <summary>UIParent the layout is imported against. 3.3.5a XML is authored at 1024x768.</summary>
    public Screen Screen { get; init; } = Screen.Default;

    /// <summary>
    /// Size given to a synthesized stand-in for a frame the document references but does not
    /// define (typically a Blizzard frame such as <c>LFDParentFrame</c>).
    /// </summary>
    /// <remarks>
    /// There is no correct answer available at import time: the real size comes from another
    /// XML file, or from Lua, at runtime. Rather than dropping the whole subtree - which is
    /// what leaving the reference dangling would do to the preview - FrameForge inserts a
    /// clearly-marked placeholder so the layout stays inspectable, and tells the user its
    /// bounds are a guess they can drag. The default matches the window FrameForge's own
    /// Native Hunts example is modelled on; nothing about it is specific to Native Hunts.
    /// </remarks>
    public Screen ExternalFrameSize { get; init; } = new(355, 500);

    /// <summary>
    /// Where a synthesized placeholder is placed when it has no anchor of its own. It is a
    /// root frame, so this is its own absolute top-left in model space.
    /// </summary>
    public ModelPoint ExternalFramePosition { get; init; } = ModelPoint.Origin;

    /// <summary>
    /// When true, an unresolved reference produces a placeholder so the subtree survives.
    /// When false the reference is left dangling and the layout engine reports it.
    /// </summary>
    public bool CreateExternalFramePlaceholders { get; init; } = true;
}

/// <summary>How well one source element's layout came through.</summary>
public enum FrameXmlSupport
{
    /// <summary>Geometry is exactly what the source declared.</summary>
    Full,

    /// <summary>Geometry is present but something about it is unknown or assumed.</summary>
    Partial,

    /// <summary>No layout could be represented for this element.</summary>
    Unsupported,
}

/// <summary>Per-element import tally, for the summary.</summary>
/// <param name="Frame">The FrameForge frame name the element produced.</param>
/// <param name="Kind">The FrameXML widget the element was.</param>
/// <param name="Support">How completely its layout came through.</param>
public readonly record struct FrameXmlElementSummary(string Frame, FrameKind Kind, FrameXmlSupport Support);

/// <summary>
/// Everything an import produced: the FrameForge project, and an honest account of what the
/// importer could and could not carry across.
/// </summary>
public sealed record FrameXmlImportResult
{
    /// <summary>The imported project, or null when the document could not be parsed at all.</summary>
    public Project? Project { get; init; }

    /// <summary>File name of the source, for display.</summary>
    public string SourceFileName { get; init; } = string.Empty;

    /// <summary>Every finding, in the order it was discovered.</summary>
    public IReadOnlyList<FrameXmlDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>One entry per layout element found, with its support level.</summary>
    public IReadOnlyList<FrameXmlElementSummary> Elements { get; init; } = [];

    /// <summary>Layout-bearing elements in the source: Frame, Button, FontString, Texture, StatusBar, ...</summary>
    public int ElementsDiscovered => Elements.Count;

    public int FullySupported => Elements.Count(e => e.Support == FrameXmlSupport.Full);

    public int PartiallySupported => Elements.Count(e => e.Support == FrameXmlSupport.Partial);

    public int Unsupported => Elements.Count(e => e.Support == FrameXmlSupport.Unsupported);

    /// <summary>True when at least one layout-affecting construct was not fully represented.</summary>
    public bool IsComplete => PartiallySupported == 0 && Unsupported == 0;

    public IReadOnlyList<FrameXmlDiagnostic> Warnings =>
        [.. Diagnostics.Where(d => d.Severity == FrameXmlSeverity.Warning)];

    public IReadOnlyList<FrameXmlDiagnostic> Errors =>
        [.. Diagnostics.Where(d => d.Severity == FrameXmlSeverity.Error)];

    public IReadOnlyList<FrameXmlDiagnostic> Infos =>
        [.. Diagnostics.Where(d => d.Severity == FrameXmlSeverity.Info)];

    /// <summary>True when the document produced a usable project.</summary>
    public bool Ok => Project is not null;

    /// <summary>
    /// The headline the summary shows. The counts deliberately do not add up to "all good" just
    /// because the file opened: a partial count is what stops a preview from looking final.
    /// </summary>
    public string SummaryText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(SourceFileName) ? "the FrameXML document" : SourceFileName;
            if (!Ok)
                return $"Import of {name} failed: {Errors.FirstOrDefault()?.Message ?? "unknown error."}";

            return $"Import completed: {ElementsDiscovered} visual/layout elements discovered from {name}, " +
                   $"{FullySupported} fully supported, {PartiallySupported} partially supported, " +
                   $"{Unsupported} unsupported.";
        }
    }

    /// <summary>Diagnostics joined for a compact status line.</summary>
    public string WarningSummary =>
        Warnings.Count == 0
            ? "No warnings."
            : $"{Warnings.Count} warning(s): {string.Join("  ", Warnings.Take(3).Select(w => w.Heading))}" +
              (Warnings.Count > 3 ? $" (+{Warnings.Count - 3} more in the import summary)" : string.Empty);
}
