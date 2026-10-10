using FrameForge.Core.Geometry;

namespace FrameForge.Core.Semantics.V2;

public enum V2ResizeHandle
{
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>V2-native multi-object and direct-manipulation commands.</summary>
public sealed partial class UiDocumentEditor
{
    public SemanticEditResult MoveSelection(UiDocument document, IReadOnlyList<SemanticId> ids,
        double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            return Failure(document, "FFV2-EDIT-GEOMETRY", "Movement deltas must be finite.");
        var selected = ids.Distinct().ToHashSet();
        if (selected.Count == 0)
            return Failure(document, "FFV2-EDIT-SELECTION", "Select at least one editable element.");
        var nodes = document.Nodes.ToDictionary(node => node.Id);
        foreach (var id in selected)
        {
            if (!nodes.TryGetValue(id, out var node))
                return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
            if (IsLocked(document, node))
                return Failure(document, "FFV2-EDIT-LOCKED", $"'{node.DisplayLabel}' is locked; the selection was not moved.", id);
            if (node.Anchors.Count != 1)
                return Failure(document, "FFV2-EDIT-ANCHOR",
                    $"'{node.DisplayLabel}' has {node.Anchors.Count} anchors; group movement requires exactly one authored anchor.", id);
        }
        if (Math.Abs(deltaX) < 1e-9 && Math.Abs(deltaY) < 1e-9)
            return Failure(document, "FFV2-EDIT-NOCHANGE", "The requested movement is zero.");

        bool HasSelectedAncestor(UiNode node)
        {
            var owner = node.Owner;
            while (owner.Kind == OwnerKind.LocalNode && nodes.TryGetValue(owner.Id, out var parent))
            {
                if (selected.Contains(parent.Id)) return true;
                owner = parent.Owner;
            }
            return false;
        }

        var movers = selected.Where(id =>
        {
            var node = nodes[id];
            if (HasSelectedAncestor(node)) return false;
            var target = node.Anchors[0].Target;
            return target.Kind != AnchorTargetKind.LocalNode || target.NodeId is not { } targetId || !selected.Contains(targetId);
        }).ToHashSet();
        var candidate = document with
        {
            Nodes = [.. document.Nodes.Select(node => !movers.Contains(node.Id) ? node : node with
            {
                Anchors = [node.Anchors[0] with
                {
                    OffsetX = node.Anchors[0].OffsetX + deltaX,
                    OffsetY = node.Anchors[0].OffsetY + deltaY,
                }],
            })],
        };
        return Complete(document, candidate, ids.Last());
    }

