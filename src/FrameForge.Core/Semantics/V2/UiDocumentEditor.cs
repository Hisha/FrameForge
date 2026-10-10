using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Templates;

namespace FrameForge.Core.Semantics.V2;

public sealed record SemanticEditResult
{
    public required UiDocument Document { get; init; }
    public required IReadOnlyList<UiDiagnostic> Diagnostics { get; init; }
    public SemanticId? AffectedId { get; init; }
    public bool Changed { get; init; }
    public bool Success => Changed && Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
    public string ErrorText => string.Join(" ", Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
}

/// <summary>
/// The single mutation boundary for schema-v2 documents. Every candidate graph is validated
/// before it is returned; failed operations return the original document unchanged.
/// </summary>
public sealed partial class UiDocumentEditor
{
    private readonly BlizzardTemplateRegistry? _templates;

    public UiDocumentEditor(BlizzardTemplateRegistry? templates = null) => _templates = templates;

    public SemanticEditResult CreateControl(UiDocument document, UiNodeKind kind, OwnerReference owner,
        string displayLabel, string? runtimeName = null)
    {
        if (!TryGetContainer(document, owner, out var error))
            return Failure(document, "FFV2-EDIT-OWNER", error!);
        if (owner.Kind == OwnerKind.LocalNode && document.Nodes.FirstOrDefault(node => node.Id == owner.Id)?.Editor?.ReferenceOnly == true)
            return Failure(document, "FFV2-EDIT-REFERENCE", "Authored controls cannot be parented beneath a Blizzard reference composition.", owner.Id);
        if (OwnerIsLocked(document, owner))
            return Failure(document, "FFV2-EDIT-LOCKED", "The owner is locked; its child list cannot be changed.", owner.Id);

        var id = SemanticId.New();
        var node = new UiNode
        {
            Id = id,
            Kind = kind,
            RuntimeName = runtimeName,
            DisplayLabel = string.IsNullOrWhiteSpace(displayLabel) ? kind.ToString() : displayLabel.Trim(),
            Owner = owner,
            Anchors =
            [
                new UiAnchor
                {
                    Point = AnchorPoint.CENTER,
                    RelativePoint = AnchorPoint.CENTER,
                    Target = AnchorTarget.Parent(),
                },
            ],
            AuthoredProperties = DefaultProperties(kind),
        };
        var candidate = AddChild(document with { Nodes = [.. document.Nodes, node] }, owner, id);
        return Complete(document, candidate, id);
    }

    public SemanticEditResult DeleteControl(UiDocument document, SemanticId id)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (node.Editor?.ReferenceOnly == true)
            return Failure(document, "FFV2-EDIT-REFERENCE", "Reference-only elements cannot be deleted; delete or restore the editor reference composition instead.", id);

        var locked = Descendants(document, id).Append(id)
            .Select(lockedId => document.Nodes.First(item => item.Id == lockedId))
            .FirstOrDefault(item => IsLocked(document, item));
        if (locked is not null)
            return Failure(document, "FFV2-EDIT-LOCKED", $"'{locked.DisplayLabel}' is locked and prevents deletion of this subtree.", locked.Id);
        if (OwnerIsLocked(document, node.Owner))
            return Failure(document, "FFV2-EDIT-LOCKED", "The owner is locked; its child list cannot be changed.", node.Owner.Id);

        var removed = Descendants(document, id).Append(id).ToHashSet();
        var survivorReference = document.Nodes.FirstOrDefault(item => !removed.Contains(item.Id) &&
            item.Anchors.Any(anchor => anchor.Target.Kind == AnchorTargetKind.LocalNode &&
                                       anchor.Target.NodeId is { } target && removed.Contains(target)));
        if (survivorReference is not null)
            return Failure(document, "FFV2-EDIT-REFERENCE",
                $"Cannot delete '{node.DisplayLabel}' because '{survivorReference.DisplayLabel}' anchors to its subtree.", id);

        var candidate = RemoveChild(document, node.Owner, id) with
        {
            Nodes = [.. document.Nodes.Where(item => !removed.Contains(item.Id))],
        };
        return Complete(document, candidate, id);
    }

