using System.Security.Cryptography;
using System.Text;
using FrameForge.Core.Import;
using FrameForge.Core.Models;

namespace FrameForge.Core.Export;

public sealed record FunctionalAssociationResult(
    bool Success,
    FunctionalExportProfile? Profile,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

/// <summary>Builds a conservative first association without changing the authored design.</summary>
public static class FunctionalExportAssociator
{
    public static FunctionalAssociationResult Associate(
        Project design, string sourceXml, string sourcePath, string? projectPath)
    {
        var diagnostics = new List<ExportDiagnostic>();
        var imported = FrameXmlImporter.Import(sourceXml, Path.GetFileName(sourcePath));
        if (!imported.Ok || imported.Project is null)
            return Fail("FUNCTIONAL_SOURCE_INVALID", "The selected FrameXML could not be imported: " + imported.SummaryText);

        var functional = imported.Project;
        var hosts = functional.Frames.Where(frame => frame.Kind == FrameKind.FRAME && frame.SetAllPoints && !frame.Placeholder).ToArray();
        if (hosts.Length != 1)
            return Fail("FUNCTIONAL_HOST_AMBIGUOUS",
                hosts.Length == 0
                    ? "No concrete setAllPoints Frame was found to host the design."
                    : $"More than one possible functional host was found ({string.Join(", ", hosts.Select(item => item.Name))}).");

        var stateBindings = SuggestStates(design, functional, diagnostics);
        var valueCandidates = design.Editor.DesignObjects
            .Where(item => !item.RuntimeValueRequired && design.Find(item.FrameName) is { } frame
                && (frame.Kind is FrameKind.FONTSTRING or FrameKind.STATUSBAR)
                && (string.IsNullOrWhiteSpace(item.TextOverride ?? frame.Visual?.Text?.Text)
                    || (item.TextOverride ?? frame.Visual?.Text?.Text) is "0" or "Text"))
            .Select(item => item.DisplayName ?? item.FrameName).ToArray();
        if (valueCandidates.Length > 0)
            diagnostics.Add(new(ExportSeverity.Warning, "FUNCTIONAL_VALUES_UNDECLARED",
                $"{valueCandidates.Length} value-like DESIGN object(s) still need explicit Runtime value and XML source mappings: {string.Join(", ", valueCandidates.Take(8))}{(valueCandidates.Length > 8 ? ", …" : string.Empty)}."));
        var reference = Path.GetFileName(sourcePath);
        if (!string.IsNullOrWhiteSpace(projectPath))
            reference = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, Path.GetFullPath(sourcePath));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceXml)));
        var profile = new FunctionalExportProfile
        {
            Source = new ProjectSource
            {
                Type = SourceTypes.WowFrameXml,
                FileName = Path.GetFileName(sourcePath),
                ReferencePath = reference.Replace('\\', '/'),
                ReadOnly = true,
                Sha256 = hash,
            },
            HostFrameName = hosts[0].Name,
            States = stateBindings,
        };
        return new(!diagnostics.Any(item => item.Severity == ExportSeverity.Error), profile, diagnostics);

        FunctionalAssociationResult Fail(string code, string message)
        {
            diagnostics.Add(new(ExportSeverity.Error, code, message));
            return new(false, null, diagnostics);
        }
    }

    private static IReadOnlyList<FunctionalStateBinding> SuggestStates(
        Project design, Project source, List<ExportDiagnostic> diagnostics)
    {
        var result = new List<FunctionalStateBinding>();
        var rank = design.Editor.EffectiveDesignOrder(design)
            .Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        var headerCandidates = source.Frames.Where(frame => frame.Kind == FrameKind.FONTSTRING
            && (frame.SourceName ?? frame.Name).Contains("Header", StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (var state in design.Editor.DesignStates)
        {
            var visibilityFrame = source.Frames.FirstOrDefault(frame => frame.Kind == FrameKind.FRAME
                && (frame.SourceName ?? frame.Name).Contains(state.Name, StringComparison.OrdinalIgnoreCase));
            if (visibilityFrame is not null)
            {
                result.Add(new FunctionalStateBinding
                {
                    StateId = state.Id,
                    SourceFrameName = visibilityFrame.Name,
                    Visible = true,
                });
                continue;
            }

            var label = design.Editor.DesignObjects
                .Where(item => item.StateIds.Count == 1 && item.StateIds[0] == state.Id)
                .Select(item => (Metadata: item, Frame: design.Find(item.FrameName)))
                .Where(item => item.Frame?.Kind == FrameKind.FONTSTRING)
                .OrderBy(item => rank.GetValueOrDefault(item.Metadata.FrameName, int.MaxValue))
                .Select(item => item.Metadata.TextOverride ?? item.Frame!.Visual?.Text?.Text)
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (headerCandidates.Length == 1 && label is not null)
            {
                result.Add(new FunctionalStateBinding
                {
                    StateId = state.Id,
                    SourceFrameName = headerCandidates[0].Name,
                    TextEquals = label,
                });
                continue;
            }

            diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_STATE_UNMAPPED",
                $"Could not safely infer a functional runtime probe for design state '{state.Name}'."));
        }
        return result;
    }
}
