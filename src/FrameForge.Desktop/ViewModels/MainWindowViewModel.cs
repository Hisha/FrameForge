using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core;
using FrameForge.Core.Examples;
using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;

namespace FrameForge.Desktop.ViewModels;

/// <summary>
/// The editor's state and commands.
/// </summary>
/// <remarks>
/// The view model owns the <see cref="Project"/>, re-resolves the layout through
/// <see cref="LayoutResolver"/> after every mutation, and pushes the result at the canvas.
/// It never performs geometry maths of its own: absolute bounds, paint order and visibility
/// inheritance all come from FrameForge.Core so there is exactly one implementation.
/// <para>
/// A project can come from three places - hand-authored, the shipped example, and a read-only
/// FrameXML import - and the difference is never hidden. <see cref="Source"/> is null, a text
/// summary of where the layout came from and what could not be carried across, and drives both
/// the banner and the save guard.
/// </para>
/// <para>
/// File dialogs are NOT handled here. <see cref="MainWindow"/> owns the window and performs
/// the pickers, then calls the Open/Save methods on this class with a path.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private Project _project = ProjectFactory.Empty();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _projectPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SaveHint))]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = "Ready.";

    [ObservableProperty]
    private string _selectionSummary = "No frame selected.";

    [ObservableProperty]
    private LayoutResult _layout = LayoutResolver.Resolve(ProjectFactory.Empty());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFrame))]
    [NotifyPropertyChangedFor(nameof(CanEditSelection))]
    private string? _selectedName;

    [ObservableProperty]
    private FrameTreeNode? _selectedTreeNode;

    /// <summary>
    /// The active canvas mode.
    /// </summary>
    /// <remarks>
    /// Changing it resets the visibility filter and the label policy to that mode's defaults, so
    /// switching to Preview really does produce a clean canvas rather than Preview's drawing rules
    /// with Debug's everything-visible toggles still switched on.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeOptions))]
    [NotifyPropertyChangedFor(nameof(ActiveModeDescription))]
    private CanvasViewMode _viewMode = CanvasViewMode.DEBUG;

    /// <summary>Which categories of widget the canvas draws. Never changes the project.</summary>
    [ObservableProperty]
    private VisibilityFilter _canvasFilter = ViewPolicy.DefaultsFor(CanvasViewMode.DEBUG);

    /// <summary>Which widgets get a name label on the canvas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLabelPolicyOption))]
    private LabelPolicy _labelPolicy = ViewPolicy.DefaultLabelPolicyFor(CanvasViewMode.DEBUG);

    /// <summary>
    /// The selected entry in the label policy selector, as an option object rather than an enum.
    /// </summary>
    /// <remarks>
    /// A ComboBox binds to an item, not to a value, so the selector needs the selected option itself
    /// to stay in step. Resolving it on demand from the policy - rather than storing it - means the
    /// two can never disagree.
    /// </remarks>
    public LabelPolicyOption? SelectedLabelPolicyOption
    {
        get => LabelPolicyOptions.FirstOrDefault(o => o.Policy == LabelPolicy);
        set
        {
            if (value is not null)
                LabelPolicy = value.Policy;
        }
    }

    /// <summary>Case-insensitive substring filter over frame names in the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TreeFilterSummary))]
    [NotifyPropertyChangedFor(nameof(IsTreeFiltering))]
    private string _treeSearch = string.Empty;

    /// <summary>Structural narrowing of the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TreeFilterSummary))]
    [NotifyPropertyChangedFor(nameof(IsTreeFiltering))]
    private TreeFilter _treeFilter = TreeFilter.ALL;

    /// <summary>One entry per mode, for the mode selector.</summary>
    public IReadOnlyList<CanvasModeOption> ModeOptions { get; }

    /// <summary>The active mode's tooltip text.</summary>
    public string ActiveModeDescription => ViewMode.Description();

    /// <summary>The label for each visibility toggle, in toolbar order.</summary>
    public IReadOnlyList<VisibilityToggle> VisibilityToggles { get; }

    /// <summary>The label policies offered for the canvas.</summary>
    public IReadOnlyList<LabelPolicyOption> LabelPolicyOptions { get; }

    /// <summary>The structural filters offered for the tree, in order.</summary>
    public IReadOnlyList<TreeFilterOption> TreeFilterOptions { get; }

    /// <summary>True when the tree is hiding anything, so the UI can say so.</summary>
    public bool IsTreeFiltering => _treeProjection?.Filtering ?? false;

    /// <summary>How many nodes the tree is showing, and how many exist.</summary>
    public string TreeFilterSummary =>
        _treeProjection is { Filtering: true } projection
            ? $"{projection.Visible.Count} of {Project.Frames.Count} shown"
            : $"{Project.Frames.Count} frames";

    private TreeProjection? _treeProjection;
    private readonly AssetSettingsStore _assetSettings;
    private string? _assetSourcePath;

    /// <summary>Application-local roots searched after source-relative content.</summary>
    public ObservableCollection<string> AssetRoots { get; } = [];

    /// <summary>The reusable resolver shared by the inspector and canvas.</summary>
    public TextureAssetResolver Assets { get; } = new();

    public string AssetRootsSummary => AssetRoots.Count == 0
        ? "Asset roots (none)"
        : $"Asset roots ({AssetRoots.Count})";

    /// <summary>
    /// True when one specific visibility category is switched on.
    /// </summary>
    /// <remarks>
    /// Exposed as a method rather than a computed property per category so the six toolbar
    /// toggles bind to one implementation instead of six near-identical copies.
    /// </remarks>
    public bool IsCategoryVisible(VisibilityFilter flag) => CanvasFilter.HasFlag(flag);

    /// <summary>Turns one visibility category on or off.</summary>
    public void SetCategoryVisible(VisibilityFilter flag, bool enabled) =>
        CanvasFilter = enabled ? CanvasFilter | flag : CanvasFilter & ~flag;

    /// <summary>Returns the canvas to this mode's own defaults.</summary>
    public void ResetViewToModeDefaults()
    {
        CanvasFilter = ViewPolicy.DefaultsFor(ViewMode);
        LabelPolicy = ViewPolicy.DefaultLabelPolicyFor(ViewMode);
        Status = $"Canvas reset to {ViewMode.Label()} defaults.";
    }

    /// <summary>
    /// Switches mode, which also applies that mode's defaults.
    /// </summary>
    public void SetViewMode(CanvasViewMode mode)
    {
        if (ViewMode == mode)
            return;

        ViewMode = mode;
        ResetViewToModeDefaults();
        Status = $"{mode.Label()}: {mode.Description()}";
    }

    partial void OnViewModeChanged(CanvasViewMode value)
    {
        // The generated property already notified the bindings; the canvas filter and label policy
        // follow so the mode's defaults are actually in force rather than merely advertised.
        CanvasFilter = ViewPolicy.DefaultsFor(value);
        LabelPolicy = ViewPolicy.DefaultLabelPolicyFor(value);

        foreach (var option in ModeOptions)
            option.Refresh();
    }

    /// <summary>Re-applies the visibility filter after an edit.</summary>
    partial void OnCanvasFilterChanged(VisibilityFilter value)
    {
        // The toggles read the filter rather than owning state, so they need telling when it moves
        // for any reason other than the user clicking them - including a mode change or a reset.
        foreach (var toggle in VisibilityToggles)
            toggle.Refresh();

        if (Project.Find(SelectedName) is { } frame && !ViewPolicy.IsVisible(frame, Layout, value))
            Status = $"\"{frame.Name}\" is hidden by the current filter; it stays selected.";
    }

    partial void OnLabelPolicyChanged(LabelPolicy value)
    {
        foreach (var option in LabelPolicyOptions)
            option.Refresh();
    }

    partial void OnTreeFilterChanged(TreeFilter value)
    {
        RebuildTree(Project);

        foreach (var option in TreeFilterOptions)
            option.Refresh();
    }

    public MainWindowViewModel() : this(null)
    {
    }

    public MainWindowViewModel(string? settingsPath)
    {
        _assetSettings = new AssetSettingsStore(settingsPath);
        foreach (var root in _assetSettings.Load())
            AssetRoots.Add(root);
        Assets.Configure(null, AssetRoots);
        ModeOptions = [.. Enum.GetValues<CanvasViewMode>().Select(m => new CanvasModeOption(this, m))];
        LabelPolicyOptions = [.. Enum.GetValues<LabelPolicy>().Select(p => new LabelPolicyOption(this, p))];
        TreeFilterOptions = [.. TreeFilters.All.Select(f => new TreeFilterOption(this, f))];
        VisibilityToggles = [.. VisibilityFilters.Toggles.Select(t => new VisibilityToggle(this, t.Label, t.Flag, t.ToolTip))];

        Editor = new FrameEditorViewModel((n, u, r) => ReplaceFrame(n, u, r), BuildFrameOptions, AnchorOptions,
            DescribeAsset);
        RelaidOut(_project, null, "New project. Load the Native Hunts example from the toolbar.");
    }

    /// <summary>The inspector bound panel for the selected frame.</summary>
    public FrameEditorViewModel Editor { get; }

    /// <summary>Tree contents, rebuilt from the parent relationship.</summary>
    public ObservableCollection<FrameTreeNode> TreeRoots { get; } = [];

    /// <summary>Flat, paint-ordered frame names, used to bind the tree selection.</summary>
    public ObservableCollection<string> CanvasSelectionNames { get; } = [];

    /// <summary>Projects available from the New menu.</summary>
    public IReadOnlyList<string> RecentProjectNames { get; } = [];

    /// <summary>The project currently open.</summary>
    public Project Project
    {
        get => _project;
        private set => SetProperty(ref _project, value);
    }

    /// <summary>True when the Native Hunts example is loaded, used to label the status hint.</summary>
    public string ProjectDescription =>
        Project.Frames.Count == 0
            ? "Empty project."
            : $"{Project.Frames.Count} frames on a {Number(Project.Screen.Width)} x {Number(Project.Screen.Height)} screen.";

    /// <summary>Title bar text, including the unsaved marker.</summary>
    public string WindowTitle
    {
        get
        {
            var name = string.IsNullOrEmpty(ProjectPath)
                ? Project.Name
                : System.IO.Path.GetFileName(ProjectPath);
            return $"{name}{(IsDirty ? " *" : string.Empty)} - FrameForge";
        }
    }

    /// <summary>Hint under the toolbar buttons.</summary>
    public string SaveHint => IsDirty ? "Unsaved changes" : "Saved";

    /// <summary>Where the open project came from, and what the import could not carry across.</summary>
    public SourceSummary? Source
    {
        get => _source;
        private set
        {
            if (SetProperty(ref _source, value))
                OnPropertyChanged(nameof(HasSource));
        }
    }

    private SourceSummary? _source;

    /// <summary>True when a FrameXML import banner should be shown.</summary>
    public bool HasSource => Source is not null;

    /// <summary>The findings of the most recent import, for the diagnostics list.</summary>
    public ObservableCollection<FrameXmlDiagnostic> ImportDiagnostics
    {
        get => _importDiagnostics;
        private set
        {
            if (SetProperty(ref _importDiagnostics, value))
                OnPropertyChanged(nameof(HasImportDiagnostics));
        }
    }

    /// <summary>True when the last import reported anything at all.</summary>
    public bool HasImportDiagnostics => ImportDiagnostics.Count > 0;

    private ObservableCollection<FrameXmlDiagnostic> _importDiagnostics = [];

    /// <summary>
    /// The outcome of the most recent FrameXML import, kept whole rather than reduced to a
    /// sentence.
    /// </summary>
    /// <remarks>
    /// The banner needs the per-element support levels, and so does anything that wants to check
    /// the importer rather than trust it. Keeping the result means those numbers come from the
    /// importer that actually ran instead of being recounted later from the project, which would
    /// only prove that the recount agrees with itself.
    /// </remarks>
    public FrameXmlImportResult? LastImport { get; private set; }

    /// <summary>True when a frame is selected.</summary>
    public FrameDef? SelectedFrame => Project.Find(SelectedName);

    /// <summary>True when the inspector can be edited.</summary>
    public bool CanEditSelection => SelectedFrame is not null;

    /// <summary>Replaces the whole project. Used by New / Open / Load Example.</summary>
    /// <remarks>
    /// Any previous import banner is cleared: opening a project - even one that was itself
    /// saved from an import - is not the same event as importing, and leaving a stale warning
    /// list on screen would describe a file the user is no longer looking at.
    /// </remarks>
    public void Load(Project project, string? path, string status)
    {
        Project = project;
        ProjectPath = path ?? string.Empty;
        IsDirty = false;

        // Everything below describes the PREVIOUS document, so it is cleared here rather than
        // left to each caller. An importer that publishes its findings before calling Load would
        // have them wiped by this line, which is exactly the kind of ordering bug that makes an
        // import look clean when it was not.
        Source = null;
        ImportDiagnostics = [];
        LastImport = null;
        _assetSourcePath = ResolveSourcePath(project, path);
        Assets.Configure(_assetSourcePath, AssetRoots);

        var keep = project.Frames.FirstOrDefault(f => f.Name == SelectedName)?.Name;
        SelectedName = keep;
        RelaidOut(project, keep, status);
    }

    /// <summary>Creates a fresh project.</summary>
    public void NewProject() => Load(ProjectFactory.Empty(), null, "New project created.");

    /// <summary>Loads the built-in Native Hunts example.</summary>
    public void LoadNativeHuntsExample() =>
        Load(NativeHuntsExample.CreateProject(), null,
            "Loaded the Native Hunts example. Identity, State and Idle overlap on purpose.");

    /// <summary>Loads a project from disk and reports any error in the status bar.</summary>
    /// <remarks>
    /// The extension decides the reader, so the same Open action handles both formats and the
    /// user never has to pick a mode. A <c>.xml</c> path is handed to
    /// <see cref="FrameXmlImporter"/>; anything else is treated as a FrameForge project.
    /// </remarks>
    public void OpenFromFile(string path)
    {
        if (ProjectCodec.IsXmlPath(path))
        {
            ImportFromFile(path);
            return;
        }

        try
        {
            var result = ProjectCodec.Parse(File.ReadAllText(path));
            if (!result.Ok)
            {
                RelaidOut(Project, SelectedName, $"Could not open {FileName(path)}: {result.ErrorText}");
                return;
            }

            Load(result.Project!, path, $"Opened {FileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RelaidOut(Project, SelectedName, $"Could not open {FileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Imports a WoW FrameXML document into an ordinary project and keeps it read-only.
    /// </summary>
    /// <remarks>
    /// The result becomes a normal FrameForge project - same tree, same canvas, same inspector,
    /// editable and savable - with two deliberate differences: no <see cref="ProjectPath"/> is
    /// set, because overwriting the addon file with a JSON project would be the worst possible
    /// outcome; and <see cref="Source"/> is populated, so the banner stays visible and the save
    /// path is guarded until the project has been saved somewhere else.
    /// </remarks>
    public void ImportFromFile(string path)
    {
        var result = FrameXmlImporter.ImportFile(path);

        if (!result.Ok)
        {
            var message = string.Join(" ", result.Errors.Select(e => e.Message));
            Source = SourceSummary.Failed(FileName(path), path, message, result.Warnings.Count);
            ImportDiagnostics = [.. result.Diagnostics];
            LastImport = result;
            RelaidOut(Project, SelectedName, $"Could not import {FileName(path)}: {message}");
            return;
        }

        // The reference path is informational only: it records where the layout came from, and
        // nothing writes back to it.
        var imported = result.Project!;
        var project = imported with { Source = imported.Source! with { ReferencePath = path } };

        // path: null, so Save never targets the source file; it routes through Save As.
        Load(project, null, result.SummaryText);
        _assetSourcePath = path;
        Assets.Configure(_assetSourcePath, AssetRoots);

        // Published AFTER Load, because Load is what clears the previous document's provenance.
        Source = SourceSummary.Imported(FileName(path), path, result);
        ImportDiagnostics = [.. result.Diagnostics];
        LastImport = result;
        Status = result.Warnings.Count == 0
            ? result.SummaryText
            : $"{result.SummaryText} {result.WarningSummary}";
    }

    /// <summary>Writes the project to disk. Returns false when the write was refused or failed.</summary>
    public bool SaveToFile(string path)
    {
        if (!ProjectCodec.CanSaveTo(path))
        {
            Status =
                $"Refused to save to {FileName(path)}: that is a FrameXML source file. " +
                $"FrameForge never writes XML, so use Save As and keep the {ProjectCodec.FileExtension} extension.";
            return false;
        }

        try
        {
            var projectToSave = Project;
            if (Project.Source?.ReferencePath is { Length: > 0 } reference)
            {
                string portableReference;
                try
                {
                    portableReference = Path.IsPathRooted(reference)
                        ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, reference)
                        : reference;
                }
                catch (ArgumentException)
                {
                    portableReference = Project.Source.FileName ?? Path.GetFileName(reference);
                }
                projectToSave = Project with
                {
                    Source = Project.Source with { ReferencePath = portableReference.Replace('\\', '/') },
                };
            }

            File.WriteAllText(path, ProjectCodec.Serialize(projectToSave));
            ProjectPath = path;
            IsDirty = false;
            Status = $"Saved {FileName(path)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save {FileName(path)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Selects a frame, or clears the selection with null.</summary>
    public void Select(string? name)
    {
        if (name is not null && !Project.Contains(name))
            name = null;

        if (SelectedName == name && FindNode(name) == SelectedTreeNode)
            return;

        SelectedName = name;
        RelaidOut(Project, name, null);
    }

    /// <summary>Keeps the tree selection in step when the user clicks the tree.</summary>
    partial void OnSelectedTreeNodeChanged(FrameTreeNode? value) => Select(value?.Name);

    /// <summary>Re-projects and redraws the tree whenever the search text changes.</summary>
    partial void OnTreeSearchChanged(string value) => RebuildTree(Project);

    private FrameTreeNode? FindNode(string? name)
    {
        if (name is null)
            return null;

        foreach (var root in TreeRoots)
        {
            var found = FindNode(root, name);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static FrameTreeNode? FindNode(FrameTreeNode node, string name) =>
        node.Name == name ? node : node.Children.Select(child => FindNode(child, name)).FirstOrDefault(found => found is not null);

    /// <summary>
    /// Applies a drag to the selected frame's offsets.
    /// </summary>
    /// <remarks>
    /// The delta arrives in MODEL units from the canvas, so dragging down the screen arrives
    /// as a negative Y and correctly decreases <c>offsetY</c>.
    /// </remarks>
    public void DragFrame(string name, double modelDx, double modelDy)
    {
        var frame = Project.Find(name);
        if (frame is null)
            return;

        var updated = frame with { OffsetX = frame.OffsetX + modelDx, OffsetY = frame.OffsetY + modelDy };
        ReplaceFrame(name, updated);
        IsDirty = true;
    }

    /// <summary>Called when a drag gesture ends.</summary>
    public void EndDrag()
    {
        if (IsDirty)
            Status = $"Dragged {SelectedName}.";
    }

    /// <summary>Adds a frame under the current selection and selects it.</summary>
    public void AddFrame()
    {
        var parent = SelectedName;
        var name = UniqueName(parent is null ? "Frame" : $"{parent}Child");
        var frame = new FrameDef
        {
            Name = name,
            Parent = parent,
            Width = 100,
            Height = 40,
            Point = AnchorPoint.TOPLEFT,
            RelativePoint = AnchorPoint.TOPLEFT,
        };

        Project = Project with { Frames = [.. Project.Frames, frame] };
        IsDirty = true;
        SelectedName = name;
        RelaidOut(Project, name, $"Added frame \"{name}\".");
    }

    /// <summary>
    /// Removes the selected frame.
    /// </summary>
    /// <remarks>
    /// Descendants are RE-PARENTED to the removed frame's parent, not deleted: deleting a
    /// window should not silently destroy the panels inside it. Frames that used the removed
    /// frame as an anchor target fall back to inheriting their own parent.
    /// </remarks>
    public void DeleteFrame()
    {
        if (SelectedName is not { } name || Project.Find(name) is not { } removed)
            return;

        var descendants = FrameHierarchy.Subtree(Project, name).Skip(1).ToHashSet(StringComparer.Ordinal);
        var frames = Project.Frames
            .Where(f => f.Name != name)
            .Select(f => f with
            {
                Parent = f.Parent == name ? removed.Parent : f.Parent,
                RelativeTo = f.RelativeTo == name ? null : f.RelativeTo,
            })
            .ToArray();

        var parent = removed.Parent ?? "the root";
        Project = Project with { Frames = frames };
        IsDirty = true;
        SelectedName = parent;
        RelaidOut(Project, SelectedName, descendants.Count == 0
            ? $"Deleted \"{name}\"."
            : $"Deleted \"{name}\"; re-parented {descendants.Count} descendant(s) to {parent}.");
    }

    /// <summary>Canvas hit-test entry point; also used to keep the tree in sync.</summary>
    public void OnCanvasSelectionRequested(string? name) => Select(name);

    /// <summary>Fits the layout to the canvas viewport.</summary>
    public void RequestFit() => Status = "Fitted the layout to the canvas.";

    /// <summary>Reports the result of centering the selected widget without changing zoom.</summary>
    public void ReportRevealSelection(bool revealed) => Status = revealed
        ? $"Revealed {SelectedName} at the current zoom."
        : "The selection has no resolved bounds to reveal.";

    public void AddAssetRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (AssetRoots.Contains(fullPath, StringComparer.Ordinal))
        {
            Status = $"Asset root already configured: {fullPath}";
            return;
        }
        AssetRoots.Add(fullPath);
        Assets.Configure(_assetSourcePath, AssetRoots);
        SaveAssetRoots();
        RefreshAssetPresentation($"Added asset root {fullPath}.");
    }

    public void RemoveAssetRoot(string path)
    {
        if (!AssetRoots.Remove(path))
            return;
        Assets.Configure(_assetSourcePath, AssetRoots);
        SaveAssetRoots();
        RefreshAssetPresentation($"Removed asset root {path}.");
    }

    public void RefreshAssets()
    {
        Assets.Refresh();
        RefreshAssetPresentation("Asset caches refreshed.");
    }

    private void SaveAssetRoots()
    {
        try { _assetSettings.Save(AssetRoots); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save asset roots: {ex.Message}";
        }
    }

    private void RefreshAssetPresentation(string status)
    {
        Editor.RefreshAssetLines();
        OnPropertyChanged(nameof(AssetRootsSummary));
        OnPropertyChanged(nameof(Assets));
        Status = status;
    }

    private IEnumerable<string> DescribeAsset(FrameDef frame)
    {
        if (frame.Kind != FrameKind.TEXTURE || frame.Visual?.Texture is not { } texture)
            yield break;
        var asset = Assets.Resolve(texture.File);
        yield return $"asset status {asset.Status}";
        if (!texture.TexCoords.IsValid || texture.TexCoords.Width <= 0 || texture.TexCoords.Height <= 0)
            yield return "fallback invalid or reversed texCoords cannot be rendered";
        if (asset.PhysicalPath is { } path)
            yield return $"resolved {path}";
        if (asset.SourceKind is { } kind)
            yield return $"source {kind}: {asset.SourceRoot}";
        if (asset.Format != TextureFileFormat.Unknown)
            yield return $"format {asset.Format}";
        if (asset.Width is { } width && asset.Height is { } height)
            yield return $"image {width} x {height}";
        if (!asset.CanRender)
            yield return $"fallback {asset.Diagnostic.Message}";
    }

    private static string? ResolveSourcePath(Project project, string? projectPath)
    {
        var reference = project.Source?.ReferencePath;
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        if (Path.IsPathRooted(reference))
            return reference;
        return projectPath is { Length: > 0 }
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, reference))
            : null;
    }

    private void ReplaceFrame(string name, FrameDef updated, string? rename = null)
    {
        var targetName = rename ?? updated.Name;

        var frames = Project.Frames.Select(f => f.Name == name
                ? updated with { Name = targetName }
                : f with
                {
                    Parent = f.Parent == name ? targetName : f.Parent,
                    RelativeTo = f.RelativeTo == name ? targetName : f.RelativeTo,
                }).ToArray();
        if (frames.Select(f => f with { }).SequenceEqual(Project.Frames.Select(f => f with { })))
            return;

        Project = Project with { Frames = frames };
        IsDirty = true;
        RelaidOut(Project, name, null);
    }

    private void RelaidOut(Project project, string? selection, string? status)
    {
        Layout = LayoutResolver.Resolve(project);
        RebuildTree(project);
        SelectedTreeNode = FindNode(selection);
        Editor.Refresh(project, selection);
        Editor.RefreshResolved(selection is null ? null : Layout.Frames.GetValueOrDefault(selection));
        CanvasSelectionNames.Clear();
        foreach (var name in Layout.PaintOrder)
            CanvasSelectionNames.Add(name);

        OnPropertyChanged(nameof(ProjectDescription));
        SelectionSummary = Editor.ResolvedSummary;

        if (status is not null)
            Status = status;
        else if (Editor.HasIssues)
            Status = $"Issue: {Editor.IssueText}";
        else if (selection is not null)
            Status = $"{selection}: {Editor.ResolvedSummary}";
        else
            Status = project.IssueSummary();
    }

    /// <summary>
    /// Rebuilds the tree through the current structural filter and search.
    /// </summary>
    /// <remarks>
    /// Filtering and searching decide what is DISPLAYED and never what EXISTS: the projection is
    /// discarded and recomputed from the project every time, so a filter cannot delete a widget and
    /// clearing the search box always restores the full hierarchy.
    /// </remarks>
    private void RebuildTree(Project project)
    {
        _treeProjection = TreeProjectionBuilder.Resolve(project, TreeFilter, TreeSearch);
        var visible = _treeProjection.Visible;

        TreeRoots.Clear();
        foreach (var root in FrameHierarchy.Children(project, null))
        {
            if (BuildNode(project, root, visible) is { } node)
                TreeRoots.Add(node);
        }

        OnPropertyChanged(nameof(IsTreeFiltering));
        OnPropertyChanged(nameof(TreeFilterSummary));
    }

    /// <summary>
    /// Builds a node, or null when the frame itself is filtered out.
    /// </summary>
    /// <remarks>
    /// The recursion still descends into a filtered-out node, because the projection already adds
    /// the ancestors of every match to the visible set - so a frame whose own subtree matched is
    /// itself visible, and a frame with no visible descendants is correctly dropped. Returning null
    /// for the latter is what makes STRUCTURE and VISUAL actually narrow the tree instead of
    /// re-including everything as context.
    /// </remarks>
    private static FrameTreeNode? BuildNode(Project project, FrameDef frame, IReadOnlySet<string> visible)
    {
        if (!visible.Contains(frame.Name))
            return null;

        var children = new List<FrameTreeNode>();
        foreach (var child in FrameHierarchy.Children(project, frame.Name))
        {
            if (BuildNode(project, child, visible) is { } node)
                children.Add(node);
        }

        return new FrameTreeNode(frame, children);
    }

    private IReadOnlyList<FrameOption> BuildFrameOptions()
    {
        var options = new List<FrameOption> { new(null) };
        foreach (var frame in Project.Frames)
        {
            // A frame cannot be parented under itself or one of its own descendants, and must
            // not anchor to them either, or the engine would report a cycle.
            if (SelectedName is { } name && FrameHierarchy.IsDescendantOf(Project, frame.Name, name))
                continue;

            options.Add(new FrameOption(frame.Name));
        }

        return options;
    }

    private IReadOnlyList<AnchorPoint> AnchorOptions() => AnchorPoints.All;

    private string UniqueName(string prefix)
    {
        var candidate = prefix;
        var index = 1;
        while (Project.Contains(candidate))
            candidate = $"{prefix}{++index}";

        return candidate;
    }

    private static string Number(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FileName(string path)
    {
        try
        {
            return System.IO.Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>
/// Where the open project came from, phrased for the banner above the canvas.
/// </summary>
/// <remarks>
/// This is the one piece of UI state that must not be allowed to look tidy. A FrameXML import
/// is a partial view of somebody else's runtime layout, and the banner exists so that is
/// stated before the user draws any conclusions from a picture.
/// </remarks>
/// <summary>
/// What the import banner shows: where the layout came from, and how much of it carried across.
/// </summary>
/// <remarks>
/// The support counts are carried separately from <see cref="Summary"/> instead of being parsed
/// back out of its sentence. "42 fully supported, 9 partially supported" is exactly the kind of
/// sentence that gets reworded, and a banner whose numbers come from prose breaks the first time
/// someone improves the wording.
/// </remarks>
public sealed record SourceSummary
{
    /// <summary>The imported file name.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Where the XML was read from, for display only. Nothing reopens it: a project saved
    /// elsewhere must still open when the original addon file is gone.
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>True when the file could not be imported at all.</summary>
    public bool ImportFailed { get; init; }

    /// <summary>The importer's one-line headline, or the reason it failed.</summary>
    public required string Summary { get; init; }

    /// <summary>Layout elements discovered in the file.</summary>
    public int Discovered { get; init; }

    /// <summary>Elements whose geometry FrameForge reproduced exactly.</summary>
    public int FullySupported { get; init; }

    /// <summary>Elements whose geometry is usable but not exact.</summary>
    public int PartiallySupported { get; init; }

    /// <summary>Elements that could not be represented at all.</summary>
    public int Unsupported { get; init; }

    /// <summary>How many warnings the importer reported.</summary>
    public int WarningCount { get; init; }

    /// <summary>How many informational findings the importer reported.</summary>
    public int InfoCount { get; init; }

    /// <summary>How many errors the importer reported.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Builds the summary for a file that could not be read.</summary>
    public static SourceSummary Failed(string fileName, string path, string reason, int warnings) =>
        new()
        {
            FileName = fileName,
            SourcePath = path,
            ImportFailed = true,
            Summary = reason,
            WarningCount = warnings,
            ErrorCount = 1,
        };

    /// <summary>Builds the summary for a file that imported.</summary>
    public static SourceSummary Imported(string fileName, string path, FrameXmlImportResult result) =>
        new()
        {
            FileName = fileName,
            SourcePath = path,
            Summary = result.SummaryText,
            Discovered = result.Elements.Count,
            FullySupported = result.FullySupported,
            PartiallySupported = result.PartiallySupported,
            Unsupported = result.Unsupported,
            WarningCount = result.Warnings.Count,
            InfoCount = result.Infos.Count,
            ErrorCount = result.Errors.Count,
        };

    /// <summary>Headline shown at the left of the banner.</summary>
    public string Headline => ImportFailed
        ? $"Import failed: {FileName}"
        : $"Read-only import from {FileName}";

    /// <summary>Second line: the counts, and what they do and do not mean.</summary>
    public string Detail
    {
        get
        {
            if (ImportFailed)
                return Summary;

            var verdict = Unsupported == 0
                ? "Nothing was dropped."
                : $"{Unsupported} element(s) could not be represented at all.";

            return $"{FullySupported} fully / {PartiallySupported} partial / {Unsupported} unsupported " +
                   $"of {Discovered} layout elements. {verdict} " +
                   (WarningCount == 0
                       ? "Nothing was left out."
                       : $"{WarningCount + InfoCount} finding(s) below describe what FrameForge could not carry across.");
        }
    }

    /// <summary>How the findings list should be headed.</summary>
    public string DiagnosticsHeader
    {
        get
        {
            if (ImportFailed)
                return "IMPORT DIAGNOSTICS (FAILED)";

            var parts = new List<string>();
            if (ErrorCount > 0)
                parts.Add($"{ErrorCount} error{(ErrorCount == 1 ? string.Empty : "s")}");
            if (WarningCount > 0)
                parts.Add($"{WarningCount} warning{(WarningCount == 1 ? string.Empty : "s")}");
            if (InfoCount > 0)
                parts.Add($"{InfoCount} note{(InfoCount == 1 ? string.Empty : "s")}");

            return $"IMPORT DIAGNOSTICS ({string.Join(", ", parts)})";
        }
    }
}

/// <summary>Layout diagnostics rendered into the status bar.</summary>
public static class ProjectDiagnostics
{
    /// <summary>A one-line summary of unresolved frames, or a confirmation when clean.</summary>
    public static string IssueSummary(this Project project)
    {
        var layout = LayoutResolver.Resolve(project);
        return layout.Issues.Count == 0
            ? $"Layout is clean: {layout.Rects.Count} of {project.Frames.Count} frames resolved."
            : $"{layout.Issues.Count} layout issue(s): {string.Join("; ", layout.Issues.Take(3).Select(i => i.ToString()))}";
    }
}