    public SemanticEditResult RenameControl(UiDocument document, SemanticId id, string displayLabel,
        string? runtimeName)
    {
        if (string.IsNullOrWhiteSpace(displayLabel))
            return Failure(document, "FFV2-EDIT-LABEL", "Display label cannot be empty.", id);
        return UpdateNode(document, id, node => node with
        {
            DisplayLabel = displayLabel.Trim(),
            RuntimeName = string.IsNullOrWhiteSpace(runtimeName) ? null : runtimeName.Trim(),
        });
    }

    public SemanticEditResult ChangeOwnership(UiDocument document, SemanticId id, OwnerReference newOwner,
        bool preserveVisualPosition)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (IsLocked(document, node))
            return Failure(document, "FFV2-EDIT-LOCKED", $"'{node.DisplayLabel}' is locked.", id);
        if (!TryGetContainer(document, newOwner, out var error))
            return Failure(document, "FFV2-EDIT-OWNER", error!, id);
        var newOwnerNode = newOwner.Kind == OwnerKind.LocalNode
            ? document.Nodes.FirstOrDefault(item => item.Id == newOwner.Id)
            : null;
        if (newOwnerNode?.Editor?.ReferenceOnly == true && node.Editor?.ReferenceCompositionId != newOwnerNode.Editor.ReferenceCompositionId)
            return Failure(document, "FFV2-EDIT-REFERENCE", "Authored controls cannot be moved into a Blizzard reference composition.", id);
        if (newOwner.Kind == OwnerKind.LocalNode && (newOwner.Id == id || Descendants(document, id).Contains(newOwner.Id)))
            return Failure(document, "FFV2-EDIT-CYCLE", "A node cannot be moved beneath itself or one of its descendants.", id);
        if (node.Owner == newOwner)
            return Failure(document, "FFV2-EDIT-NOCHANGE", "The node already has that owner.", id);
        if (OwnerIsLocked(document, node.Owner) || OwnerIsLocked(document, newOwner))
            return Failure(document, "FFV2-EDIT-LOCKED", "Reparenting cannot change the child list of a locked owner.", id);

