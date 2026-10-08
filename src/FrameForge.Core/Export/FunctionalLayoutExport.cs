using FrameForge.Core.Import;
using FrameForge.Core.Models;
using System.Xml;
using System.Xml.Linq;

namespace FrameForge.Core.Export;

/// <summary>Result of preparing a layout-only export without writing either source or destination.</summary>
public sealed record FunctionalLayoutExportResult(
    bool Success,
    string? PatchedXml,
    int ModifiedFrameCount,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

/// <summary>
/// Reconstructs the authoritative import baseline, verifies its identity, builds the Phase 1
/// patch, and applies it in memory. Callers may write <see cref="FunctionalLayoutExportResult.PatchedXml"/>
/// only after a successful result.
/// </summary>
public static class FunctionalLayoutExporter
{
    public static bool IsEligible(Project project) =>
        project.Source is { IsReadOnlyXml: true, ReferencePath.Length: > 0, Sha256.Length: > 0 };

    public static FunctionalLayoutExportResult Prepare(Project edited, string sourceXml)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(sourceXml);

        var diagnostics = new List<ExportDiagnostic>();
        if (!IsEligible(edited))
            return Failure("SOURCE_INELIGIBLE",
                "This project is not an imported FrameXML project with a preserved source path and hash.");

        var actualHash = FrameXmlImporter.ComputeSha256(sourceXml);
        if (!string.Equals(actualHash, edited.Source!.Sha256, StringComparison.OrdinalIgnoreCase))
            return Failure("SOURCE_HASH_MISMATCH",
                "The source XML changed since import. Reopen the current source and reapply the edit before exporting.");

        var import = FrameXmlImporter.Import(sourceXml, edited.Source.FileName);
        if (!import.Ok || import.Project is null)
            return Failure("SOURCE_REIMPORT_FAILED",
                $"The source XML could not be reimported: {string.Join(" ", import.Errors.Select(error => error.Message))}");

        var build = LayoutPatchBuilder.Build(import.Project, edited);
        diagnostics.AddRange(build.Diagnostics);
        if (!build.Success || build.Patch is null)
            return new(false, null, 0, diagnostics);

        diagnostics.AddRange(FindInlineLuaGeometryConflicts(sourceXml, build.Patch));
        if (diagnostics.Any(item => item.Severity == ExportSeverity.Error))
            return new(false, null, 0, diagnostics);

        var apply = LayoutPatchApplier.Apply(sourceXml, build.Patch);
        diagnostics.AddRange(apply.Diagnostics);
        if (!apply.Success || apply.PatchedXml is null)
            return new(false, null, 0, diagnostics);

        return new(true, apply.PatchedXml, build.Patch.Entries.Count, diagnostics);

        FunctionalLayoutExportResult Failure(string code, string message)
        {
            diagnostics.Add(new(ExportSeverity.Error, code, message));
            return new(false, null, 0, diagnostics);
        }
    }

    /// <summary>
    /// Detects geometry setters in an edited frame's inline Scripts block. External Lua cannot be
    /// proven from a single XML file, but an explicit self/this or resolved-name setter here is a
    /// concrete conflict and is rejected rather than pretending the XML will win at runtime.
    /// </summary>
    private static IReadOnlyList<ExportDiagnostic> FindInlineLuaGeometryConflicts(string xml, LayoutPatch patch)
    {
        if (patch.Entries.Count == 0 || !xml.Contains("Set", StringComparison.Ordinal))
            return [];

        XDocument document;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), settings))
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);

        var diagnostics = new List<ExportDiagnostic>();
        foreach (var entry in patch.Entries)
        {
            var element = document.Descendants().FirstOrDefault(candidate =>
            {
                var info = (IXmlLineInfo)candidate;
                return info.HasLineInfo() && info.LineNumber == entry.FrameLocation.Line && info.LinePosition == entry.FrameLocation.Column;
            });
            var scripts = element?.Elements().FirstOrDefault(child => child.Name.LocalName == "Scripts")?.Value ?? string.Empty;
            var namedSetters = new[] { "SetPoint", "SetSize", "SetWidth", "SetHeight", "SetAllPoints" }
                .Select(method => $"{entry.FrameName}:{method}");
            var inlineSetter = scripts.Contains("self:SetPoint", StringComparison.Ordinal)
                               || scripts.Contains("self:SetSize", StringComparison.Ordinal)
                               || scripts.Contains("self:SetWidth", StringComparison.Ordinal)
                               || scripts.Contains("self:SetHeight", StringComparison.Ordinal)
                               || scripts.Contains("self:SetAllPoints", StringComparison.Ordinal)
                               || scripts.Contains("this:SetPoint", StringComparison.Ordinal)
                               || scripts.Contains("this:SetSize", StringComparison.Ordinal)
                               || namedSetters.Any(setter => scripts.Contains(setter, StringComparison.Ordinal)
                                                            || xml.Contains(setter, StringComparison.Ordinal));
            if (inlineSetter)
                diagnostics.Add(new(ExportSeverity.Error, "LUA_GEOMETRY_CONFLICT",
                    "Inline Lua sets this frame's geometry, so an XML-only edit may be overwritten at runtime.", entry.FrameName));
        }

        return diagnostics;
    }
}
