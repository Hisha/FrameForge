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

        // Association establishes only the generic composition boundary. Runtime values and
        // state changes belong to the consuming module, which can address exported controls by
        // the identities in the manifest. Older projects may retain explicit probes/mirrors, but
        // FrameForge must not infer application behavior while associating a new source.
        IReadOnlyList<FunctionalStateBinding> stateBindings = [];
        if (design.Editor.DesignStates.Count > 0)
            diagnostics.Add(new(ExportSeverity.Warning, "FUNCTIONAL_STATES_MODULE_MANAGED",
                $"{design.Editor.DesignStates.Count} design state(s) will be exported without inferred source probes. The consuming module owns runtime visibility."));
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

}
