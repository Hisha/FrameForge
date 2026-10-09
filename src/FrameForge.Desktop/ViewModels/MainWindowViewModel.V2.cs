using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Core.Semantics.V2;

namespace FrameForge.Desktop.ViewModels;

public sealed record V2OwnerOption(OwnerReference Owner, string Label)
{
    public override string ToString() => Label;
}

public sealed record V2AnchorTargetOption(AnchorTarget Target, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MainWindowViewModel
{
    private readonly UiDocumentEditor _v2Editor = new();
    private UiDocument? _v2Document;
    private bool _loadingV2;
    private bool _refreshingV2Inspector;

    public UiDocument? V2Document => _v2Document;
    public bool IsV2Project => _v2Document is not null;
    public bool IsV1Project => _v2Document is null;
    public bool HasV2NodeSelection => SelectedV2Node is not null;
    public bool IsV2RootSelected => IsV2Project && _v2Document!.CompositionRoots.Any(root => root.Id.Value == SelectedName);
    public UiNode? SelectedV2Node => UiDocumentProjection.IdFromProjectionName(SelectedName) is { } id
        ? _v2Document?.Nodes.FirstOrDefault(node => node.Id == id)
        : null;
    public string V2ProjectSummary => _v2Document is null
        ? string.Empty
        : $"Schema v2 · WoW 3.3.5a build 12340 · {_v2Document.Nodes.Count} control(s)";
    public string V2RootSummary => _v2Document?.CompositionRoots.SingleOrDefault() is { } root
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
    public string V2UnsupportedPropertiesNote =>
        "Only Milestone 2 geometry, ownership, anchor, identity, and basic appearance are editable. Other Blizzard properties remain unsupported and unchanged.";

    public ObservableCollection<UiDiagnostic> V2Diagnostics { get; } = [];
    public ObservableCollection<V2OwnerOption> V2OwnerOptions { get; } = [];
    public ObservableCollection<V2AnchorTargetOption> V2AnchorTargetOptions { get; } = [];
    public IReadOnlyList<AnchorPoint> V2AnchorPoints { get; } = AnchorPoints.All;

    [ObservableProperty] private string _v2DisplayLabelDraft = string.Empty;
    [ObservableProperty] private string _v2RuntimeNameDraft = string.Empty;
    [ObservableProperty] private string _v2WidthDraft = string.Empty;
    [ObservableProperty] private string _v2HeightDraft = string.Empty;
    [ObservableProperty] private string _v2OffsetXDraft = string.Empty;
    [ObservableProperty] private string _v2OffsetYDraft = string.Empty;
    [ObservableProperty] private string _v2TextureDraft = string.Empty;
    [ObservableProperty] private string _v2TextDraft = string.Empty;
    [ObservableProperty] private string _v2FontDraft = string.Empty;
    [ObservableProperty] private string _v2TintDraft = string.Empty;
    [ObservableProperty] private string _v2StatusMinimumDraft = string.Empty;
    [ObservableProperty] private string _v2StatusMaximumDraft = string.Empty;
    [ObservableProperty] private string _v2StatusValueDraft = string.Empty;
    [ObservableProperty] private string _v2StatusTextureDraft = string.Empty;
    [ObservableProperty] private bool _v2VisibleDraft = true;
    [ObservableProperty] private bool _v2ButtonEnabledDraft = true;
    [ObservableProperty] private AnchorPoint _v2PointDraft = AnchorPoint.CENTER;
    [ObservableProperty] private AnchorPoint _v2RelativePointDraft = AnchorPoint.CENTER;
    [ObservableProperty] private V2OwnerOption? _v2OwnerDraft;
    [ObservableProperty] private V2AnchorTargetOption? _v2AnchorTargetDraft;
    [ObservableProperty] private string _v2InspectorValidation = string.Empty;

    public void NewV2Project()
    {
        var document = UiDocumentFactory.Create("NewDesignRoot", "ModuleUiHost", 1024, 768) with
        {
            Editor = new DocumentEditorMetadata
            {
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [UiDocumentProjection.ProjectNameMetadataKey] = "Untitled v2 Project",
                },
            },
        };
        LoadV2(document, null, "New FrameForge 2.0 project created with one module-hosted composition root.");
        Select(document.CompositionRoots[0].Id.Value);
    }

    public void LoadV2(UiDocument document, string? path, string status)
    {
        SetV2Document(document);
        PublishV2Diagnostics(UiDocumentValidator.Validate(document));
        _loadingV2 = true;
        try
        {
            Load(UiDocumentProjection.ToProject(document), path, status);
        }
        finally
        {
            _loadingV2 = false;
        }
        SetV2Document(document);
        RefreshV2Inspector();
    }

    public bool SaveV2ToFile(string path)
    {
        if (_v2Document is null)
            return false;
        if (!path.EndsWith(ProjectCodec.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            Status = $"Refused to save schema-v2 JSON to {Path.GetFileName(path)}. Use the {ProjectCodec.FileExtension} extension.";
            return false;
        }
        var diagnostics = UiDocumentValidator.Validate(_v2Document);
        PublishV2Diagnostics(diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            Status = "Save blocked by semantic validation: " + string.Join(" ", diagnostics.Take(3).Select(item => $"{item.Code}: {item.Message}"));
            return false;
        }
        try
        {
            File.WriteAllText(path, UiDocumentCodec.Serialize(_v2Document));
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
        if (_v2Document?.CompositionRoots.SingleOrDefault() is not { } root)
            return;
        var selected = SelectedV2Node;
        var owner = selected?.CanOwnChildren == true
            ? OwnerReference.Node(selected.Id)
            : OwnerReference.Root(root.Id);
        var ordinal = _v2Document.Nodes.Count(node => node.Kind == kind) + 1;
        while (root.RuntimeName == $"{kind}{ordinal}" || _v2Document.Nodes.Any(node => node.RuntimeName == $"{kind}{ordinal}")) ordinal++;
        var result = _v2Editor.CreateControl(_v2Document, kind, owner, $"{kind} {ordinal}", $"{kind}{ordinal}");
        ApplyV2Result(result, $"Added {kind} under {(selected?.CanOwnChildren == true ? selected.DisplayLabel : "the composition root")}.");
    }

    public void DeleteV2Selection()
    {
        if (_v2Document is null || SelectedV2Node is not { } node)
        {
            Status = IsV2RootSelected ? "The composition root is required and cannot be deleted." : "Select a v2 control to delete.";
            return;
        }
        var parentSelection = node.Owner.Id.Value;
        var result = _v2Editor.DeleteControl(_v2Document, node.Id);
        ApplyV2Result(result, $"Deleted {node.DisplayLabel} and its owned subtree.", parentSelection);
    }

    public void MoveV2SelectionInOrder(int delta)
    {
        if (_v2Document is null || SelectedV2Node is not { } node)
            return;
        var siblings = node.Owner.Kind == OwnerKind.CompositionRoot
            ? _v2Document.CompositionRoots.Single(root => root.Id == node.Owner.Id).Children
            : _v2Document.Nodes.Single(owner => owner.Id == node.Owner.Id).Children;
        var result = _v2Editor.ReorderChild(_v2Document, node.Id, siblings.IndexOf(node.Id) + delta);
        ApplyV2Result(result, delta < 0 ? "Moved control earlier in its owner." : "Moved control later in its owner.");
    }

    public void ApplyV2Inspector()
    {
        if (_refreshingV2Inspector || _v2Document is null || SelectedV2Node is not { } original ||
            V2OwnerDraft is null || V2AnchorTargetDraft is null)
            return;
        if (!TryRequiredDouble(V2WidthDraft, "Width", out var width) ||
            !TryRequiredDouble(V2HeightDraft, "Height", out var height) ||
            !TryRequiredDouble(V2OffsetXDraft, "Offset X", out var offsetX) ||
            !TryRequiredDouble(V2OffsetYDraft, "Offset Y", out var offsetY))
            return;
        if (!TryOptionalDouble(V2StatusMinimumDraft, "Status minimum", out var minimum) ||
            !TryOptionalDouble(V2StatusMaximumDraft, "Status maximum", out var maximum) ||
            !TryOptionalDouble(V2StatusValueDraft, "Status value", out var statusValue) ||
            !TryTint(V2TintDraft, out var tint))
            return;

        var working = _v2Document;
        var rename = _v2Editor.RenameControl(working, original.Id, V2DisplayLabelDraft, V2RuntimeNameDraft);
        if (!TryContinue(rename, out working)) return;
        var geometry = _v2Editor.UpdateGeometry(working, original.Id, width, height, offsetX, offsetY);
        if (!TryContinue(geometry, out working)) return;

        var node = working.Nodes.Single(item => item.Id == original.Id);
        var properties = node.AuthoredProperties;
        if (node.IsRegion)
            properties = properties with { Region = properties.Region! with { Tint = tint } };
        else
            properties = properties with { Frame = properties.Frame! with { Visible = V2VisibleDraft } };
        properties = node.Kind switch
        {
            UiNodeKind.Texture => properties with { Texture = properties.Texture! with { TextureReference = EmptyToNull(V2TextureDraft) } },
            UiNodeKind.FontString => properties with
            {
                FontString = properties.FontString! with { Text = V2TextDraft, FontReference = EmptyToNull(V2FontDraft) },
            },
            UiNodeKind.Button => properties with { Button = properties.Button! with { Enabled = V2ButtonEnabledDraft } },
            UiNodeKind.StatusBar => properties with
            {
                StatusBar = properties.StatusBar! with
                {
                    Minimum = minimum,
                    Maximum = maximum,
                    Value = statusValue,
                    TextureReference = EmptyToNull(V2StatusTextureDraft),
                },
            },
            _ => properties,
        };
        var appearance = _v2Editor.UpdateProperties(working, original.Id, properties);
        if (!TryContinue(appearance, out working)) return;

        var currentAnchor = working.Nodes.Single(item => item.Id == original.Id).Anchors[0];
        var anchor = currentAnchor with
        {
            Point = V2PointDraft,
            RelativePoint = V2RelativePointDraft,
            Target = V2AnchorTargetDraft.Target,
            OffsetX = offsetX,
            OffsetY = offsetY,
        };
        var anchors = _v2Editor.UpdateAnchors(working, original.Id, [anchor, .. original.Anchors.Skip(1)]);
        if (!TryContinue(anchors, out working)) return;

        var movedOwner = false;
        var currentNode = working.Nodes.Single(item => item.Id == original.Id);
        if (currentNode.Owner != V2OwnerDraft.Owner)
        {
            var ownership = _v2Editor.ChangeOwnership(working, original.Id, V2OwnerDraft.Owner, preserveVisualPosition: true);
            if (!TryContinue(ownership, out working)) return;
            movedOwner = true;
        }

        V2InspectorValidation = string.Empty;
        ApplyV2Result(new SemanticEditResult
        {
            Document = working,
            Diagnostics = UiDocumentValidator.Validate(working),
            AffectedId = original.Id,
            Changed = true,
        }, movedOwner ? "Applied v2 properties and moved the control while preserving its visual position." : "Applied v2 properties.");
    }

    private bool TryContinue(SemanticEditResult result, out UiDocument document)
    {
        document = result.Document;
        if (result.Success) return true;
        V2InspectorValidation = result.ErrorText;
        PublishV2Diagnostics(result.Diagnostics);
        Status = "Edit rejected: " + result.ErrorText;
        return false;
    }

    private void ApplyV2Result(SemanticEditResult result, string successStatus, string? selectionOverride = null)
    {
        PublishV2Diagnostics(result.Diagnostics);
        if (!result.Success)
        {
            V2InspectorValidation = result.ErrorText;
            Status = "Edit rejected: " + result.ErrorText;
            return;
        }
        SetV2Document(result.Document);
        Project = UiDocumentProjection.ToProject(result.Document);
        IsDirty = true;
        var selection = selectionOverride ?? result.AffectedId?.Value ?? SelectedName;
        RelaidOut(Project, selection, successStatus);
        RefreshV2Inspector();
    }

    private void SetV2Document(UiDocument? document)
    {
        if (ReferenceEquals(_v2Document, document)) return;
        _v2Document = document;
        OnPropertyChanged(nameof(V2Document));
        OnPropertyChanged(nameof(IsV2Project));
        OnPropertyChanged(nameof(IsV1Project));
        OnPropertyChanged(nameof(ShowV1DesignTools));
        OnPropertyChanged(nameof(ShowV2DesignTools));
        OnPropertyChanged(nameof(ShowV1InspectTools));
        OnPropertyChanged(nameof(V2ProjectSummary));
        OnPropertyChanged(nameof(V2RootSummary));
        OnPropertyChanged(nameof(ProjectDescription));
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
        if (_v2Document is null)
        {
            NotifyV2Selection();
            return;
        }
        _refreshingV2Inspector = true;
        try
        {
            V2OwnerOptions.Clear();
            V2AnchorTargetOptions.Clear();
            var root = _v2Document.CompositionRoots.SingleOrDefault();
            if (root is not null)
            {
                V2OwnerOptions.Add(new V2OwnerOption(OwnerReference.Root(root.Id), $"Composition Root · {root.RuntimeName}"));
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.Parent(), "Parent"));
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.Root(), "Composition Root"));
            }
            var selected = SelectedV2Node;
            foreach (var candidate in _v2Document.Nodes.Where(node => node.CanOwnChildren && node.Id != selected?.Id))
                V2OwnerOptions.Add(new V2OwnerOption(OwnerReference.Node(candidate.Id), candidate.DisplayLabel));
            foreach (var candidate in _v2Document.Nodes.Where(node => node.Id != selected?.Id))
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.Local(candidate.Id), $"Local · {candidate.DisplayLabel}"));
            foreach (var external in _v2Document.ExternalReferences)
                V2AnchorTargetOptions.Add(new V2AnchorTargetOption(AnchorTarget.External(external.GlobalName), $"External · {external.GlobalName}"));

            if (selected is null)
            {
                V2DisplayLabelDraft = string.Empty;
                V2RuntimeNameDraft = string.Empty;
                return;
            }
            var dimensions = selected.IsRegion
                ? (selected.AuthoredProperties.Region?.Width ?? 0, selected.AuthoredProperties.Region?.Height ?? 0)
                : (selected.AuthoredProperties.Frame?.Width ?? 0, selected.AuthoredProperties.Frame?.Height ?? 0);
            var anchor = selected.Anchors.FirstOrDefault();
            V2DisplayLabelDraft = selected.DisplayLabel;
            V2RuntimeNameDraft = selected.RuntimeName ?? string.Empty;
            V2WidthDraft = FormatNumber(dimensions.Item1);
            V2HeightDraft = FormatNumber(dimensions.Item2);
            V2OffsetXDraft = FormatNumber(anchor?.OffsetX ?? 0);
            V2OffsetYDraft = FormatNumber(anchor?.OffsetY ?? 0);
            V2PointDraft = anchor?.Point ?? AnchorPoint.CENTER;
            V2RelativePointDraft = anchor?.RelativePoint ?? AnchorPoint.CENTER;
            V2OwnerDraft = V2OwnerOptions.FirstOrDefault(option => option.Owner == selected.Owner);
            V2AnchorTargetDraft = V2AnchorTargetOptions.FirstOrDefault(option => anchor is not null && SameTarget(option.Target, anchor.Target));
            V2TextureDraft = selected.AuthoredProperties.Texture?.TextureReference ?? string.Empty;
            V2TextDraft = selected.AuthoredProperties.FontString?.Text ?? string.Empty;
            V2FontDraft = selected.AuthoredProperties.FontString?.FontReference ?? string.Empty;
            V2TintDraft = selected.AuthoredProperties.Region?.Tint is { } tint
                ? string.Join(",", FormatNumber(tint.Red), FormatNumber(tint.Green), FormatNumber(tint.Blue), FormatNumber(tint.Alpha))
                : string.Empty;
            V2VisibleDraft = selected.AuthoredProperties.Frame?.Visible ?? true;
            V2ButtonEnabledDraft = selected.AuthoredProperties.Button?.Enabled ?? true;
            V2StatusMinimumDraft = FormatOptional(selected.AuthoredProperties.StatusBar?.Minimum);
            V2StatusMaximumDraft = FormatOptional(selected.AuthoredProperties.StatusBar?.Maximum);
            V2StatusValueDraft = FormatOptional(selected.AuthoredProperties.StatusBar?.Value);
            V2StatusTextureDraft = selected.AuthoredProperties.StatusBar?.TextureReference ?? string.Empty;
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
    }

    partial void OnSelectedNameChanged(string? value) => RefreshV2Inspector();

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
