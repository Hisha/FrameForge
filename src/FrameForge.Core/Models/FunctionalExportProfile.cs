namespace FrameForge.Core.Models;

/// <summary>
/// Persistent association between an authored DESIGN project and an authoritative functional
/// FrameXML document. The source remains external and read-only; this record stores only the
/// identity contract needed to compose the design into a verified copy during export.
/// </summary>
public sealed record FunctionalExportProfile
{
    public required ProjectSource Source { get; init; }
    /// <summary>Existing source frame that owns the generated design composition.</summary>
    public required string HostFrameName { get; init; }
    /// <summary>Optional legacy bridge probes. Empty means the consuming module owns state visibility.</summary>
    public IReadOnlyList<FunctionalStateBinding> States { get; init; } = [];
    /// <summary>Optional legacy mirrors. Empty means the consuming module populates exported controls.</summary>
    public IReadOnlyList<FunctionalValueBinding> Values { get; init; } = [];
}

/// <summary>A generic state probe evaluated by the generated inline FrameXML bridge.</summary>
public sealed record FunctionalStateBinding
{
    public required string StateId { get; init; }
    public required string SourceFrameName { get; init; }
    /// <summary>Optional exact, case-insensitive GetText() match.</summary>
    public string? TextEquals { get; init; }
    /// <summary>Optional IsShown() requirement. Null means visibility is not part of the probe.</summary>
    public bool? Visible { get; init; }
}

/// <summary>Mirrors one functional source control into one generated DESIGN object.</summary>
public sealed record FunctionalValueBinding
{
    public required string DesignFrameName { get; init; }
    public required string SourceFrameName { get; init; }
}