    public SemanticEditResult ResizeBy(UiDocument document, SemanticId id, V2ResizeHandle handle,
        double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            return Failure(document, "FFV2-EDIT-GEOMETRY", "Resize deltas must be finite.", id);
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null) return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (IsLocked(document, node)) return Failure(document, "FFV2-EDIT-LOCKED", $"'{node.DisplayLabel}' is locked.", id);
        if (node.Kind is not (UiNodeKind.Frame or UiNodeKind.Texture or UiNodeKind.Button or UiNodeKind.StatusBar))
            return Failure(document, "FFV2-EDIT-RESIZE-KIND", "Direct resizing supports frame-like controls and textures; FontString auto-sizing is not rewritten.", id);
        if (node.Anchors.Count != 1)
            return Failure(document, "FFV2-EDIT-ANCHOR", "Direct resizing requires exactly one authored anchor.", id);
        var root = document.CompositionRoots.SingleOrDefault();
        if (root is null) return Failure(document, "FFV2-EDIT-GEOMETRY", "Resize requires one composition root.", id);
        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root), _templates);
        if (!layout.Elements.TryGetValue(id, out var element) || element.Rect is not { } rect)
            return Failure(document, "FFV2-EDIT-GEOMETRY", "The selected element has no resolved rectangle to resize.", id);

        var left = rect.Left;
        var right = rect.Right;
        var top = rect.Top;
        var bottom = rect.Bottom;
        if (handle is V2ResizeHandle.Left or V2ResizeHandle.TopLeft or V2ResizeHandle.BottomLeft) left += deltaX;
        if (handle is V2ResizeHandle.Right or V2ResizeHandle.TopRight or V2ResizeHandle.BottomRight) right += deltaX;
        if (handle is V2ResizeHandle.Top or V2ResizeHandle.TopLeft or V2ResizeHandle.TopRight) top += deltaY;
        if (handle is V2ResizeHandle.Bottom or V2ResizeHandle.BottomLeft or V2ResizeHandle.BottomRight) bottom += deltaY;
        var width = right - left;
        var height = top - bottom;
        if (width < 1 || height < 1)
            return Failure(document, "FFV2-EDIT-RESIZE-MIN", "Resize would make width or height smaller than one native UI unit.", id);

        var anchor = node.Anchors[0];
        var oldPoint = PointOn(rect, anchor.Point);
        var resized = new FrameRect(left, top, right, bottom);
        var newPoint = PointOn(resized, anchor.Point);
        var properties = node.IsRegion
            ? node.AuthoredProperties with { Region = node.AuthoredProperties.Region! with { Width = width, Height = height } }
            : node.AuthoredProperties with { Frame = node.AuthoredProperties.Frame! with { Width = width, Height = height } };
        var candidate = document with
        {
            Nodes = [.. document.Nodes.Select(item => item.Id == id ? item with
            {
                AuthoredProperties = properties,
                Anchors = [anchor with
                {
                    OffsetX = anchor.OffsetX + newPoint.X - oldPoint.X,
                    OffsetY = anchor.OffsetY + newPoint.Y - oldPoint.Y,
                }],
            } : item)],
        };
        return Complete(document, candidate, id);
    }

    public SemanticEditResult ArrangeSelection(UiDocument document, IReadOnlyList<SemanticId> ids,
        SemanticId primaryId, SelectionArrangeCommand command)
    {
        var selected = ids.Distinct().ToArray();
        var required = SelectionArrange.IsDistribution(command) ? 3 : 2;
        if (selected.Length < required || !selected.Contains(primaryId))
            return Failure(document, "FFV2-EDIT-SELECTION", $"{SelectionArrange.Label(command)} requires at least {required} selected elements and a primary selection.");
        var nodes = document.Nodes.ToDictionary(node => node.Id);
        var root = document.CompositionRoots.SingleOrDefault();
        if (root is null) return Failure(document, "FFV2-EDIT-GEOMETRY", "Arrange requires one composition root.");
        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root), _templates);
        foreach (var id in selected)
        {
            if (!nodes.TryGetValue(id, out var node)) return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
            if (!layout.Elements.TryGetValue(id, out var element) || element.Rect is null)
                return Failure(document, "FFV2-EDIT-GEOMETRY", $"'{node.DisplayLabel}' has unresolved geometry.", id);
            if (node.Anchors.Count != 1)
                return Failure(document, "FFV2-EDIT-ANCHOR", $"'{node.DisplayLabel}' requires exactly one anchor for arrangement.", id);
        }

        var initial = selected.ToDictionary(id => id, id => layout.Elements[id].Rect!.Value);
        var targets = new Dictionary<SemanticId, FrameRect>();
        if (!SelectionArrange.IsDistribution(command))
        {
            var reference = initial[primaryId];
            foreach (var id in selected)
            {
                var rect = initial[id];
                var targetLeft = command switch
                {
                    SelectionArrangeCommand.AlignLeft => reference.Left,
                    SelectionArrangeCommand.AlignRight => reference.Right - rect.Width,
                    SelectionArrangeCommand.AlignCenterHorizontal => reference.CenterX - rect.Width / 2,
                    _ => rect.Left,
                };
                var targetTop = command switch
                {
                    SelectionArrangeCommand.AlignTop => reference.Top,
                    SelectionArrangeCommand.AlignBottom => reference.Bottom + rect.Height,
                    SelectionArrangeCommand.AlignCenterVertical => reference.CenterY + rect.Height / 2,
                    _ => rect.Top,
                };
                targets[id] = FrameRect.FromSize(targetLeft, targetTop, rect.Width, rect.Height);
            }
        }
        else
        {
            var horizontal = command == SelectionArrangeCommand.DistributeHorizontal;
            var ordered = horizontal
                ? selected.OrderBy(id => initial[id].Left).ToArray()
                : selected.OrderByDescending(id => initial[id].Top).ToArray();
            var total = ordered.Sum(id => horizontal ? initial[id].Width : initial[id].Height);
            var span = horizontal
                ? initial[ordered[^1]].Right - initial[ordered[0]].Left
                : initial[ordered[0]].Top - initial[ordered[^1]].Bottom;
            var gap = (span - total) / (ordered.Length - 1);
            var cursor = horizontal ? initial[ordered[0]].Left : initial[ordered[0]].Top;
            foreach (var id in ordered)
            {
                var rect = initial[id];
                targets[id] = horizontal
                    ? FrameRect.FromSize(cursor, rect.Top, rect.Width, rect.Height)
                    : FrameRect.FromSize(rect.Left, cursor, rect.Width, rect.Height);
                cursor += horizontal ? rect.Width + gap : -(rect.Height + gap);
            }
        }

        var moving = selected.Where(id => targets[id] != initial[id]).ToArray();
        foreach (var id in moving)
            if (IsLocked(document, nodes[id]))
                return Failure(document, "FFV2-EDIT-LOCKED", $"'{nodes[id].DisplayLabel}' is locked; the selection was not arranged.", id);
        if (moving.Length == 0)
            return Failure(document, "FFV2-EDIT-NOCHANGE", "The selection is already arranged.");

        var working = document;
        foreach (var id in moving.OrderBy(id => OwnershipDepth(nodes, id)))
        {
            var current = UiLayoutResolver.Resolve(working, UiPreviewHost.FromDesignRoot(root), _templates);
            if (!current.Elements.TryGetValue(id, out var element) || element.Rect is not { } rect)
                return Failure(document, "FFV2-EDIT-GEOMETRY", "Geometry became unresolved while arranging the selection.", id);
            var result = MoveBy(working, id, targets[id].Left - rect.Left, targets[id].Top - rect.Top);
            if (!result.Success) return result with { Document = document };
            working = result.Document;
        }
        return Complete(document, working, primaryId);
    }

    public SemanticEditResult DeleteSelection(UiDocument document, IReadOnlyList<SemanticId> ids)
    {
        var selected = ids.Distinct().ToHashSet();
        if (selected.Count == 0) return Failure(document, "FFV2-EDIT-SELECTION", "Select at least one element to delete.");
        var nodes = document.Nodes.ToDictionary(node => node.Id);
        if (selected.Any(id => !nodes.ContainsKey(id))) return Failure(document, "FFV2-EDIT-NODE", "The selection contains an element that no longer exists.");
        var reference = selected.Select(id => nodes[id]).FirstOrDefault(node => node.Editor?.ReferenceOnly == true);
        if (reference is not null)
            return Failure(document, "FFV2-EDIT-REFERENCE", $"'{reference.DisplayLabel}' belongs to a protected Blizzard reference composition and cannot be deleted.", reference.Id);
        var removed = new HashSet<SemanticId>();
        foreach (var id in selected)
        {
            removed.Add(id);
            removed.UnionWith(Descendants(document, id));
        }
        var locked = removed.Select(id => nodes[id]).FirstOrDefault(node => IsLocked(document, node));
        if (locked is not null) return Failure(document, "FFV2-EDIT-LOCKED", $"'{locked.DisplayLabel}' is locked; nothing was deleted.", locked.Id);
        var lockedOwner = selected.Select(id => nodes[id]).FirstOrDefault(node =>
            !removed.Contains(node.Owner.Id) && OwnerIsLocked(document, node.Owner));
        if (lockedOwner is not null)
            return Failure(document, "FFV2-EDIT-LOCKED", $"The owner of '{lockedOwner.DisplayLabel}' is locked; nothing was deleted.", lockedOwner.Id);
        var survivorReference = document.Nodes.FirstOrDefault(node => !removed.Contains(node.Id) && node.Anchors.Any(anchor =>
            anchor.Target.Kind == AnchorTargetKind.LocalNode && anchor.Target.NodeId is { } target && removed.Contains(target)));
        if (survivorReference is not null)
            return Failure(document, "FFV2-EDIT-REFERENCE", $"'{survivorReference.DisplayLabel}' anchors to the selected deletion set.", survivorReference.Id);
        var candidate = document with
        {
            Nodes = [.. document.Nodes.Where(node => !removed.Contains(node.Id)).Select(node => node with
            {
                Children = [.. node.Children.Where(id => !removed.Contains(id))],
            })],
            CompositionRoots = [.. document.CompositionRoots.Select(root => root with
            {
                Children = [.. root.Children.Where(id => !removed.Contains(id))],
            })],
            Editor = document.Editor is null ? null : document.Editor with
            {
                Groups = [.. document.Editor.Groups.Select(group => group with
                {
                    Members = [.. group.Members.Where(id => !removed.Contains(id))],
                })],
            },
        };
        return Complete(document, candidate, ids.Last());
    }

    public SemanticEditResult ReorderControl(UiDocument document, SemanticId id, int destination)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null) return Failure(document, "FFV2-EDIT-NODE", $"Node '{id}' does not exist.", id);
        if (IsLocked(document, node) || OwnerIsLocked(document, node.Owner))
            return Failure(document, "FFV2-EDIT-LOCKED", "A locked element or owner cannot be reordered.", id);
        var children = Children(document, node.Owner);
        var nodes = document.Nodes.ToDictionary(item => item.Id);
        bool SamePaintBand(UiNode other) => node.IsRegion == other.IsRegion && (node.IsRegion
            ? (node.AuthoredProperties.Region?.DrawLayer ?? RegionDrawLayer.Artwork) ==
              (other.AuthoredProperties.Region?.DrawLayer ?? RegionDrawLayer.Artwork) &&
              (node.AuthoredProperties.Region?.Sublevel ?? 0) == (other.AuthoredProperties.Region?.Sublevel ?? 0)
            : (node.AuthoredProperties.Frame?.Strata ?? FrameStrata.Medium) ==
              (other.AuthoredProperties.Frame?.Strata ?? FrameStrata.Medium) &&
              (node.AuthoredProperties.Frame?.Level ?? 0) == (other.AuthoredProperties.Frame?.Level ?? 0));
        var slots = children.Select((childId, index) => (childId, index))
            .Where(item => nodes.TryGetValue(item.childId, out var sibling) && SamePaintBand(sibling))
            .ToArray();
        var band = slots.Select(item => item.childId).ToList();
        var index = band.IndexOf(id);
        if (index < 0 || band.Count < 2)
            return Failure(document, "FFV2-EDIT-ORDER-BAND",
                "Ordering is only meaningful between siblings in the same frame strata/level or region draw-layer/sublevel.", id);
        var target = destination switch { int.MinValue => 0, int.MaxValue => band.Count - 1, _ => index + destination };
        target = Math.Clamp(target, 0, band.Count - 1);
        if (target == index)
            return Failure(document, "FFV2-EDIT-NOCHANGE", "The control is already at that position in its paint-order band.", id);
        band.RemoveAt(index);
        band.Insert(target, id);
        var reordered = children.ToArray();
        for (var slot = 0; slot < slots.Length; slot++) reordered[slots[slot].index] = band[slot];
        return Complete(document, SetChildren(document, node.Owner, reordered), id);
    }

    public SemanticEditResult CreateGroup(UiDocument document, string name, IReadOnlyList<SemanticId> members)
    {
        if (string.IsNullOrWhiteSpace(name)) return Failure(document, "FFV2-EDIT-GROUP", "Group name cannot be empty.");
        var editor = document.Editor ?? new DocumentEditorMetadata();
        if (editor.Groups.Any(group => string.Equals(group.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Failure(document, "FFV2-EDIT-GROUP", $"A group named '{name.Trim()}' already exists.");
        var valid = members.Distinct().Where(id => document.Nodes.Any(node => node.Id == id)).ToArray();
        if (valid.Length == 0)
            return Failure(document, "FFV2-EDIT-GROUP", "Select at least one native element to create an editor group.");
        var group = new SemanticEditorGroup { Id = SemanticId.New(), Name = name.Trim(), Members = valid };
        return Complete(document, document with { Editor = editor with { Groups = [.. editor.Groups, group] } }, group.Id);
    }

    public SemanticEditResult UpdateGroup(UiDocument document, SemanticId groupId, string? name = null,
        IReadOnlyList<SemanticId>? members = null, bool? locked = null)
    {
        var editor = document.Editor ?? new DocumentEditorMetadata();
        var group = editor.Groups.FirstOrDefault(item => item.Id == groupId);
        if (group is null) return Failure(document, "FFV2-EDIT-GROUP", $"Group '{groupId}' does not exist.", groupId);
        if (name is not null && string.IsNullOrWhiteSpace(name)) return Failure(document, "FFV2-EDIT-GROUP", "Group name cannot be empty.", groupId);
        if (name is not null && editor.Groups.Any(item => item.Id != groupId &&
                string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Failure(document, "FFV2-EDIT-GROUP", $"A group named '{name.Trim()}' already exists.", groupId);
        var validMembers = members?.Distinct().Where(id => document.Nodes.Any(node => node.Id == id)).ToArray();
        var updated = group with { Name = name?.Trim() ?? group.Name, Members = validMembers ?? group.Members, Locked = locked ?? group.Locked };
        var candidate = document with { Editor = editor with { Groups = [.. editor.Groups.Select(item => item.Id == groupId ? updated : item)] } };
        return Complete(document, candidate, groupId);
    }

    public SemanticEditResult DeleteGroup(UiDocument document, SemanticId groupId)
    {
        var editor = document.Editor ?? new DocumentEditorMetadata();
        if (!editor.Groups.Any(group => group.Id == groupId)) return Failure(document, "FFV2-EDIT-GROUP", $"Group '{groupId}' does not exist.", groupId);
        if (editor.ReferenceCompositions.Any(reference => reference.LockGroupId == groupId))
            return Failure(document, "FFV2-EDIT-REFERENCE", "The lock group for a Blizzard reference composition cannot be deleted.", groupId);
        return Complete(document, document with { Editor = editor with { Groups = [.. editor.Groups.Where(group => group.Id != groupId)] } }, groupId);
    }

    public SemanticEditResult SetReferenceLocked(UiDocument document, SemanticId referenceId, bool locked)
    {
        var editor = document.Editor ?? new DocumentEditorMetadata();
        var reference = editor.ReferenceCompositions.FirstOrDefault(item => item.Id == referenceId);
        if (reference is null) return Failure(document, "FFV2-EDIT-REFERENCE", $"Reference composition '{referenceId}' does not exist.", referenceId);
        var group = editor.Groups.FirstOrDefault(item => item.Id == reference.LockGroupId);
        if (group is null) return Failure(document, "FFV2-EDIT-REFERENCE", "The reference composition lock group is missing.", referenceId);
        if (group.Locked == locked) return Failure(document, "FFV2-EDIT-NOCHANGE", $"'{reference.Name}' already has that lock state.", referenceId);
        var candidate = document with { Editor = editor with
        {
            Groups = [.. editor.Groups.Select(item => item.Id == group.Id ? item with { Locked = locked } : item)],
        } };
        return Complete(document, candidate, referenceId);
    }

    public SemanticEditResult SetReferenceVisibility(UiDocument document, SemanticId nodeId,
        bool visible, bool includeDescendants = false)
    {
        var node = document.Nodes.FirstOrDefault(item => item.Id == nodeId);
        if (node?.Editor?.ReferenceOnly != true)
            return Failure(document, "FFV2-EDIT-REFERENCE",
                "Only protected Blizzard reference elements have editor-only visibility.", nodeId);
        var affected = includeDescendants
            ? Descendants(document, nodeId).Prepend(nodeId).ToHashSet()
            : new HashSet<SemanticId> { nodeId };
        var hidden = (document.Editor?.HiddenReferenceNodes ?? []).ToHashSet();
        var changed = visible ? hidden.RemoveWhere(affected.Contains) > 0 : hidden.UnionWithChanged(affected);
        if (!changed)
            return Failure(document, "FFV2-EDIT-NOCHANGE",
                visible ? "The selected reference geometry is already restored." : "The selected reference geometry is already hidden.", nodeId);
        var editor = document.Editor ?? new DocumentEditorMetadata();
        return Complete(document, document with
        {
            Editor = editor with { HiddenReferenceNodes = hidden.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray() },
        }, nodeId);
    }

    public SemanticEditResult RestoreReferenceComposition(UiDocument document, SemanticId referenceId)
    {
        var editor = document.Editor ?? new DocumentEditorMetadata();
        var reference = editor.ReferenceCompositions.FirstOrDefault(item => item.Id == referenceId);
        if (reference is null) return Failure(document, "FFV2-EDIT-REFERENCE", $"Reference composition '{referenceId}' does not exist.", referenceId);
        var originals = reference.OriginalNodes.ToDictionary(node => node.Id);
        if (!reference.Members.ToHashSet().SetEquals(originals.Keys))
            return Failure(document, "FFV2-EDIT-REFERENCE", "The reference baseline is incomplete and cannot be restored safely.", referenceId);
        var members = reference.Members.ToHashSet();
        var rootMembers = originals.Values.Where(node => node.Owner.Kind == OwnerKind.CompositionRoot)
            .Select(node => node.Id).ToArray();
        var candidate = document with
        {
            CompositionRoots = [.. document.CompositionRoots.Select(root => root with
            {
                Children = [.. rootMembers.Where(id => originals[id].Owner.Id == root.Id),
                    .. root.Children.Where(id => !members.Contains(id))],
            })],
            Nodes = [.. document.Nodes.Select(current => !originals.TryGetValue(current.Id, out var original)
                ? current with { Children = [.. current.Children.Where(id => !members.Contains(id))] }
                : original with { Editor = current.Editor })],
            Editor = editor with
            {
                HiddenReferenceNodes = [.. editor.HiddenReferenceNodes.Where(id => !members.Contains(id))],
            },
        };
        return Complete(document, candidate, referenceId);
    }

    private static int OwnershipDepth(IReadOnlyDictionary<SemanticId, UiNode> nodes, SemanticId id)
    {
        var depth = 0;
        var node = nodes[id];
        while (node.Owner.Kind == OwnerKind.LocalNode && nodes.TryGetValue(node.Owner.Id, out node!)) depth++;
        return depth;
    }
}

internal static class SemanticVisualListExtensions
{
    public static bool UnionWithChanged<T>(this HashSet<T> values, IEnumerable<T> additions)
    {
        var before = values.Count;
        values.UnionWith(additions);
        return values.Count != before;
    }

    public static int IndexOf(this IReadOnlyList<SemanticId> values, SemanticId id)
    {
        for (var index = 0; index < values.Count; index++) if (values[index] == id) return index;
        return -1;
    }
}
