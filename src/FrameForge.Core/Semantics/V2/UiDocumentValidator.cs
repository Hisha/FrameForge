using System.Text.RegularExpressions;
using FrameForge.Core.Templates;

namespace FrameForge.Core.Semantics.V2;

/// <summary>Validates the schema-v2 semantic graph without mutating or repairing it.</summary>
public static partial class UiDocumentValidator
{
    public static IReadOnlyList<UiDiagnostic> Validate(UiDocument document,
        BlizzardTemplateRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<UiDiagnostic>();
        if (document.Version != UiDocument.SchemaVersion)
            Add("FFV2-DOC-001", $"Schema version {document.Version} is unsupported; expected {UiDocument.SchemaVersion}.", "version");
        if (!document.DocumentId.IsValid)
            Add("FFV2-ID-001", "Document identity must be a canonical GUID.", "documentId");
        if (!document.Target.IsSupported)
            Add("FFV2-TARGET-001",
                $"Target '{document.Target.Product}' build {document.Target.Build} is unsupported; only WoW 3.3.5a build 12340 is supported.",
                "target");

        if (document.CompositionRoots.Count == 0)
            Add("FFV2-ROOT-001", "The document is missing its composition root.", "compositionRoots");
        else if (document.CompositionRoots.Count > 1)
            Add("FFV2-ROOT-002", $"The document has {document.CompositionRoots.Count} composition roots; exactly one is required.", "compositionRoots");

        var duplicateIds = document.CompositionRoots.Select(root => (root.Id, "composition root"))
            .Concat(document.Nodes.Select(node => (node.Id, "node")))
            .GroupBy(item => item.Id)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateIds)
            Add("FFV2-ID-002", $"Internal identity '{duplicate.Key}' is duplicated.", "id", duplicate.Key);

        foreach (var root in document.CompositionRoots)
            ValidateRoot(root);

