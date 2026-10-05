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
using FrameForge.Desktop.Inspection;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Templates;

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
    private static readonly string[] NativeHuntsStockFonts =
    [
        "GameFontNormal", "GameFontHighlight", "GameFontNormalSmall", "GameFontNormalLarge",
        "GameFontHighlightSmall", "GameFontHighlightLarge",
    ];
    private Project _project = ProjectFactory.Empty();
    private Project _presentationProject = ProjectFactory.Empty();
    private bool _syncingDesignUi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesignWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsInspectWorkspace))]
    private WorkspaceExperience _workspace = WorkspaceExperience.Design;

    public bool IsDesignWorkspace => Workspace == WorkspaceExperience.Design;
    public bool IsInspectWorkspace => Workspace == WorkspaceExperience.Inspect;
    public IReadOnlyList<WorkspaceOption> WorkspaceOptions { get; private set; } = [];

    [ObservableProperty] private string _designNameDraft = string.Empty;
    [ObservableProperty] private string _newObjectName = string.Empty;
    [ObservableProperty] private string _newImageAsset = string.Empty;
    [ObservableProperty] private string _stateNameDraft = string.Empty;
    [ObservableProperty] private DesignStateChoice? _activeDesignState;
    [ObservableProperty] private DesignStateChoice? _selectedAuthoredState;
    public ObservableCollection<DesignStateChoice> DesignStateOptions { get; } = [];
    public ObservableCollection<DesignStateChoice> AuthoredStateOptions { get; } = [];
    public string SelectedStateMembership => SelectedName is null ? "All States" :
        Project.Editor.DesignObjectFor(SelectedName) is not { StateIds.Count: > 0 } item
            ? "All States"
            : string.Join(", ", item.StateIds.Select(id => Project.Editor.DesignStates.FirstOrDefault(s => s.Id == id)?.Name ?? id));
    public bool HasConceptualStockFramework => Project.Editor.Groups.Any(group => group.Concept == "stock-framework");
    public EditorGroup? ConceptualStockFramework => Project.Editor.Groups.FirstOrDefault(group => group.Concept == "stock-framework");
    public bool IsStockFrameworkSelected => ConceptualStockFramework?.Members.Contains(SelectedName ?? string.Empty, StringComparer.Ordinal) == true;
    public string StockFrameworkAction => ConceptualStockFramework?.Locked == true
        ? "Unlock for Editing" : "Lock Blizzard Dungeon Finder Frame";
    public bool CanBrowseDesignAssets => ProjectPath.Length > 0;
    public bool IsDesignImageSelected => SelectedFrame?.Kind == FrameKind.TEXTURE
        && Project.Editor.DesignObjectFor(SelectedName) is not null;
    public string SelectedDesignAssetReference =>
        Project.Editor.DesignObjectFor(SelectedName)?.DesignAsset ?? string.Empty;
    private ResolvedTextureAsset? SelectedDesignAsset => IsDesignImageSelected
        ? Assets.Resolve(SelectedDesignAssetReference)
        : null;
    public string SelectedDesignAssetPhysicalPath => SelectedDesignAsset?.PhysicalPath ?? "Not resolved";
    public string SelectedDesignAssetDimensions => SelectedDesignAsset is { Width: { } width, Height: { } height }
        ? $"{width} × {height}"
        : "Unknown";
    public string SelectedDesignAssetFormat => SelectedDesignAsset?.Format switch
    {
        TextureFileFormat.Blp => "BLP",
        TextureFileFormat.Png => "PNG (design source only)",
        TextureFileFormat.Tga => "TGA",
        _ => "Unknown",
    };
    public string SelectedDesignAssetOwnership => SelectedDesignAsset?.SourceKind == AssetSourceKind.ProjectRelative
        ? "Project-owned / portable"
        : "Project asset unresolved";
    public string SelectedDesignAssetDiagnostic => SelectedDesignAsset?.Diagnostic.Message ?? string.Empty;
    public string SelectedWowExportReference => "Not assigned — future export work";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(CanBrowseDesignAssets))]
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

    /// <summary>Presentation-only origin categories; never changes source visibility.</summary>
    [ObservableProperty]
    private OriginVisibility _originFilter = OriginVisibility.All;

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

    public IReadOnlyList<OriginVisibilityToggle> OriginVisibilityToggles { get; }

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
    private readonly IWoWClientAssetProvider _wowAssets;
    private readonly IStockTemplateResolver _stockTemplates;
    private readonly IPreviewStateRegistry _previewStates;
    private PreviewOverrideSet _activePreviewOverrides = new(
        PreviewStateRegistry.XmlDefaults,
        new Dictionary<string, PreviewFrameOverride>(StringComparer.Ordinal), []);
    private PreviewStateDefinition? _selectedPreviewState;
    private ElementOriginClassifier _originClassifier = null!;
    private VisualCompositionInspector _compositionInspector = null!;
    private IReadOnlyDictionary<string, ElementOrigin> _elementOrigins = new Dictionary<string, ElementOrigin>();
    private string? _assetSourcePath;
    private WowClientValidation _wowClient = new(WowClientValidationStatus.NotConfigured, null, null, null, [],
        "No WoW client is configured.");

    /// <summary>Application-local roots searched after source-relative content.</summary>
    public ObservableCollection<string> AssetRoots { get; } = [];

    /// <summary>The reusable resolver shared by the inspector and canvas.</summary>
    public TextureAssetResolver Assets { get; } = new();

    public IWoWClientAssetProvider WoWAssets => _wowAssets;
    public IStockTemplateResolver StockTemplates => _stockTemplates;
    public PreviewOverrideSet ActivePreviewOverrides => _activePreviewOverrides;
    public Project PresentationProject => _presentationProject;
    public IReadOnlySet<string> HiddenByOrigin { get; private set; } = new HashSet<string>();
    public IReadOnlySet<string> LockedNames { get; private set; } = new HashSet<string>();
    public IReadOnlySet<string> PreferredSelectionNames { get; private set; } = new HashSet<string>();

    public ObservableCollection<VisualComponentInfo> VisualComposition { get; } = [];
    public bool HasVisualComposition => VisualComposition.Count > 0;
    public string CompositionSizeSource { get; private set; } = string.Empty;
    public string CompositionAppearanceSource { get; private set; } = string.Empty;
    public string ResizeGuidance { get; private set; } = string.Empty;

    public ObservableCollection<EditorGroup> Groups { get; } = [];
    [ObservableProperty] private EditorGroup? _selectedGroup;
    [ObservableProperty] private string _groupNameDraft = string.Empty;

    public string SelectedOrigin
    {
        get
        {
            if (SelectedFrame is null)
                return string.Empty;
            var primary = _elementOrigins.GetValueOrDefault(SelectedFrame.Name, ElementOrigin.ProjectSource);
            if (primary == ElementOrigin.RuntimeDesignTime && !SelectedFrame.Placeholder)
                return "Project / imported source; Runtime / design-time override active";
            return primary.Label();
        }
    }
    public string SelectedElementKind => SelectedFrame?.Kind.TagName() ?? string.Empty;
    public string SelectedSourceFile => SelectedFrame?.Placeholder == true
        ? "Synthesized by FrameForge"
        : Project.Source?.FileName ?? "FrameForge project";
    public string SelectedSourcePath => SelectedFrame?.Placeholder == true
        ? string.Empty
        : _assetSourcePath ?? Project.Source?.ReferencePath ?? string.Empty;
    public string SelectedSourceLocation => SelectedFrame?.SourceLocation?.ToString() ?? "Location unavailable";
    public string SelectedInheritance => SelectedFrame?.Inherits ?? "(none)";
    public string SelectedParentName => SelectedFrame?.Parent ?? "UIParent / screen";
    public string SelectedGroupMembership => SelectedFrame is null
        ? string.Empty
        : string.Join(", ", Project.Editor.GroupsFor(SelectedFrame.Name).DefaultIfEmpty("(none)"));
    public string SelectedPreviewProvenance => SelectedFrame is not null && _activePreviewOverrides.Find(SelectedFrame.Name) is not null
        ? $"Effective values differ under {ActivePreviewOverrides.State.Label}; source data is unchanged."
        : "No design-time override on this element.";
    public bool IsSelectionLocked => Project.Editor.IsLocked(SelectedName);
    public bool SelectedElementLocked
    {
        get => SelectedName is not null && Project.Editor.LockedElements.Contains(SelectedName, StringComparer.Ordinal);
        set => SetElementLocked(SelectedName, value);
    }
    public bool SelectedGroupLocked
    {
        get => SelectedGroup?.Locked ?? false;
        set
        {
            if (SelectedGroup is not null)
                SetGroupLocked(SelectedGroup.Name, value);
        }
    }

    /// <summary>States applicable to this document; definitions are built-in and never serialized.</summary>
    public ObservableCollection<PreviewStateDefinition> PreviewStateOptions { get; } = [];

    public PreviewStateDefinition? SelectedPreviewState
    {
        get => _selectedPreviewState;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedPreviewState))
                return;
            _selectedPreviewState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewStateExplanation));
            RelaidOut(Project, SelectedName, $"Design-time preview state: {value.Label}. Source XML is unchanged.");
        }
    }

    public string PreviewStateExplanation => SelectedPreviewState?.Description
        ?? PreviewStateRegistry.XmlDefaults.Description;

    public string WoWClientPath => _wowClient.ClientPath ?? string.Empty;
    public string WoWClientVersion => _wowClient.Build?.ToString() ?? "Unknown";
    public string WoWClientLocale => _wowClient.Locale ?? "Unknown";
    public string WoWClientStatus => _wowClient.Message;
    public bool HasWoWClientSelection => _wowClient.ClientPath is { Length: > 0 };
    public bool CanResolveStockAssets => _wowClient.IsValid
                                         && (MissingStockAssetReferences().Count > 0 || !StockDefinitionsReady());
    public string StockAssetsSummary
    {
        get
        {
            var stock = StockAssetReferences();
            var available = stock.Count(reference => Assets.Resolve(reference).CanRender);
            return $"Stock assets: {stock.Count} required, {available} available";
        }
    }

    public string StockDefinitionsSummary => !RequiresStockDefinitions()
        ? "Stock definitions: not required by this project"
        : StockDefinitionsReady()
            ? $"Stock definitions: template and 6 font styles ready ({_stockTemplates.Diagnostics.Count} boundary/compatibility diagnostics)"
            : $"Stock definitions: unavailable or incomplete ({_stockTemplates.Diagnostics.Count} diagnostics)";

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

    public void SetOriginVisible(OriginVisibility flag, bool enabled) =>
        OriginFilter = enabled ? OriginFilter | flag : OriginFilter & ~flag;

    /// <summary>Returns the canvas to this mode's own defaults.</summary>
    public void ResetViewToModeDefaults()
    {
        CanvasFilter = ViewPolicy.DefaultsFor(ViewMode);
        OriginFilter = OriginVisibility.All;
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

    partial void OnOriginFilterChanged(OriginVisibility value)
    {
        foreach (var toggle in OriginVisibilityToggles)
            toggle.Refresh();
        RefreshOriginSets();
        OnPropertyChanged(nameof(HiddenByOrigin));
        Status = "Origin visibility changed. Project visibility and source XML are unchanged.";
    }

    partial void OnSelectedGroupChanged(EditorGroup? value)
    {
        GroupNameDraft = value?.Name ?? string.Empty;
        OnPropertyChanged(nameof(SelectedGroupLocked));
    }

    partial void OnDesignNameDraftChanged(string value)
    {
        if (_syncingDesignUi || SelectedFrame is null || string.IsNullOrWhiteSpace(value))
            return;
        var values = Project.Editor.DesignObjects.ToList();
        var index = values.FindIndex(item => item.FrameName == SelectedFrame.Name);
        var item = index >= 0 ? values[index] : new DesignObjectMetadata { FrameName = SelectedFrame.Name };
        item = item with { DisplayName = value.Trim() };
        if (index >= 0) values[index] = item; else values.Add(item);
        Project = Project with { Editor = Project.Editor with { DesignObjects = values } };
        IsDirty = true;
        RebuildTree(Project);
        NotifySelectionInspection();
    }

    partial void OnActiveDesignStateChanged(DesignStateChoice? value)
    {
        if (_syncingDesignUi || value is null)
            return;
        Project = Project with { Editor = Project.Editor with { ActiveDesignStateId = value.Id } };
        IsDirty = true;
        RelaidOut(Project, SelectedName, $"Design state: {value.Name}. Lua was not executed.");
    }

    partial void OnSelectedAuthoredStateChanged(DesignStateChoice? value)
    {
        if (!_syncingDesignUi)
            StateNameDraft = value?.Name ?? string.Empty;
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

    public MainWindowViewModel() : this(null, null, null, null)
    {
    }

    public MainWindowViewModel(string? settingsPath, IWoWClientAssetProvider? wowAssets = null,
        IStockTemplateResolver? stockTemplates = null, IPreviewStateRegistry? previewStates = null)
    {
        _assetSettings = new AssetSettingsStore(settingsPath
            ?? Environment.GetEnvironmentVariable("FRAMEFORGE_SETTINGS_PATH"));
        _wowAssets = wowAssets ?? new WoWClientAssetProvider();
        _stockTemplates = stockTemplates ?? new StockTemplateResolver(_wowAssets);
        _previewStates = previewStates ?? new PreviewStateRegistry();
        var configuration = _assetSettings.LoadConfiguration();
        foreach (var root in configuration.AssetRoots)
            AssetRoots.Add(root);
        _wowClient = _wowAssets.ValidateClient(configuration.WowClientPath);
        ConfigureAssets();
        ModeOptions = [.. Enum.GetValues<CanvasViewMode>().Select(m => new CanvasModeOption(this, m))];
        WorkspaceOptions = [.. Enum.GetValues<WorkspaceExperience>().Select(item => new WorkspaceOption(this, item))];
        LabelPolicyOptions = [.. Enum.GetValues<LabelPolicy>().Select(p => new LabelPolicyOption(this, p))];
        TreeFilterOptions = [.. TreeFilters.All.Select(f => new TreeFilterOption(this, f))];
        VisibilityToggles = [.. VisibilityFilters.Toggles.Select(t => new VisibilityToggle(this, t.Label, t.Flag, t.ToolTip))];
        OriginVisibilityToggles =
        [
            new(this, "Project", OriginVisibility.Project, "Imported/project-authored content"),
            new(this, "Blizzard", OriginVisibility.BlizzardStock, "Stock artwork and resolved templates"),
            new(this, "Runtime", OriginVisibility.RuntimeDesignTime, "Design-time runtime overrides"),
            new(this, "Stand-ins", OriginVisibility.StandIn, "Synthesized unresolved external frames"),
        ];
        _originClassifier = new ElementOriginClassifier(Assets, _stockTemplates, _wowAssets.CacheRoot);
        _compositionInspector = new VisualCompositionInspector(Assets, _stockTemplates, _originClassifier, _wowAssets.CacheRoot);
        RefreshPreviewStateOptions(_project);
        RefreshDesignStates(_project);

        Editor = new FrameEditorViewModel((n, u, r) => ReplaceFrame(n, u, r), BuildFrameOptions, AnchorOptions,
            DescribeAsset);
        RelaidOut(_project, null, "New project. Open a FrameXML or FrameForge project to begin.");
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
    public bool CanEditSelection => SelectedFrame is not null && !IsSelectionLocked;

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
        ConfigureAssets();
        RefreshPreviewStateOptions(project);
        RefreshGroups(project);
        Workspace = project.Editor.Workspace == "inspect" ? WorkspaceExperience.Inspect : WorkspaceExperience.Design;
        foreach (var option in WorkspaceOptions) option.Refresh();
        RefreshDesignStates(project);

        var keep = project.Frames.FirstOrDefault(f => f.Name == SelectedName)?.Name;
        SelectedName = keep;
        RelaidOut(project, keep, status);
        NotifyWoWClientState();
    }

    /// <summary>Creates a fresh project.</summary>
    public void NewProject() => Load(ProjectFactory.Blank(), null, "Blank FrameForge project created. Add a Frame, Text, or Image to begin.");

    public bool NewDungeonFinderProject()
    {
        if (!_wowClient.IsValid)
        {
            Status = "Dungeon Finder requires a configured local WoW 3.3.5a build 12340 client. Open WoW Client settings and validate it first.";
            return false;
        }
        try
        {
            var definitions = _stockTemplates.MaterializeRequired(_wowClient);
            var xmlResult = definitions.FirstOrDefault(item => item.RequestedPath.Equals(@"Interface\FrameXML\LFDFrame.xml", StringComparison.OrdinalIgnoreCase));
            var xmlPath = xmlResult?.CachePath ?? Path.Combine(_wowAssets.CacheRoot, "Interface", "FrameXML", "LFDFrame.xml");
            if (!File.Exists(xmlPath))
            {
                Status = "The validated client did not provide Interface/FrameXML/LFDFrame.xml; no substitute artwork was used.";
                return false;
            }
            var imported = FrameXmlImporter.ImportFile(xmlPath);
            if (!imported.Ok || imported.Project is null)
            {
                Status = $"Could not create the Dungeon Finder framework: {string.Join(" ", imported.Errors.Select(error => error.Message))}";
                return false;
            }
            var importedRoot = imported.Project.Find(StockTemplateResolver.LfdParentFrame);
            if (importedRoot is null)
            {
                Status = "The client LFDFrame.xml did not define LFDParentFrame.";
                return false;
            }
            var subtree = FrameHierarchy.Subtree(imported.Project, importedRoot.Name).ToHashSet(StringComparer.Ordinal);
            var frames = imported.Project.Frames.Where(frame => subtree.Contains(frame.Name))
                .Select(frame => frame.Name == importedRoot.Name ? frame with { Visible = true } : frame)
                .ToArray();
            var representative = frames.FirstOrDefault(frame => frame.Name == StockTemplateResolver.LfdParentFrame)
                                 ?? frames.FirstOrDefault();
            if (representative is null)
            {
                Status = "The client LFDFrame.xml contained no supported layout elements.";
                return false;
            }
            var editor = new EditorMetadata
            {
                Groups =
                [
                    new EditorGroup
                    {
                        Name = "Blizzard Dungeon Finder Frame",
                        Members = [.. frames.Select(frame => frame.Name)],
                        Locked = true,
                        Expanded = false,
                        Concept = "stock-framework",
                        StockIdentity = "wow-3.3.5a-12340:Interface/FrameXML/LFDFrame.xml:LFDParentFrame",
                    },
                ],
                DesignObjects = [new DesignObjectMetadata { FrameName = representative.Name, DisplayName = "Blizzard Dungeon Finder Frame" }],
            };
            var project = imported.Project with { Name = "Dungeon Finder UI", Frames = frames, Source = null, Editor = editor };
            Load(project, null, "Created a protected Dungeon Finder framework from the validated local WoW client.");
            foreach (var reference in EnumerateAssetReferences().Distinct(StringComparer.OrdinalIgnoreCase))
                _wowAssets.Materialize(reference, _wowClient);
            Assets.Refresh();
            RelaidOut(Project, representative.Name, Status);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Could not create the Dungeon Finder framework safely: {ex.Message}";
            return false;
        }
    }

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
        ConfigureAssets();

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
            ConfigureAssets();
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
        if (Project.Editor.IsLocked(name))
        {
            Status = $"{name} is locked. Unlock the element or its group before moving it.";
            return;
        }

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

    public void AddDesignFrame() => AddDesignObject(FrameKind.FRAME, "Frame / Container");
    public void AddDesignText() => AddDesignObject(FrameKind.FONTSTRING, "Text");
    public void AddDesignImage() => AddDesignObject(FrameKind.TEXTURE, "Image");

    public bool PrepareDesignAssetBrowse()
    {
        if (CanBrowseDesignAssets)
            return true;
        Status = "Save the FrameForge project before choosing project-owned artwork.";
        return false;
    }

    public bool SetNewDesignImageFromFile(string path)
    {
        if (!TryMakeProjectAssetReference(path, out var reference, out var error))
        {
            Status = error;
            return false;
        }
        NewImageAsset = reference;
        Status = $"Selected project image {reference}.";
        return true;
    }

    public bool ChangeSelectedDesignImageFromFile(string path)
    {
        if (!IsDesignImageSelected || SelectedName is not { } name || SelectedFrame is not { } frame)
            return false;
        if (IsSelectionLocked)
        {
            Status = $"{name} is locked. Unlock it before changing its image.";
            return false;
        }
        if (!TryMakeProjectAssetReference(path, out var reference, out var error))
        {
            Status = error;
            return false;
        }

        var texture = frame.Visual?.Texture ?? new TextureVisual(null, TexCoords.Full);
        var updated = frame with
        {
            Visual = (frame.Visual ?? new FrameVisual()) with { Texture = texture with { File = reference } },
        };
        var designObjects = Project.Editor.DesignObjects.Select(item => item.FrameName == name
            ? item with { DesignAsset = reference }
            : item).ToArray();
        Project = Project with
        {
            Frames = [.. Project.Frames.Select(item => item.Name == name ? updated : item)],
            Editor = Project.Editor with { DesignObjects = designObjects },
        };
        IsDirty = true;
        ConfigureAssets();
        RelaidOut(Project, name, $"Changed image to {reference}.");
        return true;
    }

    private bool TryMakeProjectAssetReference(string path, out string reference, out string error)
    {
        reference = string.Empty;
        error = string.Empty;
        if (!PrepareDesignAssetBrowse())
        {
            error = Status;
            return false;
        }
        if (!File.Exists(path))
        {
            error = $"The selected image does not exist: {path}";
            return false;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
        var relative = Path.GetRelativePath(projectDirectory, Path.GetFullPath(path)).Replace('\\', '/');
        if (!TextureAssetResolver.TryNormalizeProjectAsset(relative, out reference, out var reason))
        {
            error = $"Choose an image inside the project directory. {reason}";
            return false;
        }
        if (!IsSupportedDesignImage(reference))
        {
            error = "Design images must be PNG, TGA, or BLP files.";
            reference = string.Empty;
            return false;
        }
        return true;
    }

    private bool TryValidateDesignAssetReference(string value, out string? reference, out string error)
    {
        reference = null;
        error = string.Empty;
        if (!CanBrowseDesignAssets)
        {
            error = "Save the FrameForge project before assigning project-owned artwork.";
            return false;
        }
        if (!TextureAssetResolver.TryNormalizeProjectAsset(value, out var normalized, out var reason))
        {
            error = reason!;
            return false;
        }
        if (!IsSupportedDesignImage(normalized))
        {
            error = "Design images must be PNG, TGA, or BLP files.";
            return false;
        }
        reference = normalized;
        return true;
    }

    private static bool IsSupportedDesignImage(string reference) =>
        Path.GetExtension(reference).ToLowerInvariant() is ".png" or ".tga" or ".blp";

    private void AddDesignObject(FrameKind kind, string fallbackName)
    {
        var displayName = string.IsNullOrWhiteSpace(NewObjectName) ? fallbackName : NewObjectName.Trim();
        string? designAsset = null;
        if (kind == FrameKind.TEXTURE && !string.IsNullOrWhiteSpace(NewImageAsset)
            && !TryValidateDesignAssetReference(NewImageAsset, out designAsset, out var assetError))
        {
            Status = $"Image was not added: {assetError}";
            return;
        }
        var internalName = UniqueName("DesignObject");
        // Basic DESIGN creation starts at the conceptual project level. Parent changes remain
        // available in the property editor when deliberate nesting is wanted.
        string? parent = null;
        FrameVisual? visual = kind switch
        {
            FrameKind.FONTSTRING => new FrameVisual { Text = new TextVisual("Text", "CENTER", "MIDDLE", null) },
            FrameKind.TEXTURE => new FrameVisual { Texture = new TextureVisual(
                designAsset, TexCoords.Full, null, null, null, null) },
            _ => null,
        };
        var frame = new FrameDef
        {
            Name = internalName,
            Parent = parent,
            Kind = kind,
            Width = kind == FrameKind.TEXTURE ? 64 : 120,
            Height = kind == FrameKind.FONTSTRING ? 24 : 64,
            Point = AnchorPoint.CENTER,
            RelativePoint = AnchorPoint.CENTER,
            Visual = visual,
        };
        var designObject = new DesignObjectMetadata
        {
            FrameName = internalName,
            DisplayName = displayName,
            DesignAsset = kind == FrameKind.TEXTURE ? designAsset : null,
        };
        Project = Project with
        {
            Frames = [.. Project.Frames, frame],
            Editor = Project.Editor with { DesignObjects = [.. Project.Editor.DesignObjects, designObject] },
        };
        NewObjectName = string.Empty;
        NewImageAsset = string.Empty;
        IsDirty = true;
        SelectedName = internalName;
        ConfigureAssets();
        RelaidOut(Project, internalName, $"Added {kind.TagName()} \"{displayName}\" in All States.");
    }

    public void SetWorkspace(WorkspaceExperience workspace)
    {
        if (Workspace == workspace)
            return;
        Workspace = workspace;
        Project = Project with { Editor = Project.Editor with { Workspace = workspace == WorkspaceExperience.Design ? "design" : "inspect" } };
        IsDirty = true;
        foreach (var option in WorkspaceOptions) option.Refresh();
        RebuildTree(Project);
        SelectedTreeNode = FindNode(SelectedName);
        NotifySelectionInspection();
        Status = workspace == WorkspaceExperience.Design
            ? "DESIGN: concise objects and authoring controls."
            : "INSPECT: source hierarchy, provenance, composition, and diagnostics.";
    }

    public void SetConceptualStockExpanded(bool expanded)
    {
        var group = ConceptualStockFramework;
        if (group is null) return;
        UpdateEditor(Project.Editor with
        {
            Groups = [.. Project.Editor.Groups.Select(item => item.Name == group.Name ? item with { Expanded = expanded } : item)],
        }, expanded
            ? "Expanded the Blizzard framework for inspection; it remains locked."
            : "Collapsed the Blizzard framework; its lock state is unchanged.");
    }

    public void SetConceptualStockLocked(bool locked)
    {
        var group = ConceptualStockFramework;
        if (group is null) return;
        SetGroupLocked(group.Name, locked);
    }

    public void CreateDesignState()
    {
        var name = StateNameDraft.Trim();
        if (name.Length == 0 || Project.Editor.DesignStates.Any(state => state.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "Enter a unique design-state name.";
            return;
        }
        var baseId = string.Concat(name.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-')).Trim('-');
        if (baseId.Length == 0) baseId = "state";
        var id = baseId;
        var suffix = 2;
        while (Project.Editor.DesignStates.Any(state => state.Id == id)) id = $"{baseId}-{suffix++}";
        UpdateEditor(Project.Editor with { DesignStates = [.. Project.Editor.DesignStates, new DesignState { Id = id, Name = name }] },
            $"Created design state \"{name}\".");
        StateNameDraft = name;
        SelectedAuthoredState = AuthoredStateOptions.FirstOrDefault(item => item.Id == id);
    }

    public void RenameSelectedDesignState()
    {
        if (SelectedAuthoredState?.Id is not { } id || string.IsNullOrWhiteSpace(StateNameDraft)) return;
        var name = StateNameDraft.Trim();
        if (Project.Editor.DesignStates.Any(state => state.Id != id && state.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "Enter a unique design-state name.";
            return;
        }
        UpdateEditor(Project.Editor with
        {
            DesignStates = [.. Project.Editor.DesignStates.Select(state => state.Id == id ? state with { Name = name } : state)],
        }, $"Renamed design state to \"{name}\".");
        SelectedAuthoredState = AuthoredStateOptions.FirstOrDefault(item => item.Id == id);
    }

    public void DeleteSelectedDesignState()
    {
        if (SelectedAuthoredState?.Id is not { } id) return;
        var name = SelectedAuthoredState.Name;
        var objects = Project.Editor.DesignObjects.Select(item => item with
        {
            StateIds = [.. item.StateIds.Where(stateId => stateId != id)],
        }).ToArray();
        UpdateEditor(Project.Editor with
        {
            DesignStates = [.. Project.Editor.DesignStates.Where(state => state.Id != id)],
            DesignObjects = objects,
            ActiveDesignStateId = Project.Editor.ActiveDesignStateId == id ? null : Project.Editor.ActiveDesignStateId,
        }, $"Deleted design state \"{name}\"; affected objects now use their remaining memberships or All States.");
    }

    public void AssignSelectionToAllStates() => SetSelectionStateIds([]);

    public void AssignSelectionToSelectedState()
    {
        if (SelectedAuthoredState?.Id is { } id)
        {
            var current = Project.Editor.DesignObjectFor(SelectedName)?.StateIds ?? [];
            SetSelectionStateIds(current.Contains(id, StringComparer.Ordinal) ? current : [.. current, id]);
        }
    }

    public void RemoveSelectionFromSelectedState()
    {
        if (SelectedAuthoredState?.Id is not { } id) return;
        var current = Project.Editor.DesignObjectFor(SelectedName)?.StateIds ?? [];
        SetSelectionStateIds([.. current.Where(item => item != id)]);
    }

    private void SetSelectionStateIds(IReadOnlyList<string> stateIds)
    {
        if (SelectedName is null) return;
        var values = Project.Editor.DesignObjects.ToList();
        var index = values.FindIndex(item => item.FrameName == SelectedName);
        var existing = index >= 0 ? values[index] : new DesignObjectMetadata { FrameName = SelectedName };
        var updated = existing with { StateIds = [.. stateIds] };
        if (index >= 0) values[index] = updated; else values.Add(updated);
        UpdateEditor(Project.Editor with { DesignObjects = values }, stateIds.Count == 0
            ? $"Assigned {Project.Editor.DisplayNameFor(SelectedFrame!)} to All States."
            : $"Assigned {Project.Editor.DisplayNameFor(SelectedFrame!)} to {SelectedAuthoredState?.Name}.");
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
        if (Project.Editor.IsLocked(name))
        {
            Status = $"{name} is locked. Unlock it before deleting it.";
            return;
        }

        var descendants = FrameHierarchy.Subtree(Project, name).Skip(1).ToHashSet(StringComparer.Ordinal);
        var frames = Project.Frames
            .Where(f => f.Name != name)
            .Select(f => f with
            {
                Parent = f.Parent == name ? removed.Parent : f.Parent,
                RelativeTo = f.RelativeTo == name ? null : f.RelativeTo,
            })
            .ToArray();

        // A deleted root has no surviving selection.  "the root" is status text, not a
        // frame identity; letting it escape into SelectedName creates a dangling selection.
        var survivingParent = removed.Parent is { } parentName && frames.Any(frame => frame.Name == parentName)
            ? parentName
            : null;
        var parentDescription = survivingParent ?? "the root";
        Project = Project with
        {
            Frames = frames,
            Editor = Project.Editor with
            {
                LockedElements = [.. Project.Editor.LockedElements.Where(item => item != name)],
                Groups = [.. Project.Editor.Groups.Select(group => group with
                {
                    Members = [.. group.Members.Where(item => item != name)],
                })],
                DesignObjects = [.. Project.Editor.DesignObjects.Where(item => item.FrameName != name)],
            },
        };
        RefreshGroups(Project);
        IsDirty = true;
        SelectedName = survivingParent;
        RelaidOut(Project, survivingParent, descendants.Count == 0
            ? $"Deleted \"{name}\"."
            : $"Deleted \"{name}\"; re-parented {descendants.Count} descendant(s) to {parentDescription}.");
    }

    /// <summary>Canvas hit-test entry point; also used to keep the tree in sync.</summary>
    public void OnCanvasSelectionRequested(string? name) => Select(name);

    public void CreateGroup()
    {
        var name = string.IsNullOrWhiteSpace(GroupNameDraft) ? UniqueGroupName("Group") : GroupNameDraft.Trim();
        if (Project.Editor.Groups.Any(group => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"A group named \"{name}\" already exists.";
            return;
        }
        var group = new EditorGroup { Name = name };
        UpdateEditor(Project.Editor with { Groups = [.. Project.Editor.Groups, group] }, $"Created editor group \"{name}\".");
        SelectedGroup = Groups.FirstOrDefault(item => item.Name == name);
    }

    public void RenameSelectedGroup()
    {
        if (SelectedGroup is null || string.IsNullOrWhiteSpace(GroupNameDraft))
            return;
        var oldName = SelectedGroup.Name;
        var newName = GroupNameDraft.Trim();
        if (Project.Editor.Groups.Any(group => group.Name != oldName && group.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"A group named \"{newName}\" already exists.";
            return;
        }
        UpdateEditor(Project.Editor with
        {
            Groups = [.. Project.Editor.Groups.Select(group => group.Name == oldName ? group with { Name = newName } : group)],
        }, $"Renamed editor group \"{oldName}\" to \"{newName}\".");
        SelectedGroup = Groups.FirstOrDefault(item => item.Name == newName);
    }

    public void DeleteSelectedGroup()
    {
        if (SelectedGroup is null)
            return;
        var name = SelectedGroup.Name;
        UpdateEditor(Project.Editor with
        {
            Groups = [.. Project.Editor.Groups.Where(group => group.Name != name)],
        }, $"Deleted editor group \"{name}\"; its elements were not deleted.");
        SelectedGroup = Groups.FirstOrDefault();
    }

    public void AddSelectionToGroup()
    {
        if (SelectedName is null || SelectedGroup is null)
            return;
        var groupName = SelectedGroup.Name;
        UpdateEditor(Project.Editor with
        {
            Groups = [.. Project.Editor.Groups.Select(group => group.Name == groupName
                ? group with { Members = group.Members.Contains(SelectedName, StringComparer.Ordinal)
                    ? group.Members : [.. group.Members, SelectedName] }
                : group)],
        }, $"Added {SelectedName} to \"{groupName}\".");
        SelectedGroup = Groups.FirstOrDefault(item => item.Name == groupName);
    }

    public void RemoveSelectionFromGroup()
    {
        if (SelectedName is null || SelectedGroup is null)
            return;
        var groupName = SelectedGroup.Name;
        UpdateEditor(Project.Editor with
        {
            Groups = [.. Project.Editor.Groups.Select(group => group.Name == groupName
                ? group with { Members = [.. group.Members.Where(item => item != SelectedName)] }
                : group)],
        }, $"Removed {SelectedName} from \"{groupName}\".");
        SelectedGroup = Groups.FirstOrDefault(item => item.Name == groupName);
    }

    public void SetGroupLocked(string groupName, bool locked)
    {
        UpdateEditor(Project.Editor with
        {
            Groups = [.. Project.Editor.Groups.Select(group => group.Name == groupName ? group with { Locked = locked } : group)],
        }, $"{(locked ? "Locked" : "Unlocked")} group \"{groupName}\".");
        SelectedGroup = Groups.FirstOrDefault(item => item.Name == groupName);
    }

    public void SetElementLocked(string? name, bool locked)
    {
        if (name is null)
            return;
        var values = Project.Editor.LockedElements.ToList();
        if (locked && !values.Contains(name, StringComparer.Ordinal))
            values.Add(name);
        if (!locked)
            values.RemoveAll(item => item == name);
        UpdateEditor(Project.Editor with { LockedElements = values }, $"{(locked ? "Locked" : "Unlocked")} {name}.");
    }

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
        ConfigureAssets();
        SaveAssetRoots();
        RefreshAssetPresentation($"Added asset root {fullPath}.");
    }

    public void RemoveAssetRoot(string path)
    {
        if (!AssetRoots.Remove(path))
            return;
        ConfigureAssets();
        SaveAssetRoots();
        RefreshAssetPresentation($"Removed asset root {path}.");
    }

    public void RefreshAssets()
    {
        Assets.Refresh();
        RefreshAssetPresentation("Asset caches refreshed.");
    }

    public void SetWoWClientPath(string path)
    {
        _wowClient = _wowAssets.ValidateClient(path);
        SaveLocalSettings();
        RefreshAssetPresentation(_wowClient.Message);
        NotifyWoWClientState();
    }

    public void ClearWoWClientPath()
    {
        _wowClient = _wowAssets.ValidateClient(null);
        SaveLocalSettings();
        RefreshAssetPresentation("WoW client selection cleared; existing managed cache remains available.");
        NotifyWoWClientState();
    }

    public void RevalidateWoWClient()
    {
        _wowClient = _wowAssets.ValidateClient(_wowClient.ClientPath);
        RefreshAssetPresentation(_wowClient.Message);
        NotifyWoWClientState();
    }

    public IReadOnlyList<AssetMaterializationResult> ResolveMissingStockAssets()
    {
        if (!_wowClient.IsValid)
        {
            Status = "Select a valid WoW 3.3.5a build 12340 client first.";
            return [];
        }
        var requested = MissingStockAssetReferences();
        AssetMaterializationResult[] results;
        try
        {
            var definitions = RequiresStockDefinitions()
                ? _stockTemplates.MaterializeRequired(_wowClient)
                : [];
            results = [.. definitions, .. requested.Select(reference => _wowAssets.Materialize(reference, _wowClient))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = $"Could not resolve stock assets safely: {ex.Message}";
            return [];
        }
        Assets.Refresh();
        RelaidOut(Project, SelectedName, null);
        var succeeded = results.Count(result => result.Success);
        RefreshAssetPresentation($"Resolved {succeeded} of {results.Length} missing stock assets from the local WoW client.");
        NotifyWoWClientState();
        return results;
    }

    public void ClearManagedStockCache()
    {
        try
        {
            _wowAssets.ClearCache();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not clear the managed stock cache: {ex.Message}";
            return;
        }
        Assets.Refresh();
        _stockTemplates.Reload();
        RelaidOut(Project, SelectedName, null);
        RefreshAssetPresentation("FrameForge-managed stock asset cache cleared.");
        NotifyWoWClientState();
    }

    private void SaveAssetRoots()
    {
        SaveLocalSettings();
    }

    private void SaveLocalSettings()
    {
        try
        {
            _assetSettings.SaveConfiguration(new FrameForgeLocalSettings([.. AssetRoots], _wowClient.ClientPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save local settings: {ex.Message}";
        }
    }

    private void RefreshAssetPresentation(string status)
    {
        Editor.RefreshAssetLines();
        OnPropertyChanged(nameof(AssetRootsSummary));
        OnPropertyChanged(nameof(StockAssetsSummary));
        OnPropertyChanged(nameof(StockDefinitionsSummary));
        OnPropertyChanged(nameof(CanResolveStockAssets));
        OnPropertyChanged(nameof(Assets));
        Status = status;
    }

    private IReadOnlyList<string> EffectiveAssetRoots() => [.. AssetRoots, _wowAssets.CacheRoot];

    private void ConfigureAssets() => Assets.Configure(_assetSourcePath, EffectiveAssetRoots(), ProjectPath,
        Project.Editor.DesignObjects.Select(item => item.DesignAsset).OfType<string>());

    private IReadOnlyList<string> StockAssetReferences() => EnumerateAssetReferences()
        .Where(reference => Assets.Resolve(reference).SourceKind is not (AssetSourceKind.SourceRelative or AssetSourceKind.ProjectRelative))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private IReadOnlyList<string> MissingStockAssetReferences() => StockAssetReferences()
        .Where(reference => !Assets.Resolve(reference).CanRender)
        .ToArray();

    private IEnumerable<string> EnumerateAssetReferences()
    {
        foreach (var frame in Project.Frames)
        {
            if (frame.Visual?.Texture?.File is { Length: > 0 } texture)
                yield return texture;
            if (frame.Visual?.StatusBar?.BarTexture is { Length: > 0 } barTexture)
                yield return barTexture;
        }
    }

    private bool RequiresStockDefinitions() => Project.Frames.Any(frame =>
        (frame.Placeholder && frame.Name == StockTemplateResolver.LfdParentFrame)
        || frame.Inherits == StockTemplateResolver.TabTemplate
        || frame.Visual?.Text?.FontTemplate is { } font && NativeHuntsStockFonts.Contains(font, StringComparer.Ordinal));

    private bool StockDefinitionsReady() => !RequiresStockDefinitions()
        || (_stockTemplates.ResolveExternalFrame(StockTemplateResolver.LfdParentFrame) is not null
        && _stockTemplates.ResolveButton(StockTemplateResolver.TabTemplate) is { Status: StockDefinitionStatus.FullyResolved }
        && NativeHuntsStockFonts.All(name => _stockTemplates.ResolveFont(name) is not null));

    private void NotifyWoWClientState()
    {
        OnPropertyChanged(nameof(WoWClientPath));
        OnPropertyChanged(nameof(WoWClientVersion));
        OnPropertyChanged(nameof(WoWClientLocale));
        OnPropertyChanged(nameof(WoWClientStatus));
        OnPropertyChanged(nameof(HasWoWClientSelection));
        OnPropertyChanged(nameof(StockAssetsSummary));
        OnPropertyChanged(nameof(StockDefinitionsSummary));
        OnPropertyChanged(nameof(CanResolveStockAssets));
    }

    private IEnumerable<string> DescribeAsset(FrameDef frame)
    {
        foreach (var line in _activePreviewOverrides.Describe(frame))
            yield return line;
        foreach (var line in _stockTemplates.Describe(frame))
            yield return line;
        var texture = frame.Kind == FrameKind.TEXTURE ? frame.Visual?.Texture : null;
        var reference = texture?.File ?? frame.Visual?.StatusBar?.BarTexture;
        if (reference is null && texture is null)
            yield break;
        if (frame.Visual?.StatusBar?.BarTexture is { } barTexture)
            yield return $"declared barTexture {barTexture}";
        var asset = Assets.Resolve(reference);
        yield return $"asset status {asset.Status}";
        if (texture is not null && (!texture.TexCoords.IsValid || texture.TexCoords.Width <= 0 || texture.TexCoords.Height <= 0))
            yield return "fallback invalid or reversed texCoords cannot be rendered";
        if (asset.PhysicalPath is { } path)
            yield return $"resolved {path}";
        if (asset.SourceKind is { } kind)
            yield return $"source {kind}: {asset.SourceRoot}";
        if (asset.Format != TextureFileFormat.Unknown)
            yield return $"format {(asset.Format == TextureFileFormat.Blp ? "BLP" : asset.Format.ToString().ToUpperInvariant())}";
        if (asset.Width is { } width && asset.Height is { } height)
            yield return $"image {width} x {height}";
        if (asset.Texture?.Image.Description is { } decoder)
            yield return $"decoder {decoder}";
        if (asset.PhysicalPath is { } physical && _wowAssets.GetProvenance(physical) is { } provenance)
        {
            yield return $"client {provenance.Build} / {provenance.Locale}";
            yield return $"archive {provenance.ArchivePath}";
            yield return $"sha256 {provenance.Sha256}";
        }
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
        if (Project.Editor.IsLocked(name))
        {
            Status = $"{name} is locked. Unlock the element or its group before editing geometry.";
            RelaidOut(Project, name, null);
            return;
        }
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

        var editor = Project.Editor;
        if (targetName != name)
        {
            editor = editor with
            {
                LockedElements = [.. editor.LockedElements.Select(item => item == name ? targetName : item)],
                Groups = [.. editor.Groups.Select(group => group with
                {
                    Members = [.. group.Members.Select(item => item == name ? targetName : item)],
                })],
                DesignObjects = [.. editor.DesignObjects.Select(item => item.FrameName == name
                    ? item with { FrameName = targetName }
                    : item)],
            };
        }
        Project = Project with { Frames = frames, Editor = editor };
        RefreshGroups(Project);
        IsDirty = true;
        RelaidOut(Project, name, null);
    }

    private void RelaidOut(Project project, string? selection, string? status)
    {
        // Selection is a model identity.  Normalize it at the refresh boundary so every
        // caller (delete, load, tree, or canvas) gets the same explicit no-selection state.
        if (selection is not null && !project.Contains(selection))
            selection = null;
        SelectedName = selection;

        _activePreviewOverrides = _previewStates.Resolve(project, SelectedPreviewState?.Id);
        var previewProject = _previewStates.Apply(project, _activePreviewOverrides);
        if (project.Editor.ActiveDesignStateId is { } activeState)
        {
            previewProject = previewProject with
            {
                Frames = [.. previewProject.Frames.Select(frame =>
                {
                    var membership = project.Editor.DesignObjectFor(frame.Name)?.StateIds ?? [];
                    return membership.Count == 0 || membership.Contains(activeState, StringComparer.Ordinal)
                        ? frame
                        : frame with { Visible = false };
                })],
            };
        }
        _presentationProject = _stockTemplates.ApplyEffectiveGeometry(previewProject);
        OnPropertyChanged(nameof(PresentationProject));
        OnPropertyChanged(nameof(ActivePreviewOverrides));
        Layout = LayoutResolver.Resolve(_presentationProject);
        var conceptualStock = project.Editor.Groups.Where(group => group.Concept == "stock-framework")
            .SelectMany(group => group.Members).ToHashSet(StringComparer.Ordinal);
        _elementOrigins = project.Frames.ToDictionary(frame => frame.Name,
            frame => conceptualStock.Contains(frame.Name)
                ? ElementOrigin.BlizzardStock
                : _originClassifier.Classify(frame, _activePreviewOverrides), StringComparer.Ordinal);
        RefreshOriginSets();
        RebuildTree(project);
        SelectedTreeNode = FindNode(selection);
        Editor.Refresh(project, selection);
        Editor.RefreshResolved(selection is not null && Layout.Frames.TryGetValue(selection, out var selectedLayout)
            ? selectedLayout
            : null);
        RefreshComposition(selection);
        _syncingDesignUi = true;
        DesignNameDraft = selection is null ? string.Empty : project.Editor.DisplayNameFor(project.Find(selection)!);
        _syncingDesignUi = false;
        CanvasSelectionNames.Clear();
        foreach (var name in Layout.PaintOrder)
            CanvasSelectionNames.Add(name);

        OnPropertyChanged(nameof(ProjectDescription));
        NotifySelectionInspection();
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

    private void RefreshComposition(string? selection)
    {
        var inspection = _compositionInspector.Inspect(Project, PresentationProject, Layout, selection, ActivePreviewOverrides);
        VisualComposition.Clear();
        foreach (var component in inspection.Components)
            VisualComposition.Add(component);
        CompositionSizeSource = inspection.SizeSource;
        CompositionAppearanceSource = inspection.AppearanceSource;
        ResizeGuidance = inspection.ResizeGuidance;
        OnPropertyChanged(nameof(HasVisualComposition));
        OnPropertyChanged(nameof(CompositionSizeSource));
        OnPropertyChanged(nameof(CompositionAppearanceSource));
        OnPropertyChanged(nameof(ResizeGuidance));
    }

    private void RefreshOriginSets()
    {
        HiddenByOrigin = _elementOrigins.Where(pair => !ElementOriginClassifier.IsVisible(pair.Value, OriginFilter))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        LockedNames = Project.Frames.Where(frame => Project.Editor.IsLocked(frame.Name))
            .Select(frame => frame.Name).ToHashSet(StringComparer.Ordinal);
        PreferredSelectionNames = Project.Frames.Where(frame => !Project.Editor.IsLocked(frame.Name)
                && _elementOrigins.GetValueOrDefault(frame.Name, ElementOrigin.ProjectSource)
                    is ElementOrigin.ProjectSource or ElementOrigin.RuntimeDesignTime)
            .Select(frame => frame.Name).ToHashSet(StringComparer.Ordinal);
        OnPropertyChanged(nameof(HiddenByOrigin));
        OnPropertyChanged(nameof(LockedNames));
        OnPropertyChanged(nameof(PreferredSelectionNames));
    }

    private void RefreshGroups(Project project)
    {
        var selectedName = SelectedGroup?.Name;
        Groups.Clear();
        foreach (var group in project.Editor.Groups)
            Groups.Add(group);
        SelectedGroup = Groups.FirstOrDefault(group => group.Name == selectedName) ?? Groups.FirstOrDefault();
    }

    private void RefreshDesignStates(Project project)
    {
        _syncingDesignUi = true;
        DesignStateOptions.Clear();
        DesignStateOptions.Add(DesignStateChoice.All);
        AuthoredStateOptions.Clear();
        foreach (var state in project.Editor.DesignStates)
        {
            var choice = DesignStateChoice.From(state);
            DesignStateOptions.Add(choice);
            AuthoredStateOptions.Add(choice);
        }
        ActiveDesignState = DesignStateOptions.FirstOrDefault(item => item.Id == project.Editor.ActiveDesignStateId)
                            ?? DesignStateChoice.All;
        SelectedAuthoredState = SelectedAuthoredState?.Id is { } selectedId
            ? AuthoredStateOptions.FirstOrDefault(item => item.Id == selectedId)
            : AuthoredStateOptions.FirstOrDefault();
        _syncingDesignUi = false;
        OnPropertyChanged(nameof(SelectedStateMembership));
    }

    private void UpdateEditor(EditorMetadata editor, string status)
    {
        Project = Project with { Editor = editor };
        IsDirty = true;
        RefreshGroups(Project);
        RefreshDesignStates(Project);
        RelaidOut(Project, SelectedName, status);
    }

    private string UniqueGroupName(string prefix)
    {
        var candidate = prefix;
        var index = 1;
        while (Project.Editor.Groups.Any(group => group.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{prefix} {++index}";
        return candidate;
    }

    private void NotifySelectionInspection()
    {
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(SelectedOrigin));
        OnPropertyChanged(nameof(SelectedElementKind));
        OnPropertyChanged(nameof(SelectedSourceFile));
        OnPropertyChanged(nameof(SelectedSourcePath));
        OnPropertyChanged(nameof(SelectedSourceLocation));
        OnPropertyChanged(nameof(SelectedInheritance));
        OnPropertyChanged(nameof(SelectedParentName));
        OnPropertyChanged(nameof(SelectedGroupMembership));
        OnPropertyChanged(nameof(SelectedPreviewProvenance));
        OnPropertyChanged(nameof(IsSelectionLocked));
        OnPropertyChanged(nameof(SelectedElementLocked));
        OnPropertyChanged(nameof(SelectedGroupLocked));
        OnPropertyChanged(nameof(SelectedStateMembership));
        OnPropertyChanged(nameof(HasConceptualStockFramework));
        OnPropertyChanged(nameof(ConceptualStockFramework));
        OnPropertyChanged(nameof(IsStockFrameworkSelected));
        OnPropertyChanged(nameof(StockFrameworkAction));
        OnPropertyChanged(nameof(IsDesignImageSelected));
        OnPropertyChanged(nameof(SelectedDesignAssetReference));
        OnPropertyChanged(nameof(SelectedDesignAssetPhysicalPath));
        OnPropertyChanged(nameof(SelectedDesignAssetDimensions));
        OnPropertyChanged(nameof(SelectedDesignAssetFormat));
        OnPropertyChanged(nameof(SelectedDesignAssetOwnership));
        OnPropertyChanged(nameof(SelectedDesignAssetDiagnostic));
        OnPropertyChanged(nameof(SelectedWowExportReference));
    }

    private void RefreshPreviewStateOptions(Project project)
    {
        PreviewStateOptions.Clear();
        foreach (var state in _previewStates.StatesFor(project))
            PreviewStateOptions.Add(state);
        _selectedPreviewState = PreviewStateOptions.FirstOrDefault() ?? PreviewStateRegistry.XmlDefaults;
        OnPropertyChanged(nameof(SelectedPreviewState));
        OnPropertyChanged(nameof(PreviewStateExplanation));
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
        var stock = Workspace == WorkspaceExperience.Design ? ConceptualStockFramework : null;
        var stockMembers = stock?.Members.ToHashSet(StringComparer.Ordinal) ?? [];
        if (stock is not null)
        {
            var representative = project.Find(StockTemplateResolver.LfdParentFrame)
                                 ?? stock.Members.Select(project.Find).FirstOrDefault(frame => frame is not null);
            if (representative is not null)
            {
                var children = stock.Expanded
                    ? BuildChildren(project, representative.Name, visible, stockMembers)
                    : [];
                TreeRoots.Add(new FrameTreeNode(representative, children, "Blizzard stock / conceptual framework",
                    stock.Locked, stock.Name, stock.Name, true, stock.Expanded));
            }
        }

        var roots = Workspace == WorkspaceExperience.Design && stock is not null
            ? project.Frames.Where(frame => !stockMembers.Contains(frame.Name)
                && (frame.Parent is null || stockMembers.Contains(frame.Parent)))
            : FrameHierarchy.Children(project, null);
        foreach (var root in roots)
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
    private FrameTreeNode? BuildNode(Project project, FrameDef frame, IReadOnlySet<string> visible)
    {
        if (!visible.Contains(frame.Name))
            return null;

        var children = new List<FrameTreeNode>();
        foreach (var child in FrameHierarchy.Children(project, frame.Name))
        {
            if (BuildNode(project, child, visible) is { } node)
                children.Add(node);
        }

        var origin = _elementOrigins.GetValueOrDefault(frame.Name, ElementOrigin.ProjectSource);
        return new FrameTreeNode(frame, children, origin.Label(), project.Editor.IsLocked(frame.Name),
            string.Join(", ", project.Editor.GroupsFor(frame.Name)),
            Workspace == WorkspaceExperience.Design ? project.Editor.DisplayNameFor(frame) : frame.Name);
    }

    private IReadOnlyList<FrameTreeNode> BuildChildren(Project project, string parent,
        IReadOnlySet<string> visible, IReadOnlySet<string> allowed)
    {
        var children = new List<FrameTreeNode>();
        foreach (var child in FrameHierarchy.Children(project, parent).Where(frame => allowed.Contains(frame.Name)))
            if (BuildNode(project, child, visible) is { } node)
                children.Add(node);
        return children;
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
