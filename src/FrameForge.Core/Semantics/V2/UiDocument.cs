using System.Text.Json.Serialization;
using FrameForge.Core.Models;
using FrameForge.Core.Templates;

namespace FrameForge.Core.Semantics.V2;

/// <summary>A stable identity assigned once and independent of labels or runtime names.</summary>
public readonly record struct SemanticId(string Value)
{
    public static SemanticId New() => new(Guid.NewGuid().ToString("D"));

    public bool IsValid => Guid.TryParseExact(Value, "D", out _);

    public override string ToString() => Value;
}

/// <summary>The only WoW client profile supported by schema v2 milestone 1.</summary>
public sealed record WowTargetProfile
{
    public const string SupportedProduct = "wow-3.3.5a";
    public const int SupportedBuild = 12340;

    public required string Product { get; init; }
    public required int Build { get; init; }

    public static WowTargetProfile Wow335a12340 => new()
    {
        Product = SupportedProduct,
        Build = SupportedBuild,
    };

    [JsonIgnore]
    public bool IsSupported =>
        Product == SupportedProduct && Build == SupportedBuild;
}

public enum UiNodeKind
{
    Frame,
    Texture,
    FontString,
    Button,
    StatusBar,
}

public enum RootSizingKind
{
    FillHost,
    Explicit,
}

/// <summary>How the composition root relates to the module-owned host.</summary>
public sealed record RootSizing
{
    public required RootSizingKind Kind { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public RootAnchor? Anchor { get; init; }

    public static RootSizing FillHost() => new() { Kind = RootSizingKind.FillHost };

    public static RootSizing Explicit(double width, double height, RootAnchor anchor) => new()
    {
        Kind = RootSizingKind.Explicit,
        Width = width,
        Height = height,
        Anchor = anchor,
    };
}

/// <summary>An explicit root-to-host anchor. Its target is always the declared host.</summary>
public sealed record RootAnchor
{
    public required AnchorPoint Point { get; init; }
    public required AnchorPoint RelativePoint { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
}

public sealed record CompositionRoot
{
    public required SemanticId Id { get; init; }
    public required string RuntimeName { get; init; }
    public required string ExternalHostName { get; init; }
    public required double DesignWidth { get; init; }
    public required double DesignHeight { get; init; }
    public required RootSizing Sizing { get; init; }
    public IReadOnlyList<SemanticId> Children { get; init; } = [];
}

public enum OwnerKind
{
    CompositionRoot,
    LocalNode,
}

/// <summary>Structural ownership; never inferred from a nullable name.</summary>
public sealed record OwnerReference
{
    public required OwnerKind Kind { get; init; }
    public required SemanticId Id { get; init; }

    public static OwnerReference Root(SemanticId id) => new() { Kind = OwnerKind.CompositionRoot, Id = id };
    public static OwnerReference Node(SemanticId id) => new() { Kind = OwnerKind.LocalNode, Id = id };
}

public enum AnchorTargetKind
{
    Parent,
    CompositionRoot,
    LocalNode,
    ExternalGlobal,
    Unresolved,
}

/// <summary>An anchor target whose domain can never be mistaken for UIParent.</summary>
public sealed record AnchorTarget
{
    public required AnchorTargetKind Kind { get; init; }
    public SemanticId? NodeId { get; init; }
    public string? GlobalName { get; init; }
    public string? UnresolvedText { get; init; }
    public string? Diagnostic { get; init; }