        var rootsById = document.CompositionRoots
            .GroupBy(root => root.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var nodesById = document.Nodes
            .GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var externalNames = document.ExternalReferences.Select(item => item.GlobalName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in document.ExternalReferences.GroupBy(item => item.GlobalName, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Add("FFV2-EXT-001", $"External global '{duplicate.Key}' is declared more than once.", "externalReferences");
        foreach (var external in document.ExternalReferences)
            if (!IsGlobalName(external.GlobalName))
                Add("FFV2-EXT-002", $"External global name '{external.GlobalName}' is not a valid WoW global identifier.", "externalReferences");
        foreach (var root in document.CompositionRoots)
        {
            if (!externalNames.Contains(root.ExternalHostName))
                Add("FFV2-EXT-003", $"Composition host '{root.ExternalHostName}' is not declared in externalReferences.", "externalHostName", root.Id);
            ValidateChildren(root.Id, root.Children, canOwnChildren: true);
        }

        var runtimeIdentities = document.CompositionRoots.Select(root => (root.RuntimeName, root.Id))
            .Concat(document.Nodes.Where(node => node.RuntimeName is not null).Select(node => (node.RuntimeName!, node.Id)));
        foreach (var duplicate in runtimeIdentities.GroupBy(item => item.Item1, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Add("FFV2-ID-003", $"Runtime/global identity '{duplicate.Key}' is duplicated.", "runtimeName", duplicate.First().Id);
        foreach (var runtime in runtimeIdentities.Where(item => externalNames.Contains(item.Item1)))
            Add("FFV2-ID-007", $"Authored runtime identity '{runtime.Item1}' collides with a declared external global.", "runtimeName", runtime.Id);

        foreach (var node in document.Nodes)
        {
            if (!node.Id.IsValid)
                Add("FFV2-ID-004", $"Node '{node.DisplayLabel}' has an invalid internal identity '{node.Id}'.", "id", node.Id);
            if (string.IsNullOrWhiteSpace(node.DisplayLabel))
                Add("FFV2-NODE-001", "A node display label cannot be empty.", "displayLabel", node.Id);
            if (node.RuntimeName is not null && !IsGlobalName(node.RuntimeName))
                Add("FFV2-ID-005", $"Runtime/global identity '{node.RuntimeName}' is invalid.", "runtimeName", node.Id);

            ValidateOwner(node);
            ValidateChildren(node.Id, node.Children, node.CanOwnChildren);
            ValidateAnchors(node);
            ValidateProperties(node);
            ValidateTemplate(node);
            if (node.Editor is { ReferenceOnly: true } referenceGeometry &&
                (referenceGeometry.ReferenceAutoWidth || referenceGeometry.ReferenceAutoHeight))
                diagnostics.Add(new UiDiagnostic
                {
                    Code = "FFV2-REF-GEOMETRY-001",
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Reference region '{node.DisplayLabel}' declares zero for its " +
                              $"{(referenceGeometry.ReferenceAutoWidth && referenceGeometry.ReferenceAutoHeight ? "width and height" : referenceGeometry.ReferenceAutoWidth ? "width" : "height")}; " +
                              "the axis is preserved as automatic/unresolved rather than as an invalid authored dimension.",
                    NodeId = node.Id,
                    PropertyPath = "authoredProperties.region",
                });
            if (node.Editor?.ReferenceOnly != true && node.Owner.Kind == OwnerKind.LocalNode &&
                nodesById.GetValueOrDefault(node.Owner.Id)?.Editor?.ReferenceOnly == true)
                Add("FFV2-REF-007", "An authored node cannot use an editor-only reference as its structural owner.", "owner", node.Id);
        }

        if (document.Editor is { } editor)
        {
            foreach (var duplicate in editor.ProjectAssets.GroupBy(asset => asset.Id).Where(group => group.Count() > 1))
                Add("FFV2-ASSET-001", $"Project asset identity '{duplicate.Key}' is duplicated.",
                    "editor.projectAssets", duplicate.Key);
            foreach (var asset in editor.ProjectAssets)
            {
                if (!asset.Id.IsValid || string.IsNullOrWhiteSpace(asset.SourceReference) ||
                    string.IsNullOrWhiteSpace(asset.PreviewReference) || string.IsNullOrWhiteSpace(asset.PreparedReference) ||
                    string.IsNullOrWhiteSpace(asset.Format))
                    Add("FFV2-ASSET-002", "Project assets require a valid identity, source, preview path, prepared path, and format.",
                        "editor.projectAssets", asset.Id);
                if (asset.Width is <= 0 || asset.Height is <= 0)
                    Add("FFV2-ASSET-003", "Known project asset dimensions must be positive.",
                        "editor.projectAssets", asset.Id);
                if (asset.IntendedClientPath is { } path &&
                    (!path.StartsWith("Interface\\FrameForge\\Artwork\\", StringComparison.Ordinal) ||
                     path.Contains("..", StringComparison.Ordinal)))
                    Add("FFV2-ASSET-004", "Prepared client artwork must use the deterministic Interface\\FrameForge\\Artwork namespace.",
                        "editor.projectAssets.intendedClientPath", asset.Id);
            }
            foreach (var duplicate in editor.HiddenReferenceNodes.GroupBy(id => id).Where(group => group.Count() > 1))
                Add("FFV2-REF-VIS-001", $"Hidden reference node '{duplicate.Key}' is listed more than once.",
                    "editor.hiddenReferenceNodes", duplicate.Key);
            foreach (var hidden in editor.HiddenReferenceNodes.Distinct())
                if (!nodesById.TryGetValue(hidden, out var hiddenNode) || hiddenNode.Editor?.ReferenceOnly != true)
                    Add("FFV2-REF-VIS-002", $"Editor visibility references non-reference node '{hidden}'.",
                        "editor.hiddenReferenceNodes", hidden);
            foreach (var duplicate in editor.Groups.GroupBy(group => group.Id).Where(group => group.Count() > 1))
                Add("FFV2-GROUP-001", $"Editor group identity '{duplicate.Key}' is duplicated.", "editor.groups", duplicate.Key);
            foreach (var duplicate in editor.Groups.GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                Add("FFV2-GROUP-002", $"Editor group name '{duplicate.Key}' is duplicated.", "editor.groups");
            foreach (var group in editor.Groups)
            {
                if (!group.Id.IsValid)
                    Add("FFV2-GROUP-003", "Editor group identity must be a canonical GUID.", "editor.groups.id", group.Id);
                if (string.IsNullOrWhiteSpace(group.Name))
                    Add("FFV2-GROUP-004", "Editor group name cannot be empty.", "editor.groups.name", group.Id);
                foreach (var duplicate in group.Members.GroupBy(id => id).Where(items => items.Count() > 1))
                    Add("FFV2-GROUP-005", $"Node '{duplicate.Key}' occurs more than once in editor group '{group.Name}'.", "editor.groups.members", group.Id);
                foreach (var member in group.Members.Distinct().Where(id => !nodesById.ContainsKey(id)))
                    Add("FFV2-GROUP-006", $"Editor group '{group.Name}' references missing node '{member}'.", "editor.groups.members", group.Id);
            }
            foreach (var duplicate in editor.ReferenceCompositions.GroupBy(reference => reference.Id).Where(group => group.Count() > 1))
                Add("FFV2-REF-001", $"Reference composition identity '{duplicate.Key}' is duplicated.", "editor.referenceCompositions", duplicate.Key);
            foreach (var reference in editor.ReferenceCompositions)
            {
                var members = reference.Members.ToHashSet();
                if (!reference.Id.IsValid || string.IsNullOrWhiteSpace(reference.Name) || string.IsNullOrWhiteSpace(reference.SourceIdentity))
                    Add("FFV2-REF-002", "Reference compositions require a valid identity, name, and source identity.", "editor.referenceCompositions", reference.Id);
                if (!members.Contains(reference.RootNodeId) || !nodesById.ContainsKey(reference.RootNodeId))
                    Add("FFV2-REF-003", $"Reference composition '{reference.Name}' has a missing or non-member root.", "editor.referenceCompositions.rootNodeId", reference.Id);
                if (!editor.Groups.Any(group => group.Id == reference.LockGroupId && group.Members.ToHashSet().SetEquals(members)))
                    Add("FFV2-REF-004", $"Reference composition '{reference.Name}' requires a matching editor lock group.", "editor.referenceCompositions.lockGroupId", reference.Id);
                foreach (var member in members)
                {
                    if (!nodesById.TryGetValue(member, out var node) || node.Editor?.ReferenceOnly != true ||
                        node.Editor.ReferenceCompositionId != reference.Id)
                        Add("FFV2-REF-005", $"Reference composition '{reference.Name}' has invalid member '{member}'.", "editor.referenceCompositions.members", reference.Id);
                }
                if (!reference.OriginalNodes.Select(node => node.Id).ToHashSet().SetEquals(members))
                    Add("FFV2-REF-006", $"Reference composition '{reference.Name}' baseline does not match its members.", "editor.referenceCompositions.originalNodes", reference.Id);
            }
            foreach (var duplicate in editor.PreviewStates.GroupBy(state => state.Id).Where(group => group.Count() > 1))
                Add("FFV2-PREVIEW-001", $"Preview-state identity '{duplicate.Key}' is duplicated.", "editor.previewStates", duplicate.Key);
            foreach (var state in editor.PreviewStates)
            {
                if (!state.Id.IsValid || string.IsNullOrWhiteSpace(state.Name))
                    Add("FFV2-PREVIEW-002", "Preview states require a valid identity and name.", "editor.previewStates", state.Id);
                foreach (var value in state.Overrides)
                {
                    if (!nodesById.TryGetValue(value.NodeId, out var node))
                        Add("FFV2-PREVIEW-003", $"Preview state '{state.Name}' references missing node '{value.NodeId}'.", "editor.previewStates.overrides", state.Id);
                    if (value.ProgressValue is { } progress && (!double.IsFinite(progress) ||
                        node?.AuthoredProperties.StatusBar is not { Minimum: { } min, Maximum: { } max } || progress < min || progress > max))
                        Add("FFV2-PREVIEW-004", $"Preview progress for '{value.NodeId}' is outside its authored status-bar range.", "editor.previewStates.overrides.progressValue", state.Id);
                }
            }
        }

        ValidateOwnershipCycles();
        return diagnostics;

        void ValidateRoot(CompositionRoot root)
        {
            if (!root.Id.IsValid)
                Add("FFV2-ID-006", $"Composition root has an invalid identity '{root.Id}'.", "id", root.Id);
            if (!IsGlobalName(root.RuntimeName))
                Add("FFV2-ROOT-003", $"Composition root runtime name '{root.RuntimeName}' is invalid.", "runtimeName", root.Id);
            if (!IsGlobalName(root.ExternalHostName))
                Add("FFV2-ROOT-004", $"Composition root host '{root.ExternalHostName}' is invalid.", "externalHostName", root.Id);
            if (!(root.DesignWidth > 0) || !double.IsFinite(root.DesignWidth) ||
                !(root.DesignHeight > 0) || !double.IsFinite(root.DesignHeight))
                Add("FFV2-ROOT-005", "Composition root design dimensions must be finite and greater than zero.", "designSize", root.Id);

            switch (root.Sizing.Kind)
            {
                case RootSizingKind.FillHost when root.Sizing.Width is not null || root.Sizing.Height is not null || root.Sizing.Anchor is not null:
                    Add("FFV2-ROOT-006", "Fill-host sizing cannot also define explicit dimensions or an anchor.", "sizing", root.Id);
                    break;
                case RootSizingKind.Explicit when root.Sizing.Width is null || root.Sizing.Height is null || root.Sizing.Anchor is null:
                    Add("FFV2-ROOT-007", "Explicit root sizing requires width, height, and a host-relative anchor.", "sizing", root.Id);
                    break;
                case RootSizingKind.Explicit when !IsPositiveFinite(root.Sizing.Width.Value) || !IsPositiveFinite(root.Sizing.Height.Value):
                    Add("FFV2-ROOT-008", "Explicit root dimensions must be finite and greater than zero.", "sizing", root.Id);
                    break;
            }
        }

        void ValidateOwner(UiNode node)
        {
            switch (node.Owner.Kind)
            {
                case OwnerKind.CompositionRoot:
                    if (!rootsById.TryGetValue(node.Owner.Id, out var root))
                    {
                        Add("FFV2-OWNER-001", $"Owner composition root '{node.Owner.Id}' does not exist.", "owner", node.Id);
                        return;
                    }
                    ValidateMembership(root.Children, root.Id, "composition root", node);
                    break;
                case OwnerKind.LocalNode:
                    if (!nodesById.TryGetValue(node.Owner.Id, out var owner))
                    {
                        Add("FFV2-OWNER-002", $"Owner node '{node.Owner.Id}' does not exist.", "owner", node.Id);
                        return;
                    }
                    if (!owner.CanOwnChildren)
                        Add("FFV2-OWNER-003", $"{owner.Kind} '{owner.DisplayLabel}' is a region and cannot own node '{node.DisplayLabel}'.", "owner", node.Id);
                    ValidateMembership(owner.Children, owner.Id, "node", node);
                    break;
                default:
                    Add("FFV2-OWNER-004", $"Node '{node.DisplayLabel}' has an invalid owner type.", "owner", node.Id);
                    break;
            }
        }

        void ValidateMembership(IReadOnlyList<SemanticId> children, SemanticId ownerId, string ownerType, UiNode node)
        {
            var count = children.Count(id => id == node.Id);
            if (count == 0)
                Add("FFV2-OWNER-005", $"Node '{node.DisplayLabel}' names {ownerType} '{ownerId}' as owner, but is absent from its ordered children.", "owner", node.Id);
            else if (count > 1)
                Add("FFV2-OWNER-006", $"Node '{node.DisplayLabel}' occurs more than once in owner '{ownerId}' children.", "owner", node.Id);
        }

        void ValidateChildren(SemanticId ownerId, IReadOnlyList<SemanticId> children, bool canOwnChildren)
        {
            if (!canOwnChildren && children.Count > 0)
                Add("FFV2-OWNER-007", "A region cannot own child nodes.", "children", ownerId);
            foreach (var duplicate in children.GroupBy(id => id).Where(group => group.Count() > 1))
                Add("FFV2-OWNER-008", $"Child '{duplicate.Key}' appears more than once in the ordered children of '{ownerId}'.", "children", ownerId);
            foreach (var childId in children.Distinct())
            {
                if (!nodesById.TryGetValue(childId, out var child))
                    Add("FFV2-OWNER-009", $"Ordered child '{childId}' does not exist.", "children", ownerId);
                else if (child.Owner.Id != ownerId)
                    Add("FFV2-OWNER-010", $"Child '{child.DisplayLabel}' does not name '{ownerId}' as its owner.", "children", ownerId);
            }
        }

        void ValidateAnchors(UiNode node)
        {
            for (var index = 0; index < node.Anchors.Count; index++)
            {
                var target = node.Anchors[index].Target;
                var path = $"anchors[{index}].target";
                switch (target.Kind)
                {
                    case AnchorTargetKind.Parent:
                        break;
                    case AnchorTargetKind.CompositionRoot:
                        if (document.CompositionRoots.Count != 1)
                            Add("FFV2-ANCHOR-001", "A composition-root anchor is ambiguous because the document does not have exactly one root.", path, node.Id);
                        break;
                    case AnchorTargetKind.LocalNode:
                        if (target.NodeId is null || !nodesById.ContainsKey(target.NodeId.Value))
                            Add("FFV2-ANCHOR-002", $"Local anchor target '{target.NodeId?.ToString() ?? "(missing)"}' does not exist.", path, node.Id);
                        else if (target.NodeId == node.Id)
                            Add("FFV2-ANCHOR-007", "A node cannot anchor to itself.", path, node.Id);
                        break;
                    case AnchorTargetKind.ExternalGlobal:
                        if (target.GlobalName is null || !IsGlobalName(target.GlobalName))
                            Add("FFV2-ANCHOR-003", "An external anchor requires a valid explicit global frame name.", path, node.Id);
                        else if (!externalNames.Contains(target.GlobalName))
                            Add("FFV2-ANCHOR-004", $"External anchor '{target.GlobalName}' is not declared in externalReferences.", path, node.Id);
                        break;
                    case AnchorTargetKind.Unresolved:
                        Add("FFV2-ANCHOR-005",
                            $"Anchor target '{target.UnresolvedText ?? "(unknown)"}' is unresolved: {target.Diagnostic ?? "no diagnostic supplied"}.",
                            path, node.Id);
                        break;
                    default:
                        Add("FFV2-ANCHOR-006", "Anchor target type is invalid.", path, node.Id);
                        break;
                }
            }
        }

        void ValidateProperties(UiNode node)
        {
            var properties = node.AuthoredProperties;
            if (node.IsRegion)
            {
                if (properties.Frame is not null || properties.Button is not null || properties.StatusBar is not null)
                    Add("FFV2-PROP-001", $"{node.Kind} is a region and cannot carry frame properties.", "authoredProperties", node.Id);
                if (properties.Region is null)
                    Add("FFV2-PROP-002", $"{node.Kind} requires region properties.", "authoredProperties.region", node.Id);
            }
            else
            {
                if (properties.Frame is null)
                    Add("FFV2-PROP-003", $"{node.Kind} requires frame properties.", "authoredProperties.frame", node.Id);
                if (properties.Region is not null || properties.Texture is not null || properties.FontString is not null)
                    Add("FFV2-PROP-004", $"{node.Kind} is a frame and cannot carry region properties.", "authoredProperties", node.Id);
            }

            if (node.Kind != UiNodeKind.Texture && properties.Texture is not null)
                Add("FFV2-PROP-005", "Texture properties are valid only on Texture nodes.", "authoredProperties.texture", node.Id);
            if (node.Kind != UiNodeKind.FontString && properties.FontString is not null)
                Add("FFV2-PROP-006", "FontString properties are valid only on FontString nodes.", "authoredProperties.fontString", node.Id);
            if (node.Kind != UiNodeKind.Button && properties.Button is not null)
                Add("FFV2-PROP-007", "Button properties are valid only on Button nodes.", "authoredProperties.button", node.Id);
            if (node.Kind != UiNodeKind.StatusBar && properties.StatusBar is not null)
                Add("FFV2-PROP-008", "StatusBar properties are valid only on StatusBar nodes.", "authoredProperties.statusBar", node.Id);
            if (node.Kind == UiNodeKind.Texture && properties.Texture is null)
                Add("FFV2-PROP-014", "Texture nodes require a texture property block, even when the reference is not yet set.", "authoredProperties.texture", node.Id);
            if (properties.Texture?.TexCoords is { IsValid: false })
                Add("FFV2-PROP-025", "Texture coordinates must be finite, ordered values from zero through one.", "authoredProperties.texture.texCoords", node.Id);
            if (node.Kind == UiNodeKind.FontString && properties.FontString is null)
                Add("FFV2-PROP-015", "FontString nodes require a font-string property block, even when its values are not yet set.", "authoredProperties.fontString", node.Id);
            if (node.Kind == UiNodeKind.Button && properties.Button is null)
                Add("FFV2-PROP-016", "Button nodes require a button property block.", "authoredProperties.button", node.Id);
            if (node.Kind == UiNodeKind.StatusBar && properties.StatusBar is null)
                Add("FFV2-PROP-017", "StatusBar nodes require a status-bar property block.", "authoredProperties.statusBar", node.Id);

            if (properties.Frame is { } frame)
            {
                if (frame.Width is not null && !IsPositiveFinite(frame.Width.Value) ||
                    frame.Height is not null && !IsPositiveFinite(frame.Height.Value))
                    Add("FFV2-PROP-009", "Authored frame dimensions must be finite and greater than zero.", "authoredProperties.frame", node.Id);
                if (frame.Level < 0)
                    Add("FFV2-PROP-010", "Frame level cannot be negative.", "authoredProperties.frame.level", node.Id);
            }
            if (properties.Region?.Tint is { } tint &&
                !new[] { tint.Red, tint.Green, tint.Blue, tint.Alpha }.All(value => double.IsFinite(value) && value is >= 0 and <= 1))
                Add("FFV2-PROP-011", "Region tint components must be finite values from 0 through 1.", "authoredProperties.region.tint", node.Id);
            if (properties.Region is { } region &&
                (region.Width is not null && !IsPositiveFinite(region.Width.Value) ||
                 region.Height is not null && !IsPositiveFinite(region.Height.Value)))
                Add("FFV2-PROP-019", "Authored region dimensions must be finite and greater than zero.", "authoredProperties.region", node.Id);
            if (properties.FontString is { FontSize: not null } font && !IsPositiveFinite(font.FontSize.Value))
                Add("FFV2-PROP-020", "FontString font size must be finite and greater than zero.", "authoredProperties.fontString.fontSize", node.Id);
            if (properties.FontString?.JustifyH is { } justifyH &&
                !new[] { "LEFT", "CENTER", "RIGHT" }.Contains(justifyH, StringComparer.OrdinalIgnoreCase))
                Add("FFV2-PROP-021", "FontString horizontal justification must be LEFT, CENTER, or RIGHT.", "authoredProperties.fontString.justifyH", node.Id);
            if (properties.FontString?.JustifyV is { } justifyV &&
                !new[] { "TOP", "MIDDLE", "BOTTOM" }.Contains(justifyV, StringComparer.OrdinalIgnoreCase))
                Add("FFV2-PROP-022", "FontString vertical justification must be TOP, MIDDLE, or BOTTOM.", "authoredProperties.fontString.justifyV", node.Id);
            if (properties.StatusBar is { Minimum: not null, Maximum: not null } status)
            {
                if (!double.IsFinite(status.Minimum.Value) || !double.IsFinite(status.Maximum.Value) || status.Minimum.Value >= status.Maximum.Value)
                    Add("FFV2-PROP-012", "StatusBar minimum must be finite and less than maximum.", "authoredProperties.statusBar", node.Id);
                else if (status.Value is not null && (!double.IsFinite(status.Value.Value) || status.Value < status.Minimum || status.Value > status.Maximum))
                    Add("FFV2-PROP-013", "StatusBar value must be finite and within its authored range.", "authoredProperties.statusBar.value", node.Id);
            }
            if (properties.StatusBar is { } partialStatus &&
                (partialStatus.Minimum is null) != (partialStatus.Maximum is null))
                Add("FFV2-PROP-018", "StatusBar minimum and maximum must be authored together.", "authoredProperties.statusBar", node.Id);
            if (properties.StatusBar?.FillColor is { } fillColor && !IsValidColor(fillColor))
                Add("FFV2-PROP-023", "StatusBar fill color components must be finite values from 0 through 1.", "authoredProperties.statusBar.fillColor", node.Id);
            if (properties.StatusBar?.BackgroundColor is { } backgroundColor && !IsValidColor(backgroundColor))
                Add("FFV2-PROP-024", "StatusBar background color components must be finite values from 0 through 1.", "authoredProperties.statusBar.backgroundColor", node.Id);
        }

        void ValidateTemplate(UiNode node)
        {
            if (node.BlizzardTemplate is null)
                return;
            if (string.IsNullOrWhiteSpace(node.BlizzardTemplate))
            {
                Add("FFV2-TEMPLATE-001", "A Blizzard template identity cannot be empty.",
                    "blizzardTemplate", node.Id);
                return;
            }
            if (node.Kind != UiNodeKind.Button)
            {
                Add("FFV2-TEMPLATE-002",
                    $"Template '{node.BlizzardTemplate}' is a Button template and is incompatible with {node.Kind}.",
                    "blizzardTemplate", node.Id);
                return;
            }
            if (registry is null)
            {
                Add("FFV2-TEMPLATE-003",
                    $"Template '{node.BlizzardTemplate}' requires an explicit build-12340 registry snapshot.",
                    "blizzardTemplate", node.Id);
                return;
            }

            var template = registry.Resolve(node.BlizzardTemplate);
            if (template is null)
            {
                Add("FFV2-TEMPLATE-001",
                    $"Template '{node.BlizzardTemplate}' is unknown to this registry snapshot.",
                    "blizzardTemplate", node.Id);
                return;
            }
            if (!string.Equals(template.Definition.NativeType, "Button", StringComparison.Ordinal))
            {
                Add("FFV2-TEMPLATE-002",
                    $"Template '{node.BlizzardTemplate}' has native type '{template.Definition.NativeType}', which is incompatible with Button.",
                    "blizzardTemplate", node.Id);
                return;
            }
            foreach (var templateDiagnostic in template.Diagnostics
                         .Where(item => item.Severity == BlizzardTemplateDiagnosticSeverity.Error))
            {
                Add("FFV2-TEMPLATE-004",
                    $"Template '{node.BlizzardTemplate}' is unresolved: {templateDiagnostic.Code}: {templateDiagnostic.Message}",
                    "blizzardTemplate", node.Id);
            }
            foreach (var dependency in template.AssetDependencies.Where(item => !item.IsResolved))
            {
                Add("FFV2-TEMPLATE-005",
                    $"Template '{node.BlizzardTemplate}' requires unresolved asset '{dependency.LogicalPath}': {dependency.Diagnostic ?? "no diagnostic supplied"}.",
                    "blizzardTemplate", node.Id);
            }

            var effective = UiTemplateEffectiveProperties.Resolve(node, registry);
            if (effective.Values.Frame?.Width is null || effective.Values.Frame.Height is null)
            {
                Add("FFV2-TEMPLATE-006",
                    $"Template '{node.BlizzardTemplate}' and the authored overrides do not provide both required dimensions.",
                    "authoredProperties.frame", node.Id);
            }
        }

        void ValidateOwnershipCycles()
        {
            foreach (var start in document.Nodes)
            {
                var path = new HashSet<SemanticId>();
                var current = start;
                while (current.Owner.Kind == OwnerKind.LocalNode && nodesById.TryGetValue(current.Owner.Id, out var owner))
                {
                    if (!path.Add(current.Id))
                    {
                        Add("FFV2-OWNER-011", $"Ownership cycle detected at node '{current.DisplayLabel}'.", "owner", current.Id);
                        break;
                    }
                    current = owner;
                }
            }
        }

        void Add(string code, string message, string propertyPath, SemanticId? nodeId = null) =>
            diagnostics.Add(new UiDiagnostic
            {
                Code = code,
                Severity = DiagnosticSeverity.Error,
                Message = message,
                NodeId = nodeId,
                PropertyPath = propertyPath,
            });
    }

    private static bool IsPositiveFinite(double value) => value > 0 && double.IsFinite(value);

    private static bool IsValidColor(UiColor color) =>
        new[] { color.Red, color.Green, color.Blue, color.Alpha }
            .All(value => double.IsFinite(value) && value is >= 0 and <= 1);

    private static bool IsGlobalName(string value) =>
        !string.IsNullOrWhiteSpace(value) && GlobalNamePattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex GlobalNamePattern();
}
