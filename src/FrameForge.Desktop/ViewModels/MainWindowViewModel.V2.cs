using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core.Models;
using FrameForge.Core;
using FrameForge.Core.Serialization;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Templates;
using FrameForge.Core.Export;
using FrameForge.Core.Geometry;
using FrameForge.Core.Viewing;
using V2TreeFilter = FrameForge.Core.Viewing.TreeFilter;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Services;

namespace FrameForge.Desktop.ViewModels;

public sealed record V2OwnerOption(OwnerReference Owner, string Label)
{
    public override string ToString() => Label;
}

public sealed record V2AnchorTargetOption(AnchorTarget Target, string Label)
{
    public override string ToString() => Label;
}

public sealed record V2TemplateOption(string? Identity, string Label)
{
    public override string ToString() => Label;
}

public sealed record V2PreviewStateOption(PreviewButtonState State, string Label)
{
    public override string ToString() => Label;
}

public sealed record V2PresentationStateOption(SemanticId? Id, string Label)
{
    public bool IsXmlDefaults => Id is null;
    public override string ToString() => Label;
}

public enum V2HierarchyFilter { All, ReferenceOnly, AuthoredOnly }

public sealed partial class MainWindowViewModel
{
    private BlizzardTemplateRegistry? _v2TemplateRegistry;
    private int _v2TemplateLoadGeneration;
    private SemanticEditingSession? _v2Session;
    private ResolvedUiLayout? _v2Layout;
    private bool _loadingV2;
    private bool _refreshingV2Inspector;
    private bool _refreshingV2PresentationStates;
    private int _v2TreeVisibleCount;
    private bool _v2TreeFiltering;
    private readonly object _v2InspectorGate = new();
    public UiDocument? V2Document => _v2Session?.Document;
    public SemanticSelection V2Selection => _v2Session?.Selection ?? SemanticSelection.Empty;
    public ResolvedUiLayout? V2Layout => _v2Layout;
    public BlizzardTemplateRegistry? V2TemplateRegistry => _v2TemplateRegistry;
    public Task V2TemplateRegistryLoadingTask { get; private set; } = Task.CompletedTask;
    public string V2TemplateRegistryStatus { get; private set; } = "No build-12340 registry snapshot is loaded.";
    public bool IsV2Project => _v2Session is not null;
    public bool IsV1Project => _v2Session is null;
    public bool IsV2DesignMode => IsV2Project && ViewMode != CanvasViewMode.PREVIEW;
    public bool IsV2PreviewMode => IsV2Project && ViewMode == CanvasViewMode.PREVIEW;
    public bool HasV2NodeSelection => SelectedV2Node is not null;
    public bool IsV2RootSelected => V2Selection.PrimaryId is { } id && V2Document?.CompositionRoots.Any(root => root.Id == id) == true;
    public UiNode? SelectedV2Node => V2Selection.PrimaryId is { } id
        ? V2Document?.Nodes.FirstOrDefault(node => node.Id == id)
        : null;
    public bool CanUndoV2 => _v2Session?.CanUndo == true;
    public bool CanRedoV2 => _v2Session?.CanRedo == true;
    public string V2UndoLabel => _v2Session?.UndoDescription is { } value ? $"Undo {value}" : "Undo";
    public string V2RedoLabel => _v2Session?.RedoDescription is { } value ? $"Redo {value}" : "Redo";
    public bool V2SelectionLocked => SelectedV2Node?.Editor?.Locked == true;
    public string V2LockActionLabel => V2SelectionLocked ? "Unlock" : "Lock";
    public SemanticReferenceComposition? SelectedV2Reference => SelectedV2Node?.Editor?.ReferenceCompositionId is { } id
        ? V2Document?.Editor?.ReferenceCompositions.FirstOrDefault(item => item.Id == id)
        : null;
    public bool HasSelectedV2Reference => SelectedV2Reference is not null;
    public bool CanDeleteSelectedV2 => SelectedV2Node is { Editor.ReferenceOnly: not true };
    public bool SelectedV2ReferenceLocked => SelectedV2Reference is { } reference &&
        V2Document?.Editor?.Groups.FirstOrDefault(group => group.Id == reference.LockGroupId)?.Locked == true;
    public string V2ReferenceLockLabel => SelectedV2ReferenceLocked ? "Unlock reference" : "Lock reference";
    public bool SelectedV2ReferenceHidden => SelectedV2Node is { } node && IsReferenceEffectivelyHidden(node.Id);
    public string V2ReferenceVisibilityLabel => SelectedV2ReferenceHidden ? "Show element" : "Hide element";
    public IReadOnlySet<string> V2LockedNames => V2Document is { } document
        ? document.Nodes.Where(node => UiDocumentEditor.IsEditingLocked(document, node.Id))
            .Select(node => node.Id.Value).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);
    public string V2ProjectSummary => V2Document is not { } document
        ? string.Empty
        : $"Schema v2 · WoW 3.3.5a build 12340 · {document.Nodes.Count} control(s)";
    public string V2RootSummary => V2Document?.CompositionRoots.SingleOrDefault() is { } root
        ? $"{root.RuntimeName} → host {root.ExternalHostName} · {Number(root.DesignWidth)} × {Number(root.DesignHeight)} · {root.Sizing.Kind}"
        : "No valid composition root";
    public string V2DiagnosticsSummary => V2Diagnostics.Count == 0
        ? "Semantic graph valid"
        : $"{V2Diagnostics.Count} semantic diagnostic(s)";
    public string V2SelectedKind => SelectedV2Node?.Kind.ToString() ?? string.Empty;
    public bool V2IsTexture => SelectedV2Node?.Kind == UiNodeKind.Texture;
    public bool V2IsFontString => SelectedV2Node?.Kind == UiNodeKind.FontString;
    public bool V2IsButton => SelectedV2Node?.Kind == UiNodeKind.Button;
    public bool V2IsStatusBar => SelectedV2Node?.Kind == UiNodeKind.StatusBar;
    public bool V2IsFrameNode => SelectedV2Node is { IsRegion: false };
    public bool V2IsRegionNode => SelectedV2Node is { IsRegion: true };
    public bool V2HasTemplate => SelectedV2Node?.BlizzardTemplate is not null;
    public string V2DimensionSummary => DimensionSummary();
    public string V2TemplateProvenanceSummary => TemplateProvenanceSummary();
    public string V2PreviewStateDiagnostic => PreviewStateDiagnostic();
    public string V2SelectedAssetDiagnostic => SelectedAssetDiagnostic();
    public string V2UnsupportedPropertiesNote =>
        "Unsupported Blizzard semantics remain unchanged and are diagnosed explicitly; the editor does not invent runtime behavior.";
    public string V2AssetCatalogStatus { get; private set; } = "Client asset catalog has not been indexed.";

    public ObservableCollection<UiDiagnostic> V2Diagnostics { get; } = [];
    public ObservableCollection<V2OwnerOption> V2OwnerOptions { get; } = [];
    public ObservableCollection<V2AnchorTargetOption> V2AnchorTargetOptions { get; } = [];
    public ObservableCollection<V2TemplateOption> V2TemplateOptions { get; } = [];
    public ObservableCollection<string> V2TemplateDiagnostics { get; } = [];
    public ObservableCollection<V2PresentationStateOption> V2PresentationStates { get; } = [];
    public IReadOnlyList<AnchorPoint> V2AnchorPoints { get; } = AnchorPoints.All;
    public IReadOnlyList<string> V2HorizontalJustifications { get; } = ["LEFT", "CENTER", "RIGHT"];
    public IReadOnlyList<string> V2VerticalJustifications { get; } = ["TOP", "MIDDLE", "BOTTOM"];
    public IReadOnlyList<V2PreviewStateOption> V2PreviewStates { get; } =
    [
        new(PreviewButtonState.Normal, "Normal"),
        new(PreviewButtonState.Pushed, "Pushed"),
        new(PreviewButtonState.Disabled, "Disabled"),
        new(PreviewButtonState.Highlighted, "Highlighted"),
        new(PreviewButtonState.Selected, "Selected (character tab)"),
    ];

    [ObservableProperty] private string _v2DisplayLabelDraft = string.Empty;
    [ObservableProperty] private string _v2RuntimeNameDraft = string.Empty;
    [ObservableProperty] private string _v2WidthDraft = string.Empty;
    [ObservableProperty] private string _v2HeightDraft = string.Empty;
    [ObservableProperty] private string _v2OffsetXDraft = string.Empty;
    [ObservableProperty] private string _v2OffsetYDraft = string.Empty;
    [ObservableProperty] private string _v2TextureDraft = string.Empty;
    [ObservableProperty] private bool _v2UseTexCoordsDraft;
    [ObservableProperty] private string _v2TexCoordLeftDraft = "0";
    [ObservableProperty] private string _v2TexCoordRightDraft = "1";
    [ObservableProperty] private string _v2TexCoordTopDraft = "0";
    [ObservableProperty] private string _v2TexCoordBottomDraft = "1";
    [ObservableProperty] private string _v2TextDraft = string.Empty;
    [ObservableProperty] private string _v2FontDraft = string.Empty;
    [ObservableProperty] private string _v2FontSizeDraft = string.Empty;
    [ObservableProperty] private string _v2JustifyHDraft = "LEFT";
    [ObservableProperty] private string _v2JustifyVDraft = "MIDDLE";
    [ObservableProperty] private string _v2TintDraft = string.Empty;
    [ObservableProperty] private string _v2StatusMinimumDraft = string.Empty;
    [ObservableProperty] private string _v2StatusMaximumDraft = string.Empty;
    [ObservableProperty] private string _v2StatusValueDraft = string.Empty;
    [ObservableProperty] private string _v2StatusTextureDraft = string.Empty;
    [ObservableProperty] private string _v2StatusFillColorDraft = string.Empty;
    [ObservableProperty] private string _v2StatusBackgroundColorDraft = string.Empty;
    [ObservableProperty] private bool _v2VisibleDraft = true;
    [ObservableProperty] private bool _v2ButtonEnabledDraft = true;
    [ObservableProperty] private V2TemplateOption? _v2TemplateDraft;
    [ObservableProperty] private V2PreviewStateOption? _v2PreviewStateDraft;
    [ObservableProperty] private AnchorPoint _v2PointDraft = AnchorPoint.CENTER;
    [ObservableProperty] private AnchorPoint _v2RelativePointDraft = AnchorPoint.CENTER;
    [ObservableProperty] private V2OwnerOption? _v2OwnerDraft;
    [ObservableProperty] private V2AnchorTargetOption? _v2AnchorTargetDraft;
    [ObservableProperty] private string _v2InspectorValidation = string.Empty;
    [ObservableProperty] private V2PresentationStateOption? _selectedV2PresentationState;
    [ObservableProperty] private V2HierarchyFilter _v2HierarchyScope;
    public IReadOnlyList<V2HierarchyFilter> V2HierarchyScopes { get; } = Enum.GetValues<V2HierarchyFilter>();

    partial void OnV2HierarchyScopeChanged(V2HierarchyFilter value)
    {
        if (IsV2Project) RebuildV2Tree();
    }

    public void NewV2Project()
    {
        var document = UiDocumentFactory.Create("NewDesignRoot", "ModuleUiHost", 1024, 768) with
        {
            Editor = new DocumentEditorMetadata
            {
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DocumentEditorMetadata.ProjectNameKey] = "Untitled v2 Project",
                },
            },
        };
        LoadV2(document, null, "New FrameForge 2.0 project created with one module-hosted composition root.");
        SetViewMode(CanvasViewMode.HYBRID);
        Select(document.CompositionRoots[0].Id.Value);
    }

    public bool NewV2DungeonFinderProject()
    {
        if (!_wowClient.IsValid)
        {
            Status = "Dungeon Finder requires a configured local WoW 3.3.5a build 12340 client. Open WoW Client settings and validate it first.";
            return false;
        }
        var source = _wowAssets.Materialize(V2DungeonFinderStarter.SourcePath, _wowClient);
        if (!source.Success || source.CachePath is null)
        {
            Status = $"The validated client could not provide {V2DungeonFinderStarter.SourcePath}: {source.Message} No substitute artwork was used.";
            return false;
        }
        var templateSources = new List<V2DungeonFinderTemplateSource>();
        foreach (var logicalPath in V2DungeonFinderStarter.TemplateSourcePaths)
        {
            var materialized = _wowAssets.Materialize(logicalPath, _wowClient);
            if (!materialized.Success || materialized.CachePath is null)
            {
                Status = $"Dungeon Finder template inheritance is incomplete: {logicalPath}: {materialized.Message} No substitute definitions were used.";
                return false;
            }
            templateSources.Add(new V2DungeonFinderTemplateSource(logicalPath, materialized.CachePath));
        }
        var created = V2DungeonFinderStarter.Create(source.CachePath, templateSources);
        if (!created.Success || created.Document is null)
        {
            Status = "Could not create the V2 Dungeon Finder reference: " + string.Join(" ", created.Errors);
            return false;
        }
        var references = created.Document.Nodes.SelectMany(node => new[]
            {
                node.AuthoredProperties.Texture?.TextureReference,
                node.AuthoredProperties.StatusBar?.TextureReference,
            }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = references.Select(reference => _wowAssets.Materialize(reference, _wowClient))
            .Where(result => !result.Success).ToArray();
        if (missing.Length > 0)
        {
            Status = $"Dungeon Finder artwork is incomplete in the configured client: {missing[0].RequestedPath}: {missing[0].Message}";
            return false;
        }
        LoadV2(created.Document, null,
            $"Created a protected V2 Dungeon Finder reference from the validated client ({references.Length} artwork asset(s)).");
        var openState = created.Document.Editor!.PreviewStates.Single();
        SelectedV2PresentationState = V2PresentationStates.Single(option => option.Id == openState.Id);
        SetViewMode(CanvasViewMode.HYBRID);
        var reference = created.Document.Editor.ReferenceCompositions.Single();
        SelectV2(reference.RootNodeId, false);
        Assets.Refresh();
        return true;
    }

    public void LoadV2(UiDocument document, string? path, string status)
    {
        SetV2Document(document);
        if (_v2TemplateRegistry is null)
            ReloadV2TemplateRegistry();
        _loadingV2 = true;
        try
        {
            // Keep the legacy workspace alive for schema-v1 commands without projecting the v2
            // graph into it. The active v2 tree, layout, canvas and inspector are refreshed below.
            Load(ProjectFactory.Empty(), path, status);
        }
        finally
        {
            _loadingV2 = false;
        }
        _v2Session?.ReplaceSelection(document.CompositionRoots.FirstOrDefault()?.Id);
        RefreshV2Presentation(status);
        RefreshV2Inspector();
    }

    public bool SaveV2ToFile(string path)
    {
        if (V2Document is not { } document)
            return false;
        if (!path.EndsWith(ProjectCodec.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            Status = $"Refused to save schema-v2 JSON to {Path.GetFileName(path)}. Use the {ProjectCodec.FileExtension} extension.";
            return false;
        }
        var diagnostics = UiDocumentValidator.Validate(document, _v2TemplateRegistry);
        PublishV2Diagnostics(diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            Status = "Save blocked by semantic validation: " + string.Join(" ", diagnostics.Take(3).Select(item => $"{item.Code}: {item.Message}"));
            return false;
        }
        try
        {
            File.WriteAllText(path, UiDocumentCodec.Serialize(document));
            ProjectPath = path;
            IsDirty = false;
            Status = $"Saved schema-v2 project {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    public void AddV2Control(UiNodeKind kind)
    {
        if (V2Document?.CompositionRoots.SingleOrDefault() is not { } root)
            return;
        var selected = SelectedV2Node;
        var owner = selected?.CanOwnChildren == true && selected.Editor?.ReferenceOnly != true
            ? OwnerReference.Node(selected.Id)
            : OwnerReference.Root(root.Id);
        var ordinal = V2Document.Nodes.Count(node => node.Kind == kind) + 1;
        while (root.RuntimeName == $"{kind}{ordinal}" || V2Document.Nodes.Any(node => node.RuntimeName == $"{kind}{ordinal}")) ordinal++;
        ExecuteV2($"Add {kind}", (editor, document) =>
                editor.CreateControl(document, kind, owner, $"{kind} {ordinal}", $"{kind}{ordinal}"),
            $"Added {kind} under {(selected?.CanOwnChildren == true && selected.Editor?.ReferenceOnly != true ? selected.DisplayLabel : "the composition root")}.",
            (selection, result) => selection.Replace(result.AffectedId));
    }

    public void DeleteV2Selection()
    {
        if (V2Document is null || SelectedV2Node is not { } node)
        {
            Status = IsV2RootSelected ? "The composition root is required and cannot be deleted." : "Select a v2 control to delete.";
            return;
        }
        var parentSelection = node.Owner.Id;
        var selected = V2Selection.OrderedIds.Where(id => V2Document.Nodes.Any(item => item.Id == id)).ToArray();
        ExecuteV2("Delete selection", (editor, document) => editor.DeleteSelection(document, selected),
            $"Deleted {selected.Length} selected control(s) and their owned subtrees.",
            (selection, _) => selection.Replace(parentSelection));
    }

    public void MoveV2SelectionInOrder(int delta)
    {
        if (V2Document is null || SelectedV2Node is not { } node)
            return;
        var siblings = node.Owner.Kind == OwnerKind.CompositionRoot
            ? V2Document.CompositionRoots.Single(root => root.Id == node.Owner.Id).Children
            : V2Document.Nodes.Single(owner => owner.Id == node.Owner.Id).Children;
        ExecuteV2("Reorder control", (editor, document) => editor.ReorderChild(document, node.Id, siblings.IndexOf(node.Id) + delta),
            delta < 0 ? "Moved control earlier in its owner." : "Moved control later in its owner.");
    }

    public void ApplyV2Inspector()
    {
        if (_refreshingV2Inspector || V2Document is null || SelectedV2Node is not { } original ||
            V2OwnerDraft is null || V2AnchorTargetDraft is null)
            return;
        if (!TryOptionalDouble(V2WidthDraft, "Width", out var width) ||
            !TryOptionalDouble(V2HeightDraft, "Height", out var height) ||
            !TryRequiredDouble(V2OffsetXDraft, "Offset X", out var offsetX) ||
            !TryRequiredDouble(V2OffsetYDraft, "Offset Y", out var offsetY))
            return;
        var requestedTemplate = V2TemplateDraft?.Identity;
        if (width is <= 0 || height is <= 0)
        {
            V2InspectorValidation = "Width and height must be positive when authored.";
            Status = "Edit rejected: Width and height must be positive when authored.";
            return;
        }
        if (requestedTemplate is null && (width is null || height is null))
        {
            V2InspectorValidation = "Width and height may be blank only while a resolved template supplies them.";
            return;
        }
        if (!TryOptionalDouble(V2StatusMinimumDraft, "Status minimum", out var minimum) ||
            !TryOptionalDouble(V2StatusMaximumDraft, "Status maximum", out var maximum) ||
            !TryOptionalDouble(V2StatusValueDraft, "Status value", out var statusValue) ||
            !TryOptionalDouble(V2FontSizeDraft, "Font size", out var fontSize) ||
            !TryTint(V2TintDraft, out var tint) ||
            !TryTint(V2StatusFillColorDraft, out var statusFillColor) ||
            !TryTint(V2StatusBackgroundColorDraft, out var statusBackgroundColor))
            return;
        if (fontSize is <= 0)
        {
            V2InspectorValidation = "Font size must be positive when authored.";
            return;
        }
        UiTexCoords? textureCoords = null;
        if (original.Kind == UiNodeKind.Texture && V2UseTexCoordsDraft)
        {
            if (!TryRequiredDouble(V2TexCoordLeftDraft, "TexCoord left", out var left) ||
                !TryRequiredDouble(V2TexCoordRightDraft, "TexCoord right", out var right) ||
                !TryRequiredDouble(V2TexCoordTopDraft, "TexCoord top", out var top) ||
                !TryRequiredDouble(V2TexCoordBottomDraft, "TexCoord bottom", out var bottom))
                return;
            textureCoords = new UiTexCoords(left, right, top, bottom);
            if (!textureCoords.IsValid)
            {
                V2InspectorValidation = "Texture coordinates must be ordered values from 0 through 1.";
                return;
            }
        }

        var movedOwner = original.Owner != V2OwnerDraft.Owner;
        ExecuteV2("Apply inspector properties", (editor, document) =>
        {
            var working = document;
            SemanticEditResult Step(SemanticEditResult result)
            {
                if (result.Success) working = result.Document;
                return result;
            }

            var result = Step(editor.RenameControl(working, original.Id, V2DisplayLabelDraft, V2RuntimeNameDraft));
            if (!result.Success) return result;
            if (!string.Equals(original.BlizzardTemplate, requestedTemplate, StringComparison.Ordinal))
            {
                result = Step(requestedTemplate is null
                    ? editor.ClearBlizzardTemplate(working, original.Id)
                    : editor.AssignBlizzardTemplate(working, original.Id, requestedTemplate));
                if (!result.Success) return result;
            }

            var node = working.Nodes.Single(item => item.Id == original.Id);
            var properties = node.AuthoredProperties;
            if (node.IsRegion)
                properties = properties with { Region = properties.Region! with { Width = width, Height = height, Tint = tint } };
            else
                properties = properties with { Frame = properties.Frame! with { Width = width, Height = height, Visible = V2VisibleDraft } };
            properties = node.Kind switch
            {
                UiNodeKind.Texture => properties with { Texture = properties.Texture! with
                    { TextureReference = EmptyToNull(V2TextureDraft), TexCoords = textureCoords } },
                UiNodeKind.FontString => properties with { FontString = properties.FontString! with
                    { Text = V2TextDraft, FontReference = EmptyToNull(V2FontDraft), FontSize = fontSize,
                        JustifyH = V2JustifyHDraft, JustifyV = V2JustifyVDraft } },
                UiNodeKind.Button => properties with { Button = properties.Button! with
                    { Enabled = V2ButtonEnabledDraft, Text = V2TextDraft } },
                UiNodeKind.StatusBar => properties with { StatusBar = properties.StatusBar! with
                    { Minimum = minimum, Maximum = maximum, Value = statusValue,
                        TextureReference = EmptyToNull(V2StatusTextureDraft), FillColor = statusFillColor,
                        BackgroundColor = statusBackgroundColor } },
                _ => properties,
            };
            result = Step(editor.UpdateProperties(working, original.Id, properties));
            if (!result.Success) return result;

            var currentAnchor = working.Nodes.Single(item => item.Id == original.Id).Anchors[0];
            var anchor = currentAnchor with { Point = V2PointDraft, RelativePoint = V2RelativePointDraft,
                Target = V2AnchorTargetDraft.Target, OffsetX = offsetX, OffsetY = offsetY };
            result = Step(editor.UpdateAnchors(working, original.Id,
                [anchor, .. working.Nodes.Single(item => item.Id == original.Id).Anchors.Skip(1)]));
            if (!result.Success) return result;

            var currentNode = working.Nodes.Single(item => item.Id == original.Id);
            if (currentNode.Owner != V2OwnerDraft.Owner)
            {
                result = Step(editor.ChangeOwnership(working, original.Id, V2OwnerDraft.Owner, preserveVisualPosition: true));
                if (!result.Success) return result;
            }
            return result with { Document = working, Changed = true, AffectedId = original.Id };
        }, movedOwner ? "Applied v2 properties and moved the control while preserving its visual position." : "Applied v2 properties.");
    }

    private void ExecuteV2(string description,
        Func<UiDocumentEditor, UiDocument, SemanticEditResult> command, string successStatus,
        Func<SemanticSelection, SemanticEditResult, SemanticSelection>? selection = null)
    {
        if (_v2Session is null) return;
        if (ViewMode == CanvasViewMode.PREVIEW)
        {
            Status = "Preview mode is read-only. Switch to Design to edit the semantic document.";
            return;
        }
        ApplyV2Change(_v2Session.Execute(description, command, selection), successStatus);
    }

    private void ApplyV2Change(SemanticSessionChange change, string successStatus)
    {
        PublishV2Diagnostics(change.Diagnostics);
        if (!change.Success)
        {
            var error = string.Join(" ", change.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
            V2InspectorValidation = error;
            Status = string.IsNullOrWhiteSpace(error) ? change.Description : "Edit rejected: " + error;
            return;
        }
        if (change.Changed)
        {
            IsDirty = true;
            ConfigureAssets();
        }
        V2InspectorValidation = string.Empty;
        RefreshV2Presentation(successStatus);
        RefreshV2Inspector();
    }

    private void SetV2Document(UiDocument? document)
    {
        if (document is null && _v2Session is null) return;
        _v2Session = document is null ? null : new SemanticEditingSession(document, _v2TemplateRegistry);
        OnPropertyChanged(nameof(V2Document));
        OnPropertyChanged(nameof(V2Selection));
        if (document is null)
        {
            _v2Layout = null;
            OnPropertyChanged(nameof(V2Layout));
        }
        OnPropertyChanged(nameof(IsV2Project));
        OnPropertyChanged(nameof(IsV1Project));
        OnPropertyChanged(nameof(IsV2DesignMode));
        OnPropertyChanged(nameof(IsV2PreviewMode));
        OnPropertyChanged(nameof(ShowV1DesignTools));
        OnPropertyChanged(nameof(ShowV2DesignTools));
        OnPropertyChanged(nameof(ShowV1InspectTools));
        OnPropertyChanged(nameof(V2ProjectSummary));
        OnPropertyChanged(nameof(V2RootSummary));
        OnPropertyChanged(nameof(V2TemplateRegistry));
        OnPropertyChanged(nameof(ProjectDescription));
        NotifyV2History();
        NotifyV2Selection();
    }

    private void PublishV2Diagnostics(IEnumerable<UiDiagnostic> diagnostics)
    {
        V2Diagnostics.Clear();
        foreach (var diagnostic in diagnostics) V2Diagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(V2DiagnosticsSummary));
    }

    private void RefreshV2Inspector()
    {
        lock (_v2InspectorGate)
            RefreshV2InspectorCore();
    }

    private void RefreshV2InspectorCore()
    {
        if (V2Document is not { } document)
        {
            NotifyV2Selection();
            return;
        }
        _refreshingV2Inspector = true;
        try
        {
            V2OwnerOptions.Clear();
            V2AnchorTargetOptions.Clear();
            V2TemplateOptions.Clear();
            V2TemplateOptions.Add(new V2TemplateOption(null, "None"));
            var root = document.CompositionRoots.SingleOrDefault();
            if (root is not null)
            {
                V2OwnerOptions.Add(new V2OwnerOption(OwnerReference.Root(root.Id), $"Composition Root · {root.RuntimeName}"));
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.Parent(), "Parent"));
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.Root(), "Composition Root"));
            }
            var selected = SelectedV2Node;
            if (selected?.Kind == UiNodeKind.Button && _v2TemplateRegistry is { } registry)
                foreach (var identity in BlizzardTemplateRegistry.ApprovedTemplates
                             .Where(name => registry.Resolve(name)?.IsResolved == true))
                    V2TemplateOptions.Add(new V2TemplateOption(identity, identity));
            foreach (var candidate in document.Nodes.Where(node => node.CanOwnChildren && node.Id != selected?.Id))
                V2OwnerOptions.Add(new V2OwnerOption(OwnerReference.Node(candidate.Id), candidate.DisplayLabel));
            foreach (var candidate in document.Nodes.Where(node => node.Id != selected?.Id))
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.Local(candidate.Id), $"Local · {candidate.DisplayLabel}"));
            foreach (var external in document.ExternalReferences)
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.External(external.GlobalName), $"External · {external.GlobalName}"));

            if (selected is null)
            {
                V2DisplayLabelDraft = string.Empty;
                V2RuntimeNameDraft = string.Empty;
                return;
            }
            var dimensions = selected.IsRegion
                ? (selected.AuthoredProperties.Region?.Width, selected.AuthoredProperties.Region?.Height)
                : (selected.AuthoredProperties.Frame?.Width, selected.AuthoredProperties.Frame?.Height);
            var anchor = selected.Anchors.FirstOrDefault();
            V2DisplayLabelDraft = selected.DisplayLabel;
            V2RuntimeNameDraft = selected.RuntimeName ?? string.Empty;
            V2WidthDraft = FormatOptional(dimensions.Item1);
            V2HeightDraft = FormatOptional(dimensions.Item2);
            V2OffsetXDraft = FormatNumber(anchor?.OffsetX ?? 0);
            V2OffsetYDraft = FormatNumber(anchor?.OffsetY ?? 0);
            V2PointDraft = anchor?.Point ?? AnchorPoint.CENTER;
            V2RelativePointDraft = anchor?.RelativePoint ?? AnchorPoint.CENTER;
            V2OwnerDraft = V2OwnerOptions.FirstOrDefault(option => option.Owner == selected.Owner);
            V2AnchorTargetDraft = V2AnchorTargetOptions.FirstOrDefault(option => anchor is not null && SameTarget(option.Target, anchor.Target));
            V2TextureDraft = selected.AuthoredProperties.Texture?.TextureReference ?? string.Empty;
            var texCoords = selected.AuthoredProperties.Texture?.TexCoords;
            V2UseTexCoordsDraft = texCoords is not null;
            V2TexCoordLeftDraft = FormatNumber(texCoords?.Left ?? 0);
            V2TexCoordRightDraft = FormatNumber(texCoords?.Right ?? 1);
            V2TexCoordTopDraft = FormatNumber(texCoords?.Top ?? 0);
            V2TexCoordBottomDraft = FormatNumber(texCoords?.Bottom ?? 1);
            V2TextDraft = selected.Kind == UiNodeKind.Button
                ? selected.AuthoredProperties.Button?.Text ?? string.Empty
                : selected.AuthoredProperties.FontString?.Text ?? string.Empty;
            V2FontDraft = selected.AuthoredProperties.FontString?.FontReference ?? string.Empty;
            V2FontSizeDraft = FormatOptional(selected.AuthoredProperties.FontString?.FontSize);
            V2JustifyHDraft = selected.AuthoredProperties.FontString?.JustifyH ?? "LEFT";
            V2JustifyVDraft = selected.AuthoredProperties.FontString?.JustifyV ?? "MIDDLE";
            V2TintDraft = selected.AuthoredProperties.Region?.Tint is { } tint
                ? string.Join(",", FormatNumber(tint.Red), FormatNumber(tint.Green), FormatNumber(tint.Blue), FormatNumber(tint.Alpha))
                : string.Empty;
            V2VisibleDraft = selected.AuthoredProperties.Frame?.Visible ?? true;
            V2ButtonEnabledDraft = selected.AuthoredProperties.Button?.Enabled ?? true;
            V2TemplateDraft = V2TemplateOptions.FirstOrDefault(option => option.Identity == selected.BlizzardTemplate)
                              ?? new V2TemplateOption(selected.BlizzardTemplate, selected.BlizzardTemplate ?? "None");
            V2PreviewStateDraft ??= V2PreviewStates[0];
            V2StatusMinimumDraft = FormatOptional(selected.AuthoredProperties.StatusBar?.Minimum);
            V2StatusMaximumDraft = FormatOptional(selected.AuthoredProperties.StatusBar?.Maximum);
            V2StatusValueDraft = FormatOptional(selected.AuthoredProperties.StatusBar?.Value);
            V2StatusTextureDraft = selected.AuthoredProperties.StatusBar?.TextureReference ?? string.Empty;
            V2StatusFillColorDraft = FormatColor(selected.AuthoredProperties.StatusBar?.FillColor);
            V2StatusBackgroundColorDraft = FormatColor(selected.AuthoredProperties.StatusBar?.BackgroundColor);
            V2InspectorValidation = string.Empty;
        }
        finally
        {
            _refreshingV2Inspector = false;
            NotifyV2Selection();
        }
    }

    private void NotifyV2Selection()
    {
        OnPropertyChanged(nameof(SelectedV2Node));
        OnPropertyChanged(nameof(HasV2NodeSelection));
        OnPropertyChanged(nameof(IsV2RootSelected));
        OnPropertyChanged(nameof(V2SelectedKind));
        OnPropertyChanged(nameof(V2IsTexture));
        OnPropertyChanged(nameof(V2IsFontString));
        OnPropertyChanged(nameof(V2IsButton));
        OnPropertyChanged(nameof(V2IsStatusBar));
        OnPropertyChanged(nameof(V2IsFrameNode));
        OnPropertyChanged(nameof(V2IsRegionNode));
        OnPropertyChanged(nameof(V2HasTemplate));
        OnPropertyChanged(nameof(V2DimensionSummary));
        OnPropertyChanged(nameof(V2TemplateProvenanceSummary));
        OnPropertyChanged(nameof(V2PreviewStateDiagnostic));
        OnPropertyChanged(nameof(V2SelectedAssetDiagnostic));
        OnPropertyChanged(nameof(V2LockedNames));
        OnPropertyChanged(nameof(V2Selection));
        OnPropertyChanged(nameof(V2SelectionLocked));
        OnPropertyChanged(nameof(V2LockActionLabel));
        OnPropertyChanged(nameof(SelectedV2Reference));
        OnPropertyChanged(nameof(HasSelectedV2Reference));
        OnPropertyChanged(nameof(CanDeleteSelectedV2));
        OnPropertyChanged(nameof(SelectedV2ReferenceLocked));
        OnPropertyChanged(nameof(V2ReferenceLockLabel));
        OnPropertyChanged(nameof(SelectedV2ReferenceHidden));
        OnPropertyChanged(nameof(V2ReferenceVisibilityLabel));
    }

    private void NotifyV2History()
    {
        OnPropertyChanged(nameof(CanUndoV2));
        OnPropertyChanged(nameof(CanRedoV2));
        OnPropertyChanged(nameof(V2UndoLabel));
        OnPropertyChanged(nameof(V2RedoLabel));
    }

    public void UndoV2()
    {
        if (_v2Session is null) return;
        if (ViewMode == CanvasViewMode.PREVIEW) { Status = "Preview mode is read-only. Switch to Design to undo edits."; return; }
        ApplyV2Change(_v2Session.Undo(), $"Undid {_v2Session.RedoDescription ?? "v2 edit"}.");
    }

    public void RedoV2()
    {
        if (_v2Session is null) return;
        if (ViewMode == CanvasViewMode.PREVIEW) { Status = "Preview mode is read-only. Switch to Design to redo edits."; return; }
        var description = _v2Session.RedoDescription ?? "v2 edit";
        ApplyV2Change(_v2Session.Redo(), $"Redid {description}.");
    }

    public void SetV2DesignMode(bool design)
    {
        if (!IsV2Project) return;
        if (!design) CancelV2Gesture();
        SetViewMode(design ? CanvasViewMode.HYBRID : CanvasViewMode.PREVIEW);
        OnPropertyChanged(nameof(IsV2DesignMode));
        OnPropertyChanged(nameof(IsV2PreviewMode));
    }

    public void ToggleSelectedV2ReferenceLock()
    {
        if (SelectedV2Reference is not { } reference) return;
        ExecuteV2(SelectedV2ReferenceLocked ? "Unlock Blizzard reference" : "Lock Blizzard reference",
            (editor, document) => editor.SetReferenceLocked(document, reference.Id, !SelectedV2ReferenceLocked),
            $"{(SelectedV2ReferenceLocked ? "Unlocked" : "Locked")} '{reference.Name}'. Reference edits remain editor-only and are never exported.");
    }

    public void RestoreSelectedV2Reference()
    {
        if (SelectedV2Reference is not { } reference) return;
        ExecuteV2("Restore Blizzard reference", (editor, document) => editor.RestoreReferenceComposition(document, reference.Id),
            $"Restored '{reference.Name}' to its client-derived reference layout.");
    }

    public async Task<IReadOnlyList<StockTextureEntry>> V2TextureChoicesAsync()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<StockTextureEntry>();
        foreach (var entry in StockTextureCatalog.Curated)
            if (seen.Add(entry.InterfacePath)) entries.Add(entry);
        foreach (var reference in EnumerateAssetReferences().Where(IsWowClientReference))
            if (seen.Add(reference)) entries.Add(new(reference.Replace('\\', '/'), "Used by this project", "Already referenced by this design."));
        foreach (var asset in V2Document?.Editor?.ProjectAssets ?? [])
            if (seen.Add(asset.PreparedReference))
                entries.Add(new(asset.PreparedReference, "Project artwork",
                    $"Source: {asset.SourceReference} · {asset.ConversionStatus} · {asset.ValidationStatus}"));
        if (_wowAssets is IWoWClientAssetCatalogProvider catalogProvider)
        {
            var catalog = await Task.Run(() => catalogProvider.DiscoverTextures(_wowClient));
            V2AssetCatalogStatus = catalog.Diagnostic;
            foreach (var path in catalog.Paths)
                if (seen.Add(path)) entries.Add(new(path.Replace('\\', '/'), "Client listfile", "Discovered from the configured client; materialized only when previewed."));
        }
        else
        {
            V2AssetCatalogStatus = "This asset provider cannot enumerate MPQ listfiles; showing curated, used, and cached assets only.";
        }
        EnumerateCachedStockTextures(seen, entries);
        OnPropertyChanged(nameof(V2AssetCatalogStatus));
        return entries.OrderBy(entry => entry.InterfacePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool AssignSelectedV2ClientTexture(string reference)
    {
        if (SelectedV2Node is not { Kind: UiNodeKind.Texture } node) return false;
        if (!WoWClientAssetProvider.TryNormalizeInterfacePath(reference, out _, out var error))
        {
            Status = error;
            return false;
        }
        var normalized = reference.Replace('/', '\\');
        ExecuteV2("Assign client texture", (editor, document) => editor.AssignTexture(document, node.Id, normalized),
            $"Assigned Blizzard client texture {normalized}; no artwork was copied or bundled.");
        return string.Equals(SelectedV2Node?.AuthoredProperties.Texture?.TextureReference, normalized, StringComparison.Ordinal);
    }

    public bool AssignSelectedV2Texture(string reference)
    {
        if (IsWowClientReference(reference)) return AssignSelectedV2ClientTexture(reference);
        if (SelectedV2Node is not { Kind: UiNodeKind.Texture } node ||
            V2Document?.Editor?.ProjectAssets.FirstOrDefault(asset =>
                string.Equals(asset.PreparedReference, reference, StringComparison.Ordinal)) is not { } asset)
        {
            Status = "The selected project artwork is not registered in this v2 project.";
            return false;
        }
        ExecuteV2("Assign project texture", (editor, document) => editor.AssignTexture(document, node.Id, asset.PreparedReference),
            $"Assigned prepared project artwork {asset.PreparedReference}.");
        return string.Equals(SelectedV2Node?.AuthoredProperties.Texture?.TextureReference,
            asset.PreparedReference, StringComparison.Ordinal);
    }

    public bool ImportSelectedV2Texture(string path, bool importExternal)
    {
        if (SelectedV2Node is not { Kind: UiNodeKind.Texture } node) return false;
        if (!TryMakeProjectAssetReference(path, importExternal, out var sourceReference, out var error))
        {
            Status = error;
            return false;
        }
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
        var sourcePhysical = Path.GetFullPath(Path.Combine(projectDirectory,
            sourceReference.Replace('/', Path.DirectorySeparatorChar)));
        DecodedImageData decoded;
        TextureFileFormat sourceFormat;
        try
        {
            using var sourceStream = File.OpenRead(sourcePhysical);
            var decoders = new TextureDecoderRegistry();
            sourceFormat = decoders.Identify(sourceStream);
            if (sourceFormat == TextureFileFormat.Unknown)
                throw new InvalidDataException("The selected file is not a supported PNG, TGA, or BLP image.");
            decoded = decoders.Decode(sourceStream, sourceFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Status = $"Imported artwork could not be decoded: {ex.Message}";
            return false;
        }

        var runtimeReference = sourceReference;
        var conversion = "ready";
        if (sourceFormat == TextureFileFormat.Png)
        {
            try
            {
                var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePhysical))).ToLowerInvariant();
                var stem = SafeAssetStem(Path.GetFileNameWithoutExtension(sourceReference));
                var preparedDirectory = Path.Combine(projectDirectory, "assets", "prepared");
                Directory.CreateDirectory(preparedDirectory);
                var preparedPath = Path.Combine(preparedDirectory, $"{stem}-{sourceHash[..12]}.tga");
                if (!File.Exists(preparedPath))
                {
                    using var stream = File.Create(preparedPath);
                    WowTgaEncoder.Write(stream, decoded);
                }
                runtimeReference = Path.GetRelativePath(projectDirectory, preparedPath).Replace('\\', '/');
                conversion = "prepared-as-uncompressed-32-bit-tga";
                ConfigureAssets();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Status = $"PNG was imported for preview but TGA preparation failed: {ex.Message}";
                return false;
            }
        }

        var preparedPathPhysical = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!, runtimeReference.Replace('/', Path.DirectorySeparatorChar));
        var preparedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(preparedPathPhysical))).ToLowerInvariant();
        var extension = Path.GetExtension(runtimeReference).ToLowerInvariant();
        var outputName = $"{SafeAssetStem(Path.GetFileNameWithoutExtension(runtimeReference))}-{preparedHash[..12]}{extension}";
        var metadata = new SemanticProjectAsset
        {
            Id = SemanticId.New(),
            SourceReference = sourceReference,
            PreviewReference = sourceReference,
            PreparedReference = runtimeReference,
            IntendedClientPath = @"Interface\FrameForge\Artwork\" + outputName,
            Width = decoded.Width,
            Height = decoded.Height,
            Format = sourceFormat.ToString().ToUpperInvariant(),
            ConversionStatus = conversion,
            ValidationStatus = "decoded-and-export-header-validated",
        };
        ExecuteV2("Import project artwork", (editor, document) => editor.AssignTexture(document, node.Id, runtimeReference, metadata),
            $"Imported {sourceReference}; prepared runtime artwork is {runtimeReference} ({decoded.Width} × {decoded.Height}).");
        return string.Equals(SelectedV2Node?.AuthoredProperties.Texture?.TextureReference, runtimeReference, StringComparison.Ordinal);
    }

    private static string SafeAssetStem(string value)
    {
        var safe = Regex.Replace(value.Trim(), "[^A-Za-z0-9_-]", "_").Trim('_').ToLowerInvariant();
        return safe.Length == 0 ? "asset" : safe;
    }

    public void ToggleSelectedV2ReferenceVisibility(bool subtree)
    {
        if (SelectedV2Node is not { Editor.ReferenceOnly: true } node) return;
        var show = IsReferenceEffectivelyHidden(node.Id);
        ExecuteV2(show ? "Show Blizzard reference element" : "Hide Blizzard reference element",
            (editor, document) => editor.SetReferenceVisibility(document, node.Id, show, subtree),
            show
                ? $"Restored editor visibility for '{node.DisplayLabel}'. A hidden ancestor may still suppress it."
                : $"Hidden {(subtree ? "the reference subtree" : "the reference element")} '{node.DisplayLabel}' in Design and Preview only. WoW runtime visibility is unchanged.");
    }

    private bool IsReferenceEffectivelyHidden(SemanticId id)
    {
        if (V2Document is not { } document) return false;
        var hidden = document.Editor?.HiddenReferenceNodes.ToHashSet() ?? [];
        var nodes = document.Nodes.ToDictionary(node => node.Id);
        for (var current = id; ;)
        {
            if (hidden.Contains(current)) return true;
            if (!nodes.TryGetValue(current, out var node) || node.Owner.Kind != OwnerKind.LocalNode) return false;
            current = node.Owner.Id;
        }
    }

    partial void OnSelectedV2PresentationStateChanged(V2PresentationStateOption? value)
    {
        if (!_refreshingV2PresentationStates && IsV2Project)
            RefreshV2Presentation(value?.IsXmlDefaults == false
                ? $"Preview simulation: {value.Label}. Authored FrameXML is unchanged."
                : "Preview simulation cleared; showing authored XML defaults.");
    }

    public void ToggleV2Lock()
    {
        if (SelectedV2Node is not { } node) return;
        var locked = node.Editor?.Locked != true;
        ExecuteV2(locked ? "Lock control" : "Unlock control",
            (editor, document) => editor.SetLocked(document, node.Id, locked),
            locked ? $"Locked {node.DisplayLabel}." : $"Unlocked {node.DisplayLabel}.");
    }

    public void MoveV2SelectionBy(double deltaX, double deltaY)
    {
        if (SelectedV2Node is null) return;
        ExecuteV2("Move selection", (editor, document) => editor.MoveSelection(document, V2Selection.OrderedIds, deltaX, deltaY),
            $"Moved {V2Selection.Count} selected control(s); authored anchors remain typed.");
    }

    public void SelectV2(SemanticId? id, bool additive)
    {
        if (_v2Session is null) return;
        if (additive && id is { } value) _v2Session.ToggleSelection(value);
        else _v2Session.ReplaceSelection(id);
        RefreshV2Presentation(null);
        RefreshV2Inspector();
    }

    public void CollapseAllV2References()
    {
        if (V2Document is not { } document) return;
        foreach (var node in document.Nodes.Where(node => node.Editor?.ReferenceOnly == true))
            _expandedTreeNames.Remove(node.Id.Value);
        RebuildV2Tree();
        Status = "Collapsed all Blizzard reference subtrees; authored hierarchy is unchanged.";
    }

    public void ExpandSelectedV2Reference()
    {
        if (SelectedV2Node is not { } selected || V2Document is not { } document) return;
        var nodes = document.Nodes.ToDictionary(node => node.Id);
        for (var current = selected; ;)
        {
            _expandedTreeNames.Add(current.Id.Value);
            if (current.Owner.Kind != OwnerKind.LocalNode || !nodes.TryGetValue(current.Owner.Id, out current!)) break;
        }
        RebuildV2Tree();
        Status = $"Expanded the path to '{selected.DisplayLabel}'.";
    }

    public void DragV2Frame(SemanticId id, double deltaX, double deltaY) => ApplyV2DragDelta(id, deltaX, deltaY);

    public void CancelV2Drag()
    {
        CancelV2Gesture();
    }

    partial void OnSelectedNameChanged(string? value) => RefreshV2Inspector();

    public PreviewButtonState V2PreviewButtonState => V2PreviewStateDraft?.State ?? PreviewButtonState.Normal;

    partial void OnV2PreviewStateDraftChanged(V2PreviewStateOption? value) =>
        NotifyV2PreviewState();

    private void NotifyV2PreviewState()
    {
        OnPropertyChanged(nameof(V2PreviewButtonState));
        OnPropertyChanged(nameof(V2PreviewStateDiagnostic));
    }

    public void ClearV2TemplateOverrides()
    {
        if (V2Document is null || SelectedV2Node is not { } node) return;
        ExecuteV2("Clear template overrides",
            (editor, document) => editor.ClearTemplateEligibleOverrides(document, node.Id),
            "Cleared authored width and height overrides; template-derived dimensions are effective.");
    }

    public V2FrameXmlExportResult? ExportV2(string destination)
    {
        if (V2Document is not { } document) return null;
        var result = V2FrameXmlExporter.Export(document,
            string.IsNullOrWhiteSpace(ProjectPath) ? null : ProjectPath, destination, _v2TemplateRegistry);
        Status = result.Summary + (result.Success ? $" Output: {destination}" : " " +
            string.Join(" ", result.Plan.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
                .Take(3).Select(item => $"{item.Code}: {item.Message}")));
        return result;
    }

    private void ReloadV2TemplateRegistry()
    {
        var generation = ++_v2TemplateLoadGeneration;
        _v2TemplateRegistry = null;
        _v2Session?.SetTemplateRegistry(null);
        V2TemplateDiagnostics.Clear();
        V2TemplateRegistryStatus = _wowClient.IsValid
            ? "Loading the build-12340 template registry…"
            : "Select a valid WoW 3.3.5a build-12340 client to load templates.";
        OnPropertyChanged(nameof(V2TemplateRegistry));
        OnPropertyChanged(nameof(V2TemplateRegistryStatus));
        if (!_wowClient.IsValid || !IsV2Project)
        {
            RefreshV2Inspector();
            V2TemplateRegistryLoadingTask = Task.CompletedTask;
            return;
        }
        var client = _wowClient;
        V2TemplateRegistryLoadingTask = LoadV2TemplateRegistryAsync(generation, client);
    }

    private async Task LoadV2TemplateRegistryAsync(int generation, WowClientValidation client)
    {
        BlizzardTemplateRegistry registry;
        try
        {
            registry = await Task.Run(() => _v2TemplateLoader.Load(client));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (generation != _v2TemplateLoadGeneration) return;
            V2TemplateRegistryStatus = $"Template registry load failed: {ex.Message}";
            V2TemplateDiagnostics.Add(V2TemplateRegistryStatus);
            OnPropertyChanged(nameof(V2TemplateRegistryStatus));
            return;
        }
        if (generation != _v2TemplateLoadGeneration) return;
        _v2TemplateRegistry = registry;
        _v2Session?.SetTemplateRegistry(registry);
        _stockTemplates.Reload();
        foreach (var diagnostic in registry.Diagnostics)
            V2TemplateDiagnostics.Add($"{diagnostic.Code}: {diagnostic.Message}");
        var resolved = BlizzardTemplateRegistry.ApprovedTemplates.Count(name => registry.Resolve(name)?.IsResolved == true);
        V2TemplateRegistryStatus = $"Build-12340 registry loaded: {resolved}/3 approved templates resolved.";
        OnPropertyChanged(nameof(V2TemplateRegistry));
        OnPropertyChanged(nameof(V2TemplateRegistryStatus));
        if (V2Document is not null) RefreshV2Presentation(V2TemplateRegistryStatus);
        RefreshV2Inspector();
    }

    private void RefreshV2Presentation(string? status)
    {
        if (V2Document is not { } document || document.CompositionRoots.SingleOrDefault() is not { } root || _v2Session is null)
            return;

        RefreshV2PresentationStates(document);
        var state = SelectedV2PresentationState?.Id is { } stateId
            ? document.Editor?.PreviewStates.FirstOrDefault(item => item.Id == stateId)
            : null;
        var presentation = UiPreviewPresentation.Apply(document, state);
        var semantic = UiDocumentValidator.Validate(document, _v2TemplateRegistry);
        _v2Layout = UiPreviewPresentation.ApplyVisibility(
            UiLayoutResolver.Resolve(presentation, UiPreviewHost.FromDesignRoot(root), _v2TemplateRegistry), state);
        PublishV2Diagnostics(semantic.Concat(_v2Layout.Diagnostics).Distinct());
        OnPropertyChanged(nameof(V2Layout));

        SelectedName = _v2Session.Selection.PrimaryId?.Value;
        _selectedNames.Clear();
        _selectedNames.AddRange(_v2Session.Selection.OrderedIds.Select(id => id.Value));
        RebuildV2Tree();
        CanvasSelectionNames.Clear();
        foreach (var id in _v2Layout.PaintOrder) CanvasSelectionNames.Add(id.Value);
        NotifySelectionSet();
        NotifyV2History();
        RefreshV2Groups();
        NotifyV2Selection();
        SelectionSummary = SelectedV2Node is { } node
            ? $"{node.DisplayLabel}: native schema-v2 {node.Kind}"
            : IsV2RootSelected ? $"{root.RuntimeName}: composition root" : "No control selected.";
        if (status is not null) Status = status;
    }

    private void RefreshV2PresentationStates(UiDocument document)
    {
        var selected = SelectedV2PresentationState?.Id;
        var ids = document.Editor?.PreviewStates.Select(state => state.Id).ToArray() ?? [];
        if (V2PresentationStates.Count == ids.Length + 1 &&
            V2PresentationStates.Skip(1).Select(option => option.Id).SequenceEqual(ids.Cast<SemanticId?>())) return;
        _refreshingV2PresentationStates = true;
        try
        {
            V2PresentationStates.Clear();
            V2PresentationStates.Add(new V2PresentationStateOption(null, "XML Defaults"));
            foreach (var state in document.Editor?.PreviewStates ?? [])
                V2PresentationStates.Add(new V2PresentationStateOption(state.Id, state.Name));
            SelectedV2PresentationState = V2PresentationStates.FirstOrDefault(option => option.Id == selected)
                                          ?? V2PresentationStates[0];
        }
        finally { _refreshingV2PresentationStates = false; }
    }

    private void RebuildV2Tree()
    {
        if (V2Document is not { } document || document.CompositionRoots.SingleOrDefault() is not { } root) return;
        var nodes = document.Nodes.ToDictionary(node => node.Id);
        var matches = document.Nodes.Where(MatchesV2Tree).Select(node => node.Id).ToHashSet();
        var visible = new HashSet<SemanticId>(matches);
        foreach (var match in matches)
        {
            var current = nodes[match];
            while (current.Owner.Kind == OwnerKind.LocalNode && nodes.TryGetValue(current.Owner.Id, out var owner))
            {
                visible.Add(owner.Id);
                if (!string.IsNullOrWhiteSpace(TreeSearch)) _expandedTreeNames.Add(owner.Id.Value);
                current = owner;
            }
        }
        if (V2Selection.PrimaryId is { } selected && nodes.TryGetValue(selected, out var selectedNode))
        {
            for (var current = selectedNode; current.Owner.Kind == OwnerKind.LocalNode && nodes.TryGetValue(current.Owner.Id, out var owner); current = owner)
                _expandedTreeNames.Add(owner.Id.Value);
        }
        _v2TreeVisibleCount = visible.Count;
        _v2TreeFiltering = TreeFilter != V2TreeFilter.ALL || !string.IsNullOrWhiteSpace(TreeSearch);
        string GroupsFor(SemanticId id) => string.Join(", ", document.Editor?.Groups
            .Where(group => group.Members.Contains(id)).Select(group => group.Name) ?? []);
        bool EditingLocked(UiNode node) => node.Editor?.Locked == true ||
            document.Editor?.Groups.Any(group => group.Locked && group.Members.Contains(node.Id)) == true;
        FrameTreeNode Build(UiNode node) => new(
            Node: node,
            Root: null,
            Children: node.Children.Where(id => visible.Contains(id) && nodes.ContainsKey(id)).Select(id => Build(nodes[id])).ToArray(),
            IsExpanded: _expandedTreeNames.Contains(node.Id.Value),
            IsSelected: V2Selection.OrderedIds.Contains(node.Id),
            IsPrimarySelection: node.Id == V2Selection.PrimaryId,
            IsLockedOverride: EditingLocked(node),
            IsHiddenOverride: IsReferenceEffectivelyHidden(node.Id),
            GroupNames: GroupsFor(node.Id));
        _selectionSyncDepth++;
        try
        {
            TreeRoots.Clear();
            TreeRoots.Add(new FrameTreeNode(null, root,
                root.Children.Where(id => visible.Contains(id) && nodes.ContainsKey(id)).Select(id => Build(nodes[id])).ToArray(),
                true, V2Selection.OrderedIds.Contains(root.Id), root.Id == V2Selection.PrimaryId));
            SelectedTreeNode = FindNode(SelectedName);
        }
        finally
        {
            _selectionSyncDepth--;
        }
        OnPropertyChanged(nameof(IsTreeFiltering));
        OnPropertyChanged(nameof(TreeFilterSummary));
    }

    private bool MatchesV2Tree(UiNode node)
    {
        var scopeMatches = V2HierarchyScope switch
        {
            V2HierarchyFilter.ReferenceOnly => node.Editor?.ReferenceOnly == true,
            V2HierarchyFilter.AuthoredOnly => node.Editor?.ReferenceOnly != true,
            _ => true,
        };
        var categoryMatches = TreeFilter switch
        {
            V2TreeFilter.ALL => true,
            V2TreeFilter.STRUCTURE => !node.IsRegion,
            V2TreeFilter.VISUAL => node.IsRegion,
            _ => true,
        };
        if (!categoryMatches || !scopeMatches) return false;
        if (string.IsNullOrWhiteSpace(TreeSearch)) return true;
        return new[] { node.DisplayLabel, node.RuntimeName, node.Kind.ToString(), node.Id.Value }
            .Any(value => value?.Contains(TreeSearch, StringComparison.OrdinalIgnoreCase) == true);
    }

    private string DimensionSummary()
    {
        if (SelectedV2Node is not { } node) return string.Empty;
        var effective = UiTemplateEffectiveProperties.Resolve(node, _v2TemplateRegistry);
        var frame = effective.Values.Frame;
        var authored = node.AuthoredProperties.Frame;
        var widthOrigin = effective.Provenance.GetValueOrDefault("frame.width");
        var heightOrigin = effective.Provenance.GetValueOrDefault("frame.height");
        var text = $"Authored: {FormatOptional(authored?.Width)} × {FormatOptional(authored?.Height)} · " +
                   $"Effective: {FormatOptional(frame?.Width)} × {FormatOptional(frame?.Height)} " +
                   $"({widthOrigin}/{heightOrigin})";
        return effective.PreviewBehaviors.Contains(BlizzardKnownPreviewBehavior.CharacterTabResizeToTextZeroPadding)
            ? text + " · runtime width resizes to text (preview estimate; not an exact static value)"
            : text;
    }

    private string TemplateProvenanceSummary()
    {
        if (SelectedV2Node?.BlizzardTemplate is not { } identity || _v2TemplateRegistry?.Resolve(identity) is not { } template)
            return string.Empty;
        var source = template.Definition.Source;
        return $"{identity} · {source.LogicalPath}:{source.Line} · SHA-256 {source.Sha256}";
    }

    private string PreviewStateDiagnostic()
    {
        var identity = SelectedV2Node?.BlizzardTemplate;
        return (identity, V2PreviewButtonState) switch
        {
            ("CharacterFrameTabButtonTemplate", PreviewButtonState.Pushed) =>
                "CharacterFrameTabButtonTemplate declares no pushed visual; Preview shows an explicit unsupported-state stand-in.",
            (not "CharacterFrameTabButtonTemplate", PreviewButtonState.Selected) =>
                "Selected is defined only for character tabs; Preview shows an explicit unsupported-state stand-in.",
            (_, PreviewButtonState.Highlighted) =>
                "The build-12340 ADD highlight is represented with an opacity overlay; exact additive blending is not available in this canvas.",
            _ => string.Empty,
        };
    }

    private string SelectedAssetDiagnostic()
    {
        var reference = SelectedV2Node?.Kind switch
        {
            UiNodeKind.Texture => SelectedV2Node.AuthoredProperties.Texture?.TextureReference,
            UiNodeKind.StatusBar => SelectedV2Node.AuthoredProperties.StatusBar?.TextureReference,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        var resolved = Assets.Resolve(reference);
        return resolved.CanRender
            ? $"Asset resolved: {reference} ({resolved.Width} × {resolved.Height})"
            : $"Asset {resolved.Status}: {resolved.Diagnostic.Message}";
    }

    private bool TryRequiredDouble(string text, string field, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value))
            return true;
        V2InspectorValidation = $"{field} must be a finite number.";
        return false;
    }

    private bool TryOptionalDouble(string text, string field, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }
        V2InspectorValidation = $"{field} must be blank or a finite number.";
        return false;
    }

    private bool TryTint(string text, out UiColor? color)
    {
        color = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (3 or 4) || parts.Any(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            V2InspectorValidation = "Tint must be blank or comma-separated R,G,B[,A] values from 0 to 1.";
            return false;
        }
        var values = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        color = new UiColor(values[0], values[1], values[2], values.Length == 4 ? values[3] : 1);
        if (values.Any(value => value is < 0 or > 1))
        {
            V2InspectorValidation = "Tint components must be from 0 to 1.";
            return false;
        }
        return true;
    }

    private static bool SameTarget(AnchorTarget left, AnchorTarget right) =>
        left.Kind == right.Kind && left.NodeId == right.NodeId && left.GlobalName == right.GlobalName;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string FormatNumber(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string FormatOptional(double? value) => value is null ? string.Empty : FormatNumber(value.Value);
    private static string FormatColor(UiColor? color) => color is null
        ? string.Empty
        : string.Join(",", FormatNumber(color.Red), FormatNumber(color.Green), FormatNumber(color.Blue), FormatNumber(color.Alpha));
}

internal static class SemanticIdListExtensions
{
    public static int IndexOf(this IReadOnlyList<SemanticId> values, SemanticId id)
    {
        for (var index = 0; index < values.Count; index++)
            if (values[index] == id) return index;
        return -1;
    }
}