    public static AnchorTarget Parent() => new() { Kind = AnchorTargetKind.Parent };
    public static AnchorTarget Root() => new() { Kind = AnchorTargetKind.CompositionRoot };
    public static AnchorTarget Local(SemanticId id) => new() { Kind = AnchorTargetKind.LocalNode, NodeId = id };
    public static AnchorTarget External(string name) => new() { Kind = AnchorTargetKind.ExternalGlobal, GlobalName = name };
    public static AnchorTarget Unresolved(string text, string diagnostic) => new()
    {
        Kind = AnchorTargetKind.Unresolved,
        UnresolvedText = text,
        Diagnostic = diagnostic,
    };
}

public sealed record UiAnchor
{
    public required AnchorPoint Point { get; init; }
    public required AnchorTarget Target { get; init; }
    public required AnchorPoint RelativePoint { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
}

public enum FrameStrata
{
    Background,
    Low,
    Medium,
    High,
    Dialog,
    Fullscreen,
    Tooltip,
}

public enum RegionDrawLayer
{
    Background,
    Border,
    Artwork,
    Overlay,
    Highlight,
}

public sealed record UiColor(double Red, double Green, double Blue, double Alpha = 1);

public sealed record FrameProperties
{
    public double? Width { get; init; }
    public double? Height { get; init; }
    public FrameStrata? Strata { get; init; }
    public int? Level { get; init; }
    public bool? Visible { get; init; }
}

public sealed record RegionProperties
{
    public double? Width { get; init; }
    public double? Height { get; init; }
    public RegionDrawLayer? DrawLayer { get; init; }
    public int? Sublevel { get; init; }
    public UiColor? Tint { get; init; }
}

public sealed record TextureProperties
{
    public string? TextureReference { get; init; }
}

public sealed record FontStringProperties
{
    public string? FontReference { get; init; }
    public string? Text { get; init; }
}

public sealed record ButtonProperties
{
    public bool? Enabled { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
}

public sealed record StatusBarProperties
{
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? Value { get; init; }
    public string? TextureReference { get; init; }
}

/// <summary>Only values directly authored in the v2 project.</summary>
public sealed record AuthoredProperties
{
    public FrameProperties? Frame { get; init; }
    public RegionProperties? Region { get; init; }
    public TextureProperties? Texture { get; init; }
    public FontStringProperties? FontString { get; init; }
    public ButtonProperties? Button { get; init; }
    public StatusBarProperties? StatusBar { get; init; }
}

/// <summary>
/// A future template resolver may produce this separate view. It is deliberately not stored
/// in <see cref="UiDocument"/>, so inherited values can never overwrite authored state.
/// </summary>
public enum EffectivePropertyOrigin
{
    Authored,
    TemplateDeclared,
    TemplateInherited,
    Unresolved,
}

public sealed record EffectiveNodeProperties(
    AuthoredProperties Values,
    string? TemplateIdentity,
    BlizzardButtonProperties? TemplateValues,
    IReadOnlyDictionary<string, EffectivePropertyOrigin> Provenance,
    IReadOnlyList<BlizzardAssetDependency> UnresolvedDependencies,
    IReadOnlyList<BlizzardKnownPreviewBehavior> PreviewBehaviors,
    IReadOnlyList<BlizzardTemplateDiagnostic> Diagnostics)
{
    [JsonIgnore]
    public bool IsResolved => Diagnostics.All(item =>
        item.Severity != BlizzardTemplateDiagnosticSeverity.Error) &&
        UnresolvedDependencies.Count == 0;
}

public sealed record NodeEditorMetadata
{
    public bool Collapsed { get; init; }
    public bool Locked { get; init; }
    public string? Notes { get; init; }
}

public sealed record UiNode
{
    public required SemanticId Id { get; init; }
    public required UiNodeKind Kind { get; init; }
    public string? RuntimeName { get; init; }
    public required string DisplayLabel { get; init; }
    public required OwnerReference Owner { get; init; }
    public IReadOnlyList<SemanticId> Children { get; init; } = [];
    public IReadOnlyList<UiAnchor> Anchors { get; init; } = [];
    public required AuthoredProperties AuthoredProperties { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlizzardTemplate { get; init; }

    public NodeEditorMetadata? Editor { get; init; }

    [JsonIgnore]
    public bool IsRegion => Kind is UiNodeKind.Texture or UiNodeKind.FontString;

    [JsonIgnore]
    public bool CanOwnChildren => !IsRegion;
}

public sealed record ExternalReference
{
    public required string GlobalName { get; init; }
    public string? ExpectedSource { get; init; }
    public string? Description { get; init; }
}

public sealed record DocumentEditorMetadata
{
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public enum DiagnosticSeverity
{
    Error,
    Warning,
}

public sealed record UiDiagnostic
{
    public required string Code { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public SemanticId? NodeId { get; init; }
    public string? PropertyPath { get; init; }
}

/// <summary>A schema-v2 authoring document. Schema v1 remains a separate model.</summary>
public sealed record UiDocument
{
    public const string FormatId = "frameforge-ui-document";
    public const int SchemaVersion = 2;

    public int Version { get; init; } = SchemaVersion;
    public required WowTargetProfile Target { get; init; }
    public required SemanticId DocumentId { get; init; }

    // A collection intentionally permits invalid in-memory documents to receive precise
    // missing/multiple-root diagnostics. A valid schema-v2 document contains exactly one.
    public IReadOnlyList<CompositionRoot> CompositionRoots { get; init; } = [];
    public IReadOnlyList<UiNode> Nodes { get; init; } = [];
    public IReadOnlyList<ExternalReference> ExternalReferences { get; init; } = [];
    public DocumentEditorMetadata? Editor { get; init; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; init; } = [];
}