        var anchors = node.Anchors;
        if (preserveVisualPosition && anchors.FirstOrDefault() is { Target.Kind: AnchorTargetKind.Parent } primary)
        {
            var root = document.CompositionRoots.SingleOrDefault();
            if (root is null)
                return Failure(document, "FFV2-EDIT-GEOMETRY", "Visual position cannot be preserved without exactly one composition root.", id);
            var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root), _templates);
            if (!layout.Elements.TryGetValue(id, out var nodeLayout) || nodeLayout.Rect is not { } nodeRect ||
                !layout.Elements.TryGetValue(newOwner.Id, out var ownerLayout) || ownerLayout.Rect is not { } ownerRect)
                return Failure(document, "FFV2-EDIT-GEOMETRY", "Visual position cannot be preserved because the node or new owner has unresolved geometry.", id);

            var nodePoint = PointOn(nodeRect, primary.Point);
            var ownerPoint = PointOn(ownerRect, primary.RelativePoint);
            anchors = [primary with { OffsetX = nodePoint.X - ownerPoint.X, OffsetY = nodePoint.Y - ownerPoint.Y }, .. anchors.Skip(1)];
        }

        var candidate = RemoveChild(document, node.Owner, id);
        candidate = candidate with
        {
            Nodes = [.. candidate.Nodes.Select(item => item.Id == id ? item with { Owner = newOwner, Anchors = anchors } : item)],
        };
        candidate = AddChild(candidate, newOwner, id);
        return Complete(document, candidate, id);
    }

    public SemanticEditResult ReorderChild(UiDocument document, SemanticId id, int newIndex)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (IsLocked(document, node) || OwnerIsLocked(document, node.Owner))
            return Failure(document, "FFV2-EDIT-LOCKED", "A locked element or owner cannot be reordered.", id);
        var children = Children(document, node.Owner).ToList();
        var oldIndex = children.IndexOf(id);
        if (oldIndex < 0)
            return Failure(document, "FFV2-EDIT-OWNER", "The node is absent from its owner's ordered children.", id);
        newIndex = Math.Clamp(newIndex, 0, children.Count - 1);
        if (oldIndex == newIndex)
            return Failure(document, "FFV2-EDIT-NOCHANGE", "The node is already at that position.", id);
        children.RemoveAt(oldIndex);
        children.Insert(newIndex, id);
        return Complete(document, SetChildren(document, node.Owner, children), id);
    }

    public SemanticEditResult UpdateAnchors(UiDocument document, SemanticId id, IReadOnlyList<UiAnchor> anchors) =>
        anchors.Count == 0
            ? Failure(document, "FFV2-EDIT-ANCHOR", "An editable control requires at least one explicit anchor.", id)
            : UpdateNode(document, id, node => node with { Anchors = [.. anchors] });

    public SemanticEditResult MoveBy(UiDocument document, SemanticId id, double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            return Failure(document, "FFV2-EDIT-GEOMETRY", "Drag deltas must be finite.", id);
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (node.Anchors.Count != 1)
            return Failure(document, "FFV2-EDIT-ANCHOR", "Dragging requires exactly one authored anchor; multi-anchor constraints are unsupported.", id);
        var anchor = node.Anchors[0];
        return UpdateNode(document, id, item => item with
        {
            Anchors = [anchor with { OffsetX = anchor.OffsetX + deltaX, OffsetY = anchor.OffsetY + deltaY }],
        });
    }

    public SemanticEditResult UpdateGeometry(UiDocument document, SemanticId id, double width, double height,
        double offsetX, double offsetY)
    {
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            return Failure(document, "FFV2-EDIT-GEOMETRY", "Width and height must be positive finite values; offsets must be finite.", id);

        return UpdateNode(document, id, node =>
        {
            var anchors = node.Anchors.Count == 0
                ? [new UiAnchor { Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER, Target = AnchorTarget.Parent(), OffsetX = offsetX, OffsetY = offsetY }]
                : (IReadOnlyList<UiAnchor>)[node.Anchors[0] with { OffsetX = offsetX, OffsetY = offsetY }, .. node.Anchors.Skip(1)];
            var properties = node.IsRegion
                ? node.AuthoredProperties with { Region = (node.AuthoredProperties.Region ?? new RegionProperties()) with { Width = width, Height = height } }
                : node.AuthoredProperties with { Frame = (node.AuthoredProperties.Frame ?? new FrameProperties()) with { Width = width, Height = height } };
            return node with { Anchors = anchors, AuthoredProperties = properties };
        });
    }

    public SemanticEditResult UpdateProperties(UiDocument document, SemanticId id, AuthoredProperties properties) =>
        UpdateNode(document, id, node => node with { AuthoredProperties = properties });

    public SemanticEditResult AssignTexture(UiDocument document, SemanticId id, string? reference,
        SemanticProjectAsset? importedAsset = null)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (node.Kind != UiNodeKind.Texture)
            return Failure(document, "FFV2-EDIT-TEXTURE", "Texture artwork can only be assigned to a Texture node.", id);
        if (IsLocked(document, node))
            return Failure(document, "FFV2-EDIT-LOCKED", $"'{node.DisplayLabel}' is locked.", id);
        var properties = node.AuthoredProperties with
        {
            Texture = node.AuthoredProperties.Texture! with { TextureReference = reference },
        };
        var editor = document.Editor ?? new DocumentEditorMetadata();
        var assets = importedAsset is null
            ? editor.ProjectAssets
            : [.. editor.ProjectAssets.Where(asset => asset.Id != importedAsset.Id), importedAsset];
        var candidate = document with
        {
            Nodes = [.. document.Nodes.Select(item => item.Id == id
                ? item with { AuthoredProperties = properties }
                : item)],
            Editor = editor with { ProjectAssets = assets },
        };
        return Complete(document, candidate, id);
    }

    public SemanticEditResult AssignBlizzardTemplate(UiDocument document, SemanticId id,
        string templateIdentity, bool clearEligibleOverrides = false)
    {
        if (_templates is null)
            return Failure(document, "FFV2-EDIT-TEMPLATE-REGISTRY",
                "Assigning a Blizzard template requires an explicit registry snapshot.", id);
        if (string.IsNullOrWhiteSpace(templateIdentity) || _templates.Resolve(templateIdentity) is not { } template)
            return Failure(document, "FFV2-EDIT-TEMPLATE-UNKNOWN",
                $"Template '{templateIdentity}' is not verified by this registry snapshot.", id);
        if (!template.IsResolved)
            return Failure(document, "FFV2-EDIT-TEMPLATE-UNRESOLVED",
                $"Template '{templateIdentity}' has unresolved definitions or dependencies.", id);

        return UpdateNode(document, id, node =>
        {
            if (node.Kind != UiNodeKind.Button)
                return node with { BlizzardTemplate = templateIdentity };
            var properties = node.AuthoredProperties;
            if (clearEligibleOverrides && properties.Frame is { } frame)
                properties = properties with { Frame = frame with { Width = null, Height = null } };
            return node with { BlizzardTemplate = templateIdentity, AuthoredProperties = properties };
        });
    }

    public SemanticEditResult ClearBlizzardTemplate(UiDocument document, SemanticId id) =>
        UpdateNode(document, id, node => node with { BlizzardTemplate = null });

    public SemanticEditResult ClearTemplateEligibleOverrides(UiDocument document, SemanticId id)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (node.BlizzardTemplate is null)
            return Failure(document, "FFV2-EDIT-TEMPLATE-NONE",
                "Template-derived overrides can be cleared only from a templated node.", id);
        return UpdateNode(document, id, item => item with
        {
            AuthoredProperties = item.AuthoredProperties.Frame is { } frame
                ? item.AuthoredProperties with { Frame = frame with { Width = null, Height = null } }
                : item.AuthoredProperties,
        });
    }

    /// <summary>Lock state itself remains editable so a locked node can always be unlocked.</summary>
    public SemanticEditResult SetLocked(UiDocument document, SemanticId id, bool locked)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if ((node.Editor?.Locked ?? false) == locked)
            return Failure(document, "FFV2-EDIT-NOCHANGE", $"'{node.DisplayLabel}' already has that lock state.", id);
        var candidate = document with
        {
            Nodes = [.. document.Nodes.Select(item => item.Id == id
                ? item with { Editor = (item.Editor ?? new NodeEditorMetadata()) with { Locked = locked } }
                : item)],
        };
        return Complete(document, candidate, id);
    }

    private static AuthoredProperties DefaultProperties(UiNodeKind kind) => kind switch
    {
        UiNodeKind.Frame => new() { Frame = new FrameProperties { Width = 120, Height = 64, Visible = true } },
        UiNodeKind.Texture => new()
        {
            Region = new RegionProperties { Width = 64, Height = 64, DrawLayer = RegionDrawLayer.Artwork },
            Texture = new TextureProperties(),
        },
        UiNodeKind.FontString => new()
        {
            Region = new RegionProperties { Width = 120, Height = 24, DrawLayer = RegionDrawLayer.Artwork },
            FontString = new FontStringProperties { Text = "Text", FontReference = "GameFontNormal" },
        },
        UiNodeKind.Button => new()
        {
            Frame = new FrameProperties { Width = 120, Height = 32, Visible = true },
            Button = new ButtonProperties { Enabled = true },
        },
        UiNodeKind.StatusBar => new()
        {
            Frame = new FrameProperties { Width = 120, Height = 16, Visible = true },
            StatusBar = new StatusBarProperties { Minimum = 0, Maximum = 100, Value = 50 },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private SemanticEditResult UpdateNode(UiDocument document, SemanticId id, Func<UiNode, UiNode> update)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
            return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (IsLocked(document, node))
            return Failure(document, "FFV2-EDIT-LOCKED", $"'{node.DisplayLabel}' is locked.", id);
        var candidate = document with { Nodes = [.. document.Nodes.Select(item => item.Id == id ? update(item) : item)] };
        return Complete(document, candidate, id);
    }

    private SemanticEditResult Complete(UiDocument original, UiDocument candidate, SemanticId affected)
    {
        var diagnostics = UiDocumentValidator.Validate(candidate, _templates);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new SemanticEditResult { Document = original, Diagnostics = diagnostics, AffectedId = affected };
        return new SemanticEditResult
        {
            Document = candidate with { Diagnostics = diagnostics },
            Diagnostics = diagnostics,
            AffectedId = affected,
            Changed = candidate != original,
        };
    }

    private static SemanticEditResult Failure(UiDocument document, string code, string message, SemanticId? id = null) => new()
    {
        Document = document,
        Diagnostics =
        [
            new UiDiagnostic { Code = code, Severity = DiagnosticSeverity.Error, Message = message, NodeId = id, PropertyPath = "edit" },
        ],
        AffectedId = id,
    };

    private static bool TryGetContainer(UiDocument document, OwnerReference owner, out string? error)
    {
        error = null;
        if (owner.Kind == OwnerKind.CompositionRoot)
        {
            if (document.CompositionRoots.Any(root => root.Id == owner.Id)) return true;
            error = $"Composition root '{owner.Id}' does not exist.";
            return false;
        }
        var node = document.Nodes.FirstOrDefault(item => item.Id == owner.Id);
        if (node is null) error = $"Owner node '{owner.Id}' does not exist.";
        else if (!node.CanOwnChildren) error = $"{node.Kind} '{node.DisplayLabel}' is a region and cannot own children.";
        return error is null;
    }

    private static bool IsLocked(UiDocument document, UiNode node) =>
        node.Editor?.Locked == true || document.Editor?.Groups.Any(group =>
            group.Locked && group.Members.Contains(node.Id)) == true;

    public static bool IsEditingLocked(UiDocument document, SemanticId id) =>
        document.Nodes.FirstOrDefault(node => node.Id == id) is { } node && IsLocked(document, node);

    private static bool OwnerIsLocked(UiDocument document, OwnerReference owner) =>
        owner.Kind == OwnerKind.LocalNode && document.Nodes.FirstOrDefault(node => node.Id == owner.Id) is { } node && IsLocked(document, node);

    private static UiDocument AddChild(UiDocument document, OwnerReference owner, SemanticId child) =>
        SetChildren(document, owner, [.. Children(document, owner), child]);

    private static UiDocument RemoveChild(UiDocument document, OwnerReference owner, SemanticId child) =>
        SetChildren(document, owner, [.. Children(document, owner).Where(item => item != child)]);

    private static IReadOnlyList<SemanticId> Children(UiDocument document, OwnerReference owner) => owner.Kind switch
    {
        OwnerKind.CompositionRoot => document.CompositionRoots.FirstOrDefault(root => root.Id == owner.Id)?.Children ?? [],
        OwnerKind.LocalNode => document.Nodes.FirstOrDefault(node => node.Id == owner.Id)?.Children ?? [],
        _ => [],
    };

    private static UiDocument SetChildren(UiDocument document, OwnerReference owner, IReadOnlyList<SemanticId> children) =>
        owner.Kind == OwnerKind.CompositionRoot
            ? document with { CompositionRoots = [.. document.CompositionRoots.Select(root => root.Id == owner.Id ? root with { Children = children } : root)] }
            : document with { Nodes = [.. document.Nodes.Select(node => node.Id == owner.Id ? node with { Children = children } : node)] };

    private static IReadOnlySet<SemanticId> Descendants(UiDocument document, SemanticId id)
    {
        var byId = document.Nodes.ToDictionary(node => node.Id);
        var result = new HashSet<SemanticId>();
        void Visit(SemanticId current)
        {
            if (!byId.TryGetValue(current, out var node)) return;
            foreach (var child in node.Children)
                if (result.Add(child)) Visit(child);
        }
        Visit(id);
        return result;
    }

    private static (double X, double Y) PointOn(FrameRect rect, AnchorPoint point)
    {
        var unit = point.Unit();
        return (rect.Left + rect.Width * unit.X, rect.Bottom + rect.Height * unit.Y);
    }
}
