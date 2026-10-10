using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core;
using FrameForge.Core.Examples;
using FrameForge.Core.Export;
using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Inspection;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Services;
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
    private enum DesignOrderMove { Front, Forward, Backward, Back }
    private static readonly string[] NativeHuntsStockFonts =
    [
        "GameFontNormal", "GameFontHighlight", "GameFontNormalSmall", "GameFontNormalLarge",
        "GameFontHighlightSmall", "GameFontHighlightLarge",
    ];
    private Project _project = ProjectFactory.Empty();
    private Project _presentationProject = ProjectFactory.Empty();
    private bool _syncingDesignUi;
    private DesignTextStyleMetadata? _copiedTextStyle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesignWorkspace))]
    [NotifyPropertyChangedFor(nameof(IsInspectWorkspace))]
    [NotifyPropertyChangedFor(nameof(ShowV1DesignTools))]
    [NotifyPropertyChangedFor(nameof(ShowV2DesignTools))]
    [NotifyPropertyChangedFor(nameof(ShowV1InspectTools))]
    private WorkspaceExperience _workspace = WorkspaceExperience.Design;

    public bool IsDesignWorkspace => Workspace == WorkspaceExperience.Design;
    public bool IsInspectWorkspace => Workspace == WorkspaceExperience.Inspect;
    public bool ShowV1DesignTools => IsV1Project && IsDesignWorkspace;
    public bool ShowV2DesignTools => IsV2Project && IsDesignWorkspace;
    public bool ShowV1InspectTools => IsV1Project && IsInspectWorkspace;
    public IReadOnlyList<WorkspaceOption> WorkspaceOptions { get; private set; } = [];

    [ObservableProperty] private string _designNameDraft = string.Empty;
    [ObservableProperty] private string _designTextDraft = string.Empty;
    [ObservableProperty] private StockTextStyleOption? _selectedTextStyle;
    [ObservableProperty] private string _textStyleSizeDraft = string.Empty;
    [ObservableProperty] private string _textStyleColorDraft = string.Empty;
    [ObservableProperty] private string _selectedTextOutline = "Style default";
    [ObservableProperty] private string _selectedTextShadow = "Style default";
    [ObservableProperty] private string _selectedTextAlignment = "Style default";
    [ObservableProperty] private string _textStyleValidation = string.Empty;
    [ObservableProperty] private string _statusBarMinDraft = string.Empty;
    [ObservableProperty] private string _statusBarMaxDraft = string.Empty;
    [ObservableProperty] private string _statusBarValueDraft = string.Empty;
    [ObservableProperty] private string _statusBarTextureDraft = string.Empty;
    [ObservableProperty] private string _statusBarColorDraft = string.Empty;
    [ObservableProperty] private string _statusBarValidation = string.Empty;
    [ObservableProperty] private bool _runtimeValueRequiredDraft;
    [ObservableProperty] private string _runtimeBindingDraft = string.Empty;
    [ObservableProperty] private string _runtimeBindingValidation = string.Empty;
    [ObservableProperty] private string? _selectedFunctionalValueSource;
    [ObservableProperty] private string _newObjectName = string.Empty;
    [ObservableProperty] private string _newImageAsset = string.Empty;
    [ObservableProperty] private string _stateNameDraft = string.Empty;
    [ObservableProperty] private DesignStateChoice? _activeDesignState;
    [ObservableProperty] private DesignStateChoice? _selectedAuthoredState;
    public ObservableCollection<DesignStateChoice> DesignStateOptions { get; } = [];
    public ObservableCollection<DesignStateChoice> AuthoredStateOptions { get; } = [];
    /// <summary>
    /// The STATE MEMBERSHIP line for the current selection: one shared membership, or Mixed when
    /// the selected objects disagree. An empty membership is All States, never "no state".
    /// </summary>
    public string SelectedStateMembership
    {
        get
        {
            IReadOnlyList<string>? membership = null;
            foreach (var name in _selectedNames)
            {
                if (!Project.Contains(name))
                    continue;
                var ids = Project.Editor.DesignObjectFor(name)?.StateIds ?? [];
                if (membership is null)
                    membership = ids;
                else if (!StateMembershipChooser.SameMembership(membership, ids))
                    return "Mixed";
            }
            return StateMembershipChooser.Describe(Project.Editor, membership ?? []);
        }
    }
    public bool HasConceptualStockFramework => Project.Editor.Groups.Any(group => group.Concept == "stock-framework");
    public EditorGroup? ConceptualStockFramework => Project.Editor.Groups.FirstOrDefault(group => group.Concept == "stock-framework");
    public bool IsStockFrameworkSelected => ConceptualStockFramework?.Members.Contains(SelectedName ?? string.Empty, StringComparer.Ordinal) == true;
    public string StockFrameworkAction => ConceptualStockFramework?.Locked == true
        ? "Unlock for Editing" : "Lock Blizzard Dungeon Finder Frame";
    public bool CanBrowseDesignAssets => ProjectPath.Length > 0;
    public bool IsDesignImageSelected => SelectedFrame?.Kind == FrameKind.TEXTURE
        && Project.Editor.DesignObjectFor(SelectedName) is not null;
    public bool IsDesignTextSelected => SelectedFrame?.Kind == FrameKind.FONTSTRING;
    public bool IsDesignStatusBarSelected => SelectedFrame?.Kind == FrameKind.STATUSBAR;
    public bool CanEditDesignStatusBar => IsDesignStatusBarSelected && !IsSelectionLocked;
    public bool IsRuntimeBindingEligible => IsDesignWorkspace
        && SelectedFrame?.Kind is FrameKind.FONTSTRING or FrameKind.STATUSBAR
        && Project.Editor.DesignObjectFor(SelectedName) is not null;
    public bool CanEditRuntimeBinding => IsRuntimeBindingEligible && !IsSelectionLocked;
    public bool HasFunctionalExport => IsV1Project && Project.FunctionalExport is not null;
    public bool CanExportFunctionalDesign => IsV1Project && Project.FunctionalExport is not null;
    public string FunctionalExportSummary => Project.FunctionalExport is { } profile
        ? $"Associated with {profile.Source.DisplayName} · host {profile.HostFrameName} · {profile.States.Count} optional state probes · {profile.Values.Count} optional value mirrors"
        : "No functional FrameXML associated.";
    public ObservableCollection<string> FunctionalValueSourceOptions { get; } = [];
    public bool CanMapFunctionalValue => HasFunctionalExport && RuntimeValueRequiredDraft && IsRuntimeBindingEligible;
    public string RuntimeBindingOperationSummary => SelectedFrame?.Kind == FrameKind.STATUSBAR
        ? "Adapter contract: number → SetValue"
        : "Adapter contract: string → SetText";
    private StatusBarVisual? SelectedStatusBar => SelectedFrame is { Kind: FrameKind.STATUSBAR } frame
        ? frame.Visual?.StatusBar
        : null;

    /// <summary>One-line summary of what the selected bar will draw, for the DESIGN panel.</summary>
    public string SelectedStatusBarFractionText => SelectedStatusBar is { DefaultFraction: { } fraction } bar
        ? $"Preview fills {Number(fraction * 100)}% of the width ({Number(bar.DefaultValue ?? 0)} of {Number(bar.MinValue ?? 0)}..{Number(bar.MaxValue ?? 0)})"
        : "Preview fills nothing: the range does not define a fraction (set Min and Max).";

    /// <summary>A colour swatch for the declared <c>&lt;BarColor&gt;</c>, transparent when unset.</summary>
    public IBrush SelectedStatusBarColorSwatch => SelectedStatusBar?.BarColor is { } color
        ? new SolidColorBrush(Color.FromArgb(Channel(color.A), Channel(color.R), Channel(color.G), Channel(color.B)))
        : Brushes.Transparent;

    /// <summary>Physical path of the declared bar texture, when it resolves; otherwise a note.</summary>
    public string SelectedStatusBarTexturePhysical => SelectedStatusBar?.BarTexture is { Length: > 0 } reference
        && SelectedName is { } name
        ? Assets.Resolve(reference) is { PhysicalPath: { } path }
            ? path
            : "Unresolved texture reference"
        : "No bar texture declared — the fill uses BarColor.";
    public bool CanEditTextStyle => IsDesignTextSelected && !IsSelectionLocked;
    public bool CanPasteTextStyle => CanEditTextStyle && _copiedTextStyle is not null;
    public IReadOnlyList<StockTextStyleOption> TextStyleOptions { get; private set; } = [];
    public IReadOnlyList<string> TextOutlineOptions { get; } = ["Style default", "None", "Normal", "Thick"];
    public IReadOnlyList<string> TextShadowOptions { get; } = ["Style default", "On", "Off"];
    public IReadOnlyList<string> TextAlignmentOptions { get; } = ["Style default", "Left", "Center", "Right"];
    private EffectiveDesignTextStyle? SelectedEffectiveTextStyle => SelectedFrame is { Kind: FrameKind.FONTSTRING } frame
        ? DesignTextStyleResolver.Resolve(frame, Project.Editor.DesignObjectFor(frame.Name), _stockTemplates)
        : null;
    public string SelectedTextBaseStyle => SelectedEffectiveTextStyle?.BaseStyle ?? "Not established by source";
    public string SelectedTextFont => SelectedEffectiveTextStyle?.Style is { } style
        ? $"{style.FontFamilyName ?? "host fallback"} · {style.FontReference ?? "unknown resource"}"
        : "Unresolved";
    public string SelectedTextEffectiveSize => SelectedEffectiveTextStyle?.Style is { } style ? $"{Number(style.Size)} px" : "Unresolved";
    public string SelectedTextEffectiveColor => SelectedEffectiveTextStyle?.Style is { } style ? ColorHex(style.Color) : "Unresolved";
    public string SelectedTextEffectiveOutline => SelectedEffectiveTextStyle?.Style?.Outline ?? "None";
    public string SelectedTextEffectiveShadow => SelectedEffectiveTextStyle?.Style?.ShadowColor is { } shadow
        ? $"{ColorHex(shadow)} at ({Number(SelectedEffectiveTextStyle.Style.ShadowX)}, {Number(SelectedEffectiveTextStyle.Style.ShadowY)})"
        : "None";
    public string SelectedTextEffectiveAlignment => SelectedEffectiveTextStyle?.Style?.JustifyH ?? "Unresolved";
    public string SelectedTextOverrides => SelectedEffectiveTextStyle is { Overrides.Count: > 0 } style
        ? string.Join(", ", style.Overrides) : "None";
    public IBrush SelectedTextColorSwatch => SelectedEffectiveTextStyle?.Style is { } style
        ? new SolidColorBrush(Color.FromArgb(Channel(style.Color.A), Channel(style.Color.R), Channel(style.Color.G), Channel(style.Color.B)))
        : Brushes.Transparent;
    public bool CanChangeDesignOrder => SelectedName is { } name
        && !Project.Editor.IsLocked(name)
        && Project.Editor.DesignObjectFor(name) is not null
        && ConceptualStockFramework?.Members.Contains(name, StringComparer.Ordinal) != true;
    public string SelectedSourceText => SelectedFrame?.Visual?.Text?.Text
        ?? "(runtime/localized value; no literal source text)";
    public string SelectedDesignTextOverride => Project.Editor.DesignObjectFor(SelectedName)?.TextOverride
        ?? "(none — source/imported text is active)";
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
        TextureFileFormat.Png => "PNG",
        TextureFileFormat.Tga => "TGA",
        _ => "Unknown",
    };
    public string SelectedDesignAssetOwnership => SelectedDesignAsset?.SourceKind == AssetSourceKind.ProjectRelative
        ? "Project-owned"
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

    /// <summary>
    /// The selection, in click order, with the primary object last.
    /// </summary>
    /// <remarks>
    /// <see cref="_selectedName"/> remains the primary selection because everything else in the app
    /// - the inspector, the anchor chrome, the composition panel - is defined in terms of "the
    /// selected object". This list is what sits underneath it, so a multi-selection is always one
    /// ordered set with a defined primary rather than a second, competing selection.
    /// <para>
    /// Ordering is deliberate: the last surviving entry is the primary, which makes Ctrl+click
    /// deterministic. Clicking an object twice in a row, or clicking two objects at the same spot,
    /// therefore always ends up with the object the user most recently expressed intent about as
    /// the one the inspector describes.
    /// </para>
    /// </remarks>
    private readonly List<string> _selectedNames = [];

    /// <summary>
    /// Depth counter for tree selection re-synchronization. While non-zero, <c>SelectedTreeNode</c>
    /// writes are this class pushing the authoritative projection into the tree, not the user, so
    /// callbacks that observe the property must ignore them. Incremented while the tree is rebuilt
    /// AND while the programmatic selection is published, because rebuilding an
    /// <c>ObservableCollection</c> makes the TreeView clear its selected item and write the cleared
    /// value back through the TwoWay binding - honouring that write would collapse the selection
    /// every time the tree tree is rebuilt or refreshed.
    /// </summary>
    private int _selectionSyncDepth;

    /// <summary>
    /// Expanded ordinary source nodes, keyed by stable imported/project identity rather than by
    /// disposable TreeView rows. Hidden filtered nodes stay in the set until the user collapses
    /// them after they become visible again.
    /// </summary>
    private readonly HashSet<string> _expandedTreeNames = new(StringComparer.Ordinal);
    private bool _discardTreeExpansionOnNextRebuild;

    /// <summary>Whether the last drag moved the whole selection or only one frame.</summary>
    private bool _lastDragMovedSelection;

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
    public bool IsTreeFiltering => IsV2Project ? _v2TreeFiltering : _treeProjection?.Filtering ?? false;

    /// <summary>How many nodes the tree is showing, and how many exist.</summary>
    public string TreeFilterSummary => IsV2Project
        ? _v2TreeFiltering ? $"{_v2TreeVisibleCount} of {V2Document?.Nodes.Count ?? 0} shown" : $"{V2Document?.Nodes.Count ?? 0} controls"
        : _treeProjection is { Filtering: true } projection
            ? $"{projection.Visible.Count} of {Project.Frames.Count} shown"
            : $"{Project.Frames.Count} frames";

    private TreeProjection? _treeProjection;
    private readonly AssetSettingsStore _assetSettings;
    private readonly IWoWClientAssetProvider _wowAssets;
    private readonly IStockTemplateResolver _stockTemplates;
    private readonly IBuild12340TemplateRegistryLoader _v2TemplateLoader;
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

    /// <summary>
    /// Legacy machine-local roots retained for advanced/diagnostic use and back-compat. These are
    /// NOT a normal DESIGN prerequisite: project-owned artwork resolves beside the project file
    /// and logical WoW references resolve through the configured client and its managed cache.
    /// </summary>
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
    public string SelectedSourceAnchor => Editor.SourceAnchorSummary;
    public string SelectedGroupMembership => SelectedFrame is null
        ? string.Empty
        : string.Join(", ", Project.Editor.GroupsFor(SelectedFrame.Name).DefaultIfEmpty("(none)"));
    public string SelectedPreviewProvenance => SelectedFrame is not null && _activePreviewOverrides.Find(SelectedFrame.Name) is not null
        ? $"Effective values differ under {ActivePreviewOverrides.State.Label}; source data is unchanged."
        : "No design-time override on this element.";
    public bool IsSelectionLocked => Project.Editor.IsLocked(SelectedName);

    /// <summary>Concise locked/reference summary for the DESIGN panel's locked banner.</summary>
    public string DesignLockedSummary
    {
        get
        {
            var frame = SelectedFrame;
            if (frame is null)
                return "Locked. Selectable for inspection only.";
            var display = Project.Editor.DisplayNameFor(frame);
            return IsStockFrameworkSelected
                ? $"🔒 {display} — locked Blizzard stock / reference element. Selectable for inspection; not editable."
                : $"🔒 {display} ({SelectedElementKind}) — locked. Selectable for inspection; not editable.";
        }
    }
    public bool SelectedElementLocked
    {
        get => Project.Editor.IsLocked(SelectedName);
        set
        {
            if (Workspace == WorkspaceExperience.Design && IsStockFrameworkSelected)
                SetConceptualStockLocked(value);
            else
                SetElementLocked(SelectedName, value);
        }
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
            if (value is null || value.Id == _selectedPreviewState?.Id)
                return;
            _selectedPreviewState = value;
            if (Project.Editor.PreviewStateId != value.Id)
            {
                Project = Project with { Editor = Project.Editor with { PreviewStateId = value.Id } };
                IsDirty = true;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewStateExplanation));
            OnPropertyChanged(nameof(PreviewSimulationSummary));
            RelaidOut(Project, SelectedName, $"Design-time preview state: {value.Label}. Source XML is unchanged.");
        }
    }

    public string PreviewStateExplanation => SelectedPreviewState?.Description
        ?? PreviewStateRegistry.XmlDefaults.Description;

    public string PreviewSimulationSummary => SelectedPreviewState is { IsXmlDefaults: false } state
        ? $"SIMULATED PREVIEW: {state.Label}. {state.Description} Runtime XML visibility remains unchanged."
        : "Preview uses literal XML visibility. Runtime Lua is recorded but not executed.";

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
        OnPropertyChanged(nameof(IsV2DesignMode));
        OnPropertyChanged(nameof(IsV2PreviewMode));
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
        if (_syncingDesignUi || SelectedFrame is null || string.IsNullOrWhiteSpace(value) || IsSelectionLocked)
            return;
        var values = Project.Editor.DesignObjects.ToList();
        var index = values.FindIndex(item => item.FrameName == SelectedFrame.Name);
        var item = index >= 0 ? values[index] : new DesignObjectMetadata { FrameName = SelectedFrame.Name };
        item = item with { DisplayName = value.Trim() };
        if (index >= 0) values[index] = item; else values.Add(item);
        Project = Project with { Editor = Project.Editor with { DesignObjects = values } };
        IsDirty = true;
        NotifySelectionInspection();
    }

    public void ChangeSelectedDesignText(string value)
    {
        if (_syncingDesignUi || SelectedFrame?.Kind != FrameKind.FONTSTRING)
            return;
        _syncingDesignUi = true;
        DesignTextDraft = value;
        _syncingDesignUi = false;
        var values = Project.Editor.DesignObjects.ToList();
        var index = values.FindIndex(item => item.FrameName == SelectedFrame.Name);
        var item = index >= 0 ? values[index] : new DesignObjectMetadata { FrameName = SelectedFrame.Name };
        item = item with { TextOverride = value };
        if (index >= 0) values[index] = item; else values.Add(item);
        Project = Project with { Editor = Project.Editor with { DesignObjects = values } };
        IsDirty = true;
        RefreshPresentation(Project);
        NotifySelectionInspection();
        Status = $"Changed visible text for {Project.Editor.DisplayNameFor(SelectedFrame)}. Source text is unchanged.";
    }

    partial void OnSelectedTextStyleChanged(StockTextStyleOption? value)
    {
        if (_syncingDesignUi || value is null || !CanEditTextStyle)
            return;
        SetSelectedTextStyle(new DesignTextStyleMetadata { BaseStyle = value.Name },
            $"Applied authentic WoW style {value.Name}.");
    }

    partial void OnSelectedTextOutlineChanged(string value)
    {
        if (_syncingDesignUi || !CanEditTextStyle) return;
        UpdateTextStyle(style => style with { Outline = value switch
        {
            "None" => "NONE", "Normal" => "NORMAL", "Thick" => "THICK", _ => null,
        } }, "Updated text outline.");
    }

    partial void OnSelectedTextShadowChanged(string value)
    {
        if (_syncingDesignUi || !CanEditTextStyle) return;
        UpdateTextStyle(style => style with { Shadow = value switch { "On" => true, "Off" => false, _ => null } },
            "Updated text shadow.");
    }

    partial void OnSelectedTextAlignmentChanged(string value)
    {
        if (_syncingDesignUi || !CanEditTextStyle) return;
        UpdateTextStyle(style => style with { JustifyH = value == "Style default" ? null : value.ToUpperInvariant() },
            "Updated horizontal alignment.");
    }

    public bool CommitTextStyleSize()
    {
        if (!CanEditTextStyle || !double.TryParse(TextStyleSizeDraft, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var size) || !double.IsFinite(size) || size <= 0)
        {
            TextStyleValidation = "Size must be a positive number.";
            return false;
        }
        TextStyleValidation = string.Empty;
        UpdateTextStyle(style => style with { Size = size }, "Updated text size.");
        return true;
    }

    public bool CommitTextStyleColor()
    {
        if (!CanEditTextStyle || !TryParseColor(TextStyleColorDraft, out var color))
        {
            TextStyleValidation = "Color must be #RRGGBB or #AARRGGBB.";
            return false;
        }
        TextStyleValidation = string.Empty;
        UpdateTextStyle(style => style with { Color = color }, "Updated text color.");
        return true;
    }

    public void CancelTextStyleField(string field) => RefreshTextStyleUi(Project, SelectedName);

    /// <summary>
    /// Commits one buffered StatusBar field; Enter or LostFocus is the edit boundary, never a
    /// keystroke, so a half-typed value cannot corrupt the model.
    /// </summary>
    public bool CommitStatusBarField(string field)
    {
        if (!IsDesignStatusBarSelected || SelectedName is not { } name || SelectedFrame is not { } frame)
            return false;
        if (!CanEditDesignStatusBar)
        {
            StatusBarValidation = $"{name} is locked. Unlock it before changing its status bar.";
            return false;
        }

        var bar = frame.Visual?.StatusBar ?? new StatusBarVisual();
        StatusBarValidation = string.Empty;
        return field switch
        {
            "statusBarMin" => CommitStatusBarNumber(frame, bar, "Min", StatusBarMinDraft,
                (b, v) => b with { MinValue = v }),
            "statusBarMax" => CommitStatusBarNumber(frame, bar, "Max", StatusBarMaxDraft,
                (b, v) => b with { MaxValue = v }),
            "statusBarValue" => CommitStatusBarNumber(frame, bar, "Preview value", StatusBarValueDraft,
                (b, v) => b with { DefaultValue = v }),
            "statusBarColor" => CommitStatusBarColor(frame, bar),
            "statusBarTexture" => ReplaceStatusBar(frame,
                bar with { BarTexture = string.IsNullOrWhiteSpace(StatusBarTextureDraft) ? null : StatusBarTextureDraft.Trim() }),
            _ => false,
        };
    }

    public void CancelStatusBarField(string field)
    {
        if (SelectedFrame is not { Kind: FrameKind.STATUSBAR } frame)
            return;
        RefreshStatusBarUi(Project, SelectedName);
        StatusBarValidation = string.Empty;
    }

    public void SetRuntimeValueRequired(bool enabled)
    {
        if (!CanEditRuntimeBinding || SelectedName is not { } name)
            return;
        var values = Project.Editor.DesignObjects.ToList();
        var index = values.FindIndex(item => item.FrameName == name);
        if (index < 0)
            return;
        var current = values[index];
        if (current.RuntimeValueRequired == enabled && (enabled || current.RuntimeBinding is null))
            return;
        values[index] = current with
        {
            RuntimeValueRequired = enabled,
            RuntimeBinding = enabled ? current.RuntimeBinding : null,
        };
        UpdateEditor(Project.Editor with { DesignObjects = values },
            enabled ? $"Enabled runtime value binding for {Project.Editor.DisplayNameFor(SelectedFrame!)}."
                : $"Removed runtime value binding from {Project.Editor.DisplayNameFor(SelectedFrame!)}.");
    }

    public bool CommitRuntimeBinding()
    {
        if (!CanEditRuntimeBinding || SelectedName is not { } name)
            return false;
        if (!RuntimeValueRequiredDraft)
        {
            RuntimeBindingValidation = "Enable Runtime value before setting a binding key.";
            return false;
        }
        var key = RuntimeBindingDraft.Trim();
        if (RuntimeBindingKeys.LooksLikeEditorIdentity(key)
            || string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
        {
            RuntimeBindingValidation = "Use an application-semantic key, not an editor/source object ID.";
            return false;
        }
        if (!RuntimeBindingKeys.IsValid(key))
        {
            RuntimeBindingValidation = $"Use {RuntimeBindingKeys.GrammarDescription}.";
            return false;
        }
        var values = Project.Editor.DesignObjects.ToList();
        var index = values.FindIndex(item => item.FrameName == name);
        if (index < 0)
            return false;
        if (values[index].RuntimeBinding == key)
        {
            RuntimeBindingValidation = string.Empty;
            return true;
        }
        values[index] = values[index] with { RuntimeBinding = key };
        RuntimeBindingValidation = string.Empty;
        UpdateEditor(Project.Editor with { DesignObjects = values }, $"Bound {Project.Editor.DisplayNameFor(SelectedFrame!)} to {key}.");
        return true;
    }

    public void CancelRuntimeBinding() => RefreshRuntimeBindingUi(Project, SelectedName);

    partial void OnSelectedFunctionalValueSourceChanged(string? value)
    {
        if (_syncingDesignUi || Project.FunctionalExport is not { } profile
            || SelectedName is not { } name || !RuntimeValueRequiredDraft)
            return;
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
        var values = profile.Values.Where(item => item.DesignFrameName != name).ToList();
        if (normalized is not null)
            values.Add(new FunctionalValueBinding { DesignFrameName = name, SourceFrameName = normalized });
        Project = Project with { FunctionalExport = profile with { Values = values } };
        IsDirty = true;
        Status = normalized is null
            ? $"Removed the functional value source for {Project.Editor.DisplayNameFor(SelectedFrame!)}."
            : $"{Project.Editor.DisplayNameFor(SelectedFrame!)} will optionally mirror {normalized} in functional export.";
        NotifyFunctionalExportState();
    }

    public bool SetSelectedStatusBarTextureFromFile(string path, bool importExternal = false)
    {
        if (!IsDesignStatusBarSelected || SelectedName is not { } name || SelectedFrame is not { } frame)
            return false;
        if (IsSelectionLocked)
        {
            Status = $"{name} is locked. Unlock it before changing its bar texture.";
            return false;
        }
        if (!TryMakeProjectAssetReference(path, importExternal, out var reference, out var error))
        {
            Status = error;
            return false;
        }
        var bar = frame.Visual?.StatusBar ?? new StatusBarVisual();
        if (ReplaceStatusBar(frame, bar with { BarTexture = reference }))
        {
            ConfigureAssets();
            Status = $"Set the status bar fill texture to {reference}.";
        }
        return true;
    }

    public bool SetSelectedStatusBarTextureFromWow(string reference)
    {
        if (!IsDesignStatusBarSelected || SelectedName is not { } name || SelectedFrame is not { } frame)
            return false;
        if (IsSelectionLocked)
        {
            Status = $"{name} is locked. Unlock it before changing its bar texture.";
            return false;
        }
        var normalized = (reference ?? string.Empty).Trim();
        if (!WoWClientAssetProvider.TryNormalizeInterfacePath(normalized, out _, out var error))
        {
            Status = error;
            return false;
        }
        normalized = normalized.Replace('\\', '/');
        var bar = frame.Visual?.StatusBar ?? new StatusBarVisual();
        if (ReplaceStatusBar(frame, bar with { BarTexture = normalized }))
        {
            ConfigureAssets();
            Status = $"Set the status bar fill texture to {normalized} (WoW client asset).";
        }
        return true;
    }

    /// <summary>
    /// The logical stock references offered by the WoW Client… picker: the curated built-in list,
    /// every <c>Interface/...</c> reference this project already uses, and everything queued in the
    /// extracted WoW cache. All are portable <c>Interface/...</c> paths - never machine paths.
    /// </summary>
    public IReadOnlyList<StockTextureEntry> StatusBarStockTextureChoices()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<StockTextureEntry>();
        foreach (var entry in StockTextureCatalog.Curated)
        {
            if (seen.Add(entry.InterfacePath))
                entries.Add(entry);
        }
        foreach (var reference in EnumerateAssetReferences())
        {
            if (!IsWowClientReference(reference) || !seen.Add(reference))
                continue;
            entries.Add(new StockTextureEntry(reference.Replace('\\', '/'), "Used by this project",
                "A logical reference already used by a frame in this project."));
        }
        EnumerateCachedStockTextures(seen, entries);
        return entries;
    }

    private void EnumerateCachedStockTextures(HashSet<string> seen, List<StockTextureEntry> entries)
    {
        var interfaceRoot = Path.Combine(_wowAssets.CacheRoot, "Interface");
        if (!Directory.Exists(interfaceRoot))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(interfaceRoot, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".blp", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".tga", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    continue;
                var logical = Path.GetRelativePath(_wowAssets.CacheRoot, file);
                if (IsWowClientReference(logical) && seen.Add(logical))
                {
                    entries.Add(new StockTextureEntry(logical.Replace('\\', '/'), "Already extracted",
                        "Extracted from your configured WoW client; resolves immediately."));
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsWowClientReference(string reference) =>
        WoWClientAssetProvider.TryNormalizeInterfacePath(reference, out _, out _);

    private bool CommitStatusBarNumber(
        FrameDef frame, StatusBarVisual bar, string label, string draft,
        Func<StatusBarVisual, double, StatusBarVisual> update)
    {
        if (!double.TryParse(draft, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            StatusBarValidation = $"{label} must be a number.";
            return false;
        }
        if (label != "Preview value" && value < 0)
        {
            StatusBarValidation = $"{label} must not be negative.";
            return false;
        }
        return ReplaceStatusBar(frame, update(bar, value));
    }

    private bool CommitStatusBarColor(FrameDef frame, StatusBarVisual bar)
    {
        if (!TryParseColor(StatusBarColorDraft, out var color))
        {
            StatusBarValidation = "Bar color must be #RRGGBB or #AARRGGBB.";
            return false;
        }
        return ReplaceStatusBar(frame, bar with { BarColor = color });
    }

    private bool ReplaceStatusBar(FrameDef frame, StatusBarVisual bar)
    {
        var updated = frame with
        {
            Visual = (frame.Visual ?? new FrameVisual()) with { StatusBar = bar },
        };
        if (updated == frame)
            return true;
        ReplaceFrame(frame.Name, updated);
        StatusBarValidation = string.Empty;
        return true;
    }

    public void ResetTextStyleOverrides()
    {
        if (!CanEditTextStyle) return;
        var baseStyle = Project.Editor.DesignObjectFor(SelectedName)?.TextStyle?.BaseStyle
                        ?? SelectedFrame?.Visual?.Text?.FontTemplate;
        SetSelectedTextStyle(new DesignTextStyleMetadata { BaseStyle = baseStyle }, "Reset text overrides to the WoW style.");
    }

    public void CopySelectedTextStyle()
    {
        if (SelectedFrame is not { Kind: FrameKind.FONTSTRING } frame || SelectedEffectiveTextStyle?.Style is null)
        {
            Status = "The selected text has no determinable supported WoW style.";
            return;
        }
        var existing = Project.Editor.DesignObjectFor(frame.Name)?.TextStyle;
        _copiedTextStyle = existing ?? new DesignTextStyleMetadata
        {
            BaseStyle = frame.Visual?.Text?.FontTemplate,
            JustifyH = frame.Visual?.Text?.JustifyHorizontal,
        };
        OnPropertyChanged(nameof(CanPasteTextStyle));
        Status = $"Copied text style from {Project.Editor.DisplayNameFor(frame)}; text and geometry were not copied.";
    }

    public void PasteSelectedTextStyle()
    {
        if (!CanPasteTextStyle || _copiedTextStyle is null) return;
        SetSelectedTextStyle(_copiedTextStyle with { }, "Pasted text style; text, name, geometry, and state membership are unchanged.");
    }

    private void UpdateTextStyle(Func<DesignTextStyleMetadata, DesignTextStyleMetadata> update, string status)
    {
        var current = Project.Editor.DesignObjectFor(SelectedName)?.TextStyle
                      ?? new DesignTextStyleMetadata { BaseStyle = SelectedFrame?.Visual?.Text?.FontTemplate };
        SetSelectedTextStyle(update(current), status);
    }

    private void SetSelectedTextStyle(DesignTextStyleMetadata style, string status)
    {
        if (SelectedName is not { } name) return;
        var values = Project.Editor.DesignObjects.ToList();
        var index = values.FindIndex(item => item.FrameName == name);
        var item = index >= 0 ? values[index] : new DesignObjectMetadata { FrameName = name };
        item = item with { TextStyle = style };
        if (index >= 0) values[index] = item; else values.Add(item);
        UpdateEditor(Project.Editor with { DesignObjects = values }, status);
    }

    partial void OnActiveDesignStateChanged(DesignStateChoice? value)
    {
        if (_syncingDesignUi || value is null)
            return;
        Project = Project with { Editor = Project.Editor with { ActiveDesignStateId = value.Id } };
        IsDirty = true;
        RelaidOut(Project, SelectedName, $"Design state: {value.Name}. Lua was not executed.");
        PruneInvisibleSelection();
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
        if (IsV2Project) RebuildV2Tree(); else RebuildTree(Project);

        foreach (var option in TreeFilterOptions)
            option.Refresh();
    }

    public MainWindowViewModel() : this(null, null, null, null)
    {
    }

    public MainWindowViewModel(string? settingsPath, IWoWClientAssetProvider? wowAssets = null,
        IStockTemplateResolver? stockTemplates = null, IPreviewStateRegistry? previewStates = null,
        IBuild12340TemplateRegistryLoader? v2TemplateLoader = null)
    {
        _assetSettings = new AssetSettingsStore(settingsPath
            ?? Environment.GetEnvironmentVariable("FRAMEFORGE_SETTINGS_PATH"));
        _wowAssets = wowAssets ?? new WoWClientAssetProvider();
        _stockTemplates = stockTemplates ?? new StockTemplateResolver(_wowAssets);
        _v2TemplateLoader = v2TemplateLoader ?? new Build12340TemplateRegistryLoader(_wowAssets);
        TextStyleOptions = WowTextStyleCatalog.Available(_stockTemplates);
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
        IsV2Project
            ? $"Schema v2 · {V2Document!.Nodes.Count} controls · explicit composition root"
            : Project.Frames.Count == 0
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

    /// <summary>Diagnostics from the most recent functional layout export attempt.</summary>
    public ObservableCollection<ExportDiagnostic> LayoutExportDiagnostics { get; } = [];

    public bool HasLayoutExportDiagnostics => LayoutExportDiagnostics.Count > 0;

    /// <summary>True only for an imported FrameXML project with a persisted source identity.</summary>
    public bool CanExportLayoutChanges => IsV1Project && FunctionalLayoutExporter.IsEligible(Project);

    private string? _functionalSourcePath;

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
    public bool CanEditSelection => SelectedFrame is { } frame && !IsSelectionLocked && CanEditImportedGeometry(frame);

    /// <summary>Imported functional frames keep identity and anchor relationships read-only.</summary>
    public bool CanEditLayoutStructure => CanEditSelection && Project.Source?.IsReadOnlyXml != true;

    public string SelectedGeometryEditDiagnostic => SelectedFrame is { } frame && !CanEditImportedGeometry(frame)
        ? ImportedGeometryDiagnostic(frame)
        : string.Empty;

    /// <summary>The whole selection in click order; the last entry is the primary selection.</summary>
    public IReadOnlyList<string> SelectedNames => _selectedNames;

    /// <summary>How many objects are selected.</summary>
    public int SelectionCount => _selectedNames.Count;

    /// <summary>True when more than one object is selected.</summary>
    public bool IsMultiSelection => _selectedNames.Count > 1;

    /// <summary>
    /// True when the single-object editors should be shown.
    /// </summary>
    /// <remarks>
    /// The name, size, anchor and appearance fields all describe ONE object. Leaving them
    /// populated - and editable - during a multi-selection would mean editing whichever frame
    /// happens to be primary while the user looks at a selection of four, which is how a
    /// background panel gets renamed by accident.
    /// </remarks>
    public bool ShowsSingleObjectEditors => _selectedNames.Count == 1;

    /// <summary>
    /// True when an alignment command would change something for this selection.
    /// </summary>
    /// <remarks>
    /// Delegated to <see cref="SelectionArrange.CanRun"/> so the button and the command can never
    /// disagree. One locked object plus one editable object is enough, because the locked object is
    /// alignment reference geometry: it is what the editable one gets lined up with. The old rule
    /// demanded two <em>movable</em> objects, which left the button lit and then did nothing.
    /// </remarks>
    public bool CanAlignSelection =>
        IsV2Project ? CanAlignV2 : SelectionArrange.CanRun(Project, Layout, _selectedNames, SelectionArrangeCommand.AlignLeft);

    /// <summary>
    /// True when a distribution command has enough selected objects to be offered.
    /// </summary>
    /// <remarks>
    /// Deliberately still a plain selection count. Distribution is not part of this correction, and
    /// <see cref="SelectionArrange.Arrange"/> still refuses mixed locked selections itself with an
    /// explanation in the status bar, so narrowing the button here would change distribution
    /// behaviour rather than fix anything.
    /// </remarks>
    public bool CanDistributeSelection =>
        IsV2Project ? CanDistributeV2 : _selectedNames.Count >= SelectionArrange.RequiredCount(SelectionArrangeCommand.DistributeHorizontal)
        && SelectionArrange.CanRun(Project, Layout, _selectedNames, SelectionArrangeCommand.DistributeHorizontal);

    /// <summary>One line describing the whole selection for the multi-selection panel.</summary>
    public string MultiSelectionSummary => IsV2Project
        ? V2Selection.Count > 1 && SelectedV2Node is { } selected
            ? $"{V2Selection.Count} controls selected. Primary: {selected.DisplayLabel}." : string.Empty
        : _selectedNames.Count > 1 && Project.Find(_selectedNames[^1]) is { } primary
            ? $"{_selectedNames.Count} objects selected. Primary: {Project.Editor.DisplayNameFor(primary)}."
            : string.Empty;

    /// <summary>Replaces the whole project. Used by New / Open / Load Example.</summary>
    /// <remarks>
    /// Any previous import banner is cleared: opening a project - even one that was itself
    /// saved from an import - is not the same event as importing, and leaving a stale warning
    /// list on screen would describe a file the user is no longer looking at.
    /// </remarks>
    public void Load(Project project, string? path, string status)
    {
        if (!_loadingV2)
            SetV2Document(null);
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
        LayoutExportDiagnostics.Clear();
        OnPropertyChanged(nameof(HasLayoutExportDiagnostics));
        OnPropertyChanged(nameof(CanExportLayoutChanges));
        _assetSourcePath = ResolveSourcePath(project, path);
        _functionalSourcePath = ResolveFunctionalSourcePath(project, path);
        RefreshFunctionalSourceOptions();
        NotifyFunctionalExportState();
        ConfigureAssets();
        RefreshPreviewStateOptions(project);
        RefreshGroups(project);
        Workspace = project.Editor.Workspace == "inspect" ? WorkspaceExperience.Inspect : WorkspaceExperience.Design;
        foreach (var option in WorkspaceOptions) option.Refresh();
        RefreshDesignStates(project);

        // Expansion belongs to the document currently on screen. Do not let matching generated
        // identities in a newly opened file inherit navigation state from the previous document.
        _expandedTreeNames.Clear();
        _discardTreeExpansionOnNextRebuild = true;

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
            var text = File.ReadAllText(path);
            if (UiDocumentCodec.HasV2FormatMarker(text))
            {
                var v2Result = UiDocumentCodec.Parse(text);
                if (!v2Result.Ok)
                {
                    Status = $"Could not open {FileName(path)}: {v2Result.ErrorText}";
                    return;
                }
                var openDiagnostics = UiDocumentValidator.Validate(v2Result.Document!);
                if (openDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error &&
                                                item.Code != "FFV2-TEMPLATE-003"))
                {
                    Status = $"Could not open {FileName(path)}: " +
                             string.Join(" ", openDiagnostics.Where(item => item.Code != "FFV2-TEMPLATE-003")
                                 .Take(3).Select(item => $"{item.Code}: {item.Message}"));
                    return;
                }
                LoadV2(v2Result.Document!, path, $"Opened schema-v2 project {FileName(path)}.");
                return;
            }

            var result = ProjectCodec.Parse(text);
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

        // The reference path identifies the read-only baseline for layout export. Nothing writes
        // back to it; export always writes a separately selected destination.
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

        // A validated local client is authoritative for Blizzard definitions and artwork. Resolve
        // the focused dependency set on import so Preview does not require a second hidden step.
        if (_wowClient.IsValid && CanResolveStockAssets)
            ResolveMissingStockAssets();
    }

    /// <summary>Writes the project to disk. Returns false when the write was refused or failed.</summary>
    public bool SaveToFile(string path)
    {
        if (IsV2Project)
            return SaveV2ToFile(path);
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
            if (Project.FunctionalExport?.Source.ReferencePath is { Length: > 0 } functionalReference)
            {
                var source = Project.FunctionalExport.Source;
                var absolute = _functionalSourcePath ?? ResolveFunctionalSourcePath(Project, ProjectPath);
                var portableReference = absolute is { Length: > 0 }
                    ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, absolute)
                    : functionalReference;
                projectToSave = projectToSave with
                {
                    FunctionalExport = Project.FunctionalExport with
                    {
                        Source = source with { ReferencePath = portableReference.Replace('\\', '/') },
                    },
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

    public bool AssociateFunctionalSource(string path)
    {
        LayoutExportDiagnostics.Clear();
        try
        {
            var xml = File.ReadAllText(path);
            var result = FunctionalExportAssociator.Associate(Project, xml, path,
                string.IsNullOrWhiteSpace(ProjectPath) ? null : ProjectPath);
            foreach (var diagnostic in result.Diagnostics)
                LayoutExportDiagnostics.Add(diagnostic);
            OnPropertyChanged(nameof(HasLayoutExportDiagnostics));
            if (!result.Success || result.Profile is null)
            {
                Status = "Functional association blocked: " + DescribeExportDiagnostics(result.Diagnostics);
                return false;
            }
            Project = Project with { FunctionalExport = result.Profile };
            _functionalSourcePath = Path.GetFullPath(path);
            IsDirty = true;
            RefreshFunctionalSourceOptions();
            RefreshRuntimeBindingUi(Project, SelectedName);
            NotifyFunctionalExportState();
            Status = $"Associated {Path.GetFileName(path)} with host {result.Profile.HostFrameName}. Save the project to persist this mapping.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Could not associate functional FrameXML: {ex.Message}";
            return false;
        }
    }

    public bool ExportFunctionalDesign(string destination)
    {
        LayoutExportDiagnostics.Clear();
        OnPropertyChanged(nameof(HasLayoutExportDiagnostics));
        var sourcePath = _functionalSourcePath ?? ResolveFunctionalSourcePath(Project, ProjectPath);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return FailLayoutExport("FUNCTIONAL_SOURCE_MISSING",
                "The associated functional FrameXML cannot be found. Associate it again before exporting.");
        try
        {
            var result = FunctionalDesignPackageExporter.Export(Project,
                string.IsNullOrWhiteSpace(ProjectPath) ? null : ProjectPath,
                File.ReadAllText(sourcePath), destination);
            foreach (var diagnostic in result.Preparation.Diagnostics)
                LayoutExportDiagnostics.Add(diagnostic);
            OnPropertyChanged(nameof(HasLayoutExportDiagnostics));
            if (!result.Success)
            {
                Status = "Functional export blocked: " + DescribeExportDiagnostics(result.Preparation.Diagnostics);
                return false;
            }
            Status = $"Exported functional FrameXML with {result.Preparation.DesignObjectCount} design objects to {destination}. Original source and Lua unchanged.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return FailLayoutExport("FUNCTIONAL_EXPORT_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// Exports an updated copy of the authoritative FrameXML. The source is read, verified, and
    /// patched in memory; it is never opened for writing.
    /// </summary>
    public bool ExportLayoutChanges(string destinationPath)
    {
        LayoutExportDiagnostics.Clear();
        OnPropertyChanged(nameof(HasLayoutExportDiagnostics));

        if (!CanExportLayoutChanges)
            return FailLayoutExport("SOURCE_INELIGIBLE",
                "Export Layout Changes is available only for an imported FrameXML source with preserved identity metadata.");

        var sourcePath = ResolveSourcePath(Project, ProjectPath);
        if (string.IsNullOrWhiteSpace(sourcePath))
            return FailLayoutExport("SOURCE_PATH_UNRESOLVED",
                "The source path cannot be resolved. Reopen the XML or save the project beside its source.");

        string sourceFullPath;
        string destinationFullPath;
        try
        {
            sourceFullPath = Path.GetFullPath(sourcePath);
            destinationFullPath = Path.GetFullPath(destinationPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return FailLayoutExport("INVALID_DESTINATION", $"The export destination is invalid: {ex.Message}");
        }

        if (!Path.GetExtension(destinationFullPath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return FailLayoutExport("INVALID_DESTINATION", "Layout changes must be exported to an .xml file.");
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
            return FailLayoutExport("SOURCE_OVERWRITE_REFUSED",
                "The destination is the imported source file. Choose a different path; the source is never overwritten.");
        if (!File.Exists(sourceFullPath))
            return FailLayoutExport("SOURCE_MISSING", $"The source XML no longer exists: {sourceFullPath}");

        try
        {
            var sourceXml = File.ReadAllText(sourceFullPath);
            var result = FunctionalLayoutExporter.Prepare(Project, sourceXml);
            foreach (var diagnostic in result.Diagnostics)
                LayoutExportDiagnostics.Add(diagnostic);
            OnPropertyChanged(nameof(HasLayoutExportDiagnostics));

            if (!result.Success || result.PatchedXml is null)
            {
                Status = "Layout export blocked: " + DescribeExportDiagnostics(result.Diagnostics);
                return false;
            }

            var directory = Path.GetDirectoryName(destinationFullPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return FailLayoutExport("INVALID_DESTINATION", "The selected destination directory does not exist.");

            var temporary = Path.Combine(directory, $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, result.PatchedXml);
                File.Move(temporary, destinationFullPath, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            Status = $"Exported {result.ModifiedFrameCount} modified frame(s) to {destinationFullPath}. Source XML unchanged.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailLayoutExport("EXPORT_IO_FAILED", $"Layout export failed safely: {ex.Message}");
        }
    }

    private bool FailLayoutExport(string code, string message)
    {
        LayoutExportDiagnostics.Add(new ExportDiagnostic(ExportSeverity.Error, code, message));
        OnPropertyChanged(nameof(HasLayoutExportDiagnostics));
        Status = $"Layout export blocked: {code}: {message}";
        return false;
    }

    private static string DescribeExportDiagnostics(IEnumerable<ExportDiagnostic> diagnostics) =>
        string.Join(" ", diagnostics.Where(item => item.Severity == ExportSeverity.Error).Take(3)
            .Select(item => $"{item.Code}: {item.Message}"));

    /// <summary>Selects a frame, or clears the selection with null.</summary>
    /// <remarks>
    /// A plain click always replaces the selection. Adding to it is an explicit request
    /// (<see cref="ToggleSelection"/>) so that every existing caller - tree, canvas, composition
    /// panel, tests - keeps its current meaning without having to learn about modifiers.
    /// </remarks>
    public void Select(string? name)
    {
        if (IsV2Project)
        {
            SemanticId? id = name is not null && Guid.TryParseExact(name, "D", out _) ? new SemanticId(name) : null;
            _v2Session?.ReplaceSelection(id);
            RefreshV2Presentation(null);
            RefreshV2Inspector();
            return;
        }
        if (name is not null && !Project.Contains(name))
            name = null;

        if (_selectedNames.Count == (name is null ? 0 : 1)
            && (name is null || _selectedNames.Contains(name, StringComparer.Ordinal))
            && FindNode(name) == SelectedTreeNode)
        {
            return;
        }

        ReplaceSelection(name);
    }

    /// <summary>
    /// Adds the frame to the selection, or removes it when it is already selected.
    /// </summary>
    /// <remarks>
    /// This is what a Ctrl/Cmd/Shift click on the canvas and in the tree means, and it toggles
    /// rather than "clicking a selected object always makes it primary". Deselecting on the second
    /// click is the behaviour every drawing tool teaches, and a modifier that could not take an
    /// object back out of the selection would be a trap.
    /// <para>
    /// Removing the primary promotes the most recently selected survivor, so the object the
    /// inspector describes is always something the user still has selected.
    /// </para>
    /// </remarks>
    public void ToggleSelection(string? name)
    {
        if (IsV2Project)
        {
            if (_v2Session is not null && name is not null && Guid.TryParseExact(name, "D", out _))
                _v2Session.ToggleSelection(new SemanticId(name));
            else
                _v2Session?.ReplaceSelection(null);
            RefreshV2Presentation(null);
            RefreshV2Inspector();
            return;
        }
        if (name is null || !Project.Contains(name))
        {
            ReplaceSelection(null);
            return;
        }

        if (_selectedNames.Remove(name))
        {
            // Removed the primary: the new primary is whatever was selected before it.
            ApplySelection(null);
            return;
        }

        AddToSelection(name);
    }

    /// <summary>Replaces the whole selection with one frame (or with nothing).</summary>
    private void ReplaceSelection(string? name)
    {
        _selectedNames.Clear();
        if (name is not null)
            _selectedNames.Add(name);
        ApplySelection(null);
    }

/// <summary>
    /// Appends a frame to the selection and makes it the primary.
    /// </summary>
    /// <remarks>
    /// Removing before appending is what keeps the list a set with an order rather than a
    /// multiset, so a frame can never be selected twice no matter how the clicks arrive.
    /// </remarks>
    private void AddToSelection(string name)
    {
        _selectedNames.Remove(name);
        _selectedNames.Add(name);
        ApplySelection();
    }

    /// <summary>
    /// Re-publishes the selection to everything that renders it.
    /// </summary>
    /// <remarks>
    /// The last surviving entry is always the primary, so every caller mutates
    /// <see cref="_selectedNames"/> first and lets this one place decide which object the
    /// inspector describes.
    /// </remarks>
    private void ApplySelection(string? status = null)
    {
        if (IsV2Project)
            RefreshV2Presentation(status);
        else
            RelaidOut(Project, _selectedNames.Count > 0 ? _selectedNames[^1] : null, status);
    }

    /// <summary>Keeps the tree selection in step when the user clicks the tree.</summary>
    /// <remarks>
    /// CommunityToolkit.Mvvm assigns the backing field before invoking this callback, so
    /// <c>value</c> is already the new property value when this runs. The authoritative source of
    /// selection is the view model's own list - <see cref="Select"/> / <see cref="ToggleSelection"/>
    /// - and the tree is a projection of it. The only writes that should be treated as selection
    /// requests are real user gestures that arrive while the projection is NOT being rebuilt or
    /// re-published; every write that arrives while <see cref="_selectionSyncDepth"/> is non-zero is
    /// generated by the tree reacting to its own <c>ItemsSource</c> changing (for example the
    /// TreeView clearing the selected row when <see cref="TreeRoots"/> is rebuilt) and would clear
    /// or move a selection the user did not touch.
    /// </remarks>
    partial void OnSelectedTreeNodeChanged(FrameTreeNode? value)
    {
        if (_selectionSyncDepth > 0)
            return;

        Select(value?.Name);
    }

    /// <summary>Re-projects and redraws the tree whenever the search text changes.</summary>

    /// <summary>Re-projects and redraws the tree whenever the search text changes.</summary>
    partial void OnTreeSearchChanged(string value)
    {
        if (IsV2Project) RebuildV2Tree(); else RebuildTree(Project);
    }

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
    /// Applies a drag to the selected frame's offsets, or to the whole selection as one group.
    /// </summary>
    /// <remarks>
    /// The delta arrives in MODEL units from the canvas, so dragging down the screen arrives
    /// as a negative Y and correctly decreases <c>offsetY</c>.
    /// <para>
    /// A drag on a member of a multi-selection moves every selected object by the same delta
    /// through the same offset-only contract the arrange commands use, so dragging a group obeys
    /// exactly the rules an align does - including refusing to touch locked or unsafe frames.
    /// </para>
    /// </remarks>
    public void DragFrame(string name, double modelDx, double modelDy)
    {
        if (IsV2Project)
        {
            if (_v2Session is null || !Guid.TryParseExact(name, "D", out _))
                return;
            var id = new SemanticId(name);
            if (!_v2Session.Document.Nodes.Any(node => node.Id == id)) return;
            ApplyV2DragDelta(id, modelDx, modelDy);
            return;
        }
        _lastDragMovedSelection = _selectedNames.Count > 1 && _selectedNames.Contains(name, StringComparer.Ordinal);
        if (_lastDragMovedSelection)
        {
            DragSelection(modelDx, modelDy);
            return;
        }

        var frame = Project.Find(name);
        if (frame is null)
            return;
        if (Project.Editor.IsLocked(name))
        {
            Status = $"{name} is locked. Unlock the element or its group before moving it.";
            return;
        }
        if (!CanEditImportedGeometry(frame))
        {
            Status = ImportedGeometryDiagnostic(frame);
            return;
        }

        var updated = frame with { OffsetX = frame.OffsetX + modelDx, OffsetY = frame.OffsetY + modelDy };
        ReplaceFrame(name, updated);
        IsDirty = true;
    }

    private void DragSelection(double modelDx, double modelDy)
    {
        if (Project.Source?.IsReadOnlyXml == true && _selectedNames.Select(Project.Find).Any(frame => frame is null || !CanEditImportedGeometry(frame)))
        {
            Status = "The selection contains imported geometry that cannot be moved safely (multiple anchors, setAllPoints, or no source identity).";
            return;
        }
        var outcome = SelectionArrange.Move(Project, Layout, _selectedNames, modelDx, modelDy);
        if (!outcome.Changed)
        {
            Status = outcome.HasExclusions ? outcome.Message : "Nothing moved.";
            return;
        }

        Project = outcome.Project;
        IsDirty = true;
        RelaidOut(Project, SelectedName, outcome.Message);
    }

    /// <summary>Aligns or distributes the whole selection as one operation.</summary>
    /// <remarks>
    /// The measurement comes from the presentation layout, because that is what the user is
    /// looking at and clicking against; the mutation is applied to the authored project, so
    /// preview overrides and stock templates are never written into the file.
    /// </remarks>
    public void ArrangeSelection(SelectionArrangeCommand command)
    {
        if (IsV2Project)
        {
            ArrangeV2Selection(command);
            return;
        }
        if (_selectedNames.Count == 0)
            return;
        if (Project.Source?.IsReadOnlyXml == true && _selectedNames.Select(Project.Find).Any(frame => frame is null || !CanEditImportedGeometry(frame)))
        {
            Status = "The selection contains imported geometry that cannot be arranged safely.";
            return;
        }

        var outcome = SelectionArrange.Arrange(Project, Layout, _selectedNames, command);
        if (!outcome.Changed)
        {
            Status = outcome.HasExclusions ? outcome.Message : "Nothing moved.";
            return;
        }

        Project = outcome.Project;
        IsDirty = true;
        RelaidOut(Project, SelectedName, outcome.Message);
    }

    /// <summary>Called when a drag gesture ends.</summary>
    public void EndDrag()
    {
        if (!IsDirty)
            return;

        if (IsV2Project)
        {
            CompleteV2Gesture();
            return;
        }

        // The gesture decides the wording, not the selection: dragging one frame while four are
        // selected still moved one frame, and saying "4 objects" would misreport the edit.
        if (_lastDragMovedSelection)
            Status = $"Dragged {_selectedNames.Count} objects as a group.";
        else
            Status = $"Dragged {SelectedName}.";
    }

    /// <summary>Adds a frame under the current selection and selects it.</summary>
    public void AddFrame()
    {
        if (IsV2Project)
        {
            AddV2Control(UiNodeKind.Frame);
            return;
        }
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
    public void AddDesignStatusBar() => AddDesignObject(FrameKind.STATUSBAR, "Status Bar");

    public void BringSelectedToFront() => MoveSelectedDesignObject(DesignOrderMove.Front);
    public void BringSelectedForward() => MoveSelectedDesignObject(DesignOrderMove.Forward);
    public void SendSelectedBackward() => MoveSelectedDesignObject(DesignOrderMove.Backward);
    public void SendSelectedToBack() => MoveSelectedDesignObject(DesignOrderMove.Back);

    private void MoveSelectedDesignObject(DesignOrderMove move)
    {
        if (!CanChangeDesignOrder || SelectedName is not { } name)
            return;
        var order = Project.Editor.EffectiveDesignOrder(Project).ToList();
        var index = order.IndexOf(name);
        if (index < 0)
            return;
        var target = move switch
        {
            DesignOrderMove.Front => order.Count - 1,
            DesignOrderMove.Forward => Math.Min(order.Count - 1, index + 1),
            DesignOrderMove.Backward => Math.Max(0, index - 1),
            DesignOrderMove.Back => 0,
            _ => index,
        };
        if (target == index)
        {
            Status = $"{Project.Editor.DisplayNameFor(Project.Find(name)!)} is already at that DESIGN layer boundary.";
            return;
        }
        order.RemoveAt(index);
        order.Insert(target, name);
        UpdateEditor(Project.Editor with { DesignOrder = order },
            $"Moved {Project.Editor.DisplayNameFor(Project.Find(name)!)} {DescribeMove(move)}.");
    }

    private static string DescribeMove(DesignOrderMove move) => move switch
    {
        DesignOrderMove.Front => "to the front",
        DesignOrderMove.Forward => "forward one layer",
        DesignOrderMove.Backward => "backward one layer",
        DesignOrderMove.Back => "to the back of custom content",
        _ => "",
    };

    public bool PrepareDesignAssetBrowse()
    {
        if (CanBrowseDesignAssets)
            return true;
        Status = "Save the FrameForge project before choosing project-owned artwork.";
        return false;
    }

    public bool SetNewDesignImageFromFile(string path, bool importExternal = false)
    {
        if (!TryMakeProjectAssetReference(path, importExternal, out var reference, out var error))
        {
            Status = error;
            return false;
        }
        NewImageAsset = reference;
        Status = $"Selected project image {reference}.";
        return true;
    }

    public bool ChangeSelectedDesignImageFromFile(string path, bool importExternal = false)
    {
        if (!IsDesignImageSelected || SelectedName is not { } name || SelectedFrame is not { } frame)
            return false;
        if (IsSelectionLocked)
        {
            Status = $"{name} is locked. Unlock it before changing its image.";
            return false;
        }
        if (!TryMakeProjectAssetReference(path, importExternal, out var reference, out var error))
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

    public bool TryAssessDesignAssetSelection(string path, out bool requiresImport)
    {
        requiresImport = false;
        if (!TryGetProjectAssetReference(path, out _, out requiresImport, out var error))
        {
            Status = error;
            return false;
        }
        return true;
    }

    private bool TryMakeProjectAssetReference(string path, bool importExternal,
        out string reference, out string error)
    {
        if (!TryGetProjectAssetReference(path, out reference, out var requiresImport, out error))
            return false;
        if (!requiresImport)
            return true;
        if (!importExternal)
        {
            reference = string.Empty;
            error = "This image is outside the FrameForge project. Import a copy into this project's assets folder?";
            return false;
        }

        return TryImportProjectAsset(path, out reference, out error);
    }

    private bool TryGetProjectAssetReference(string path, out string reference,
        out bool requiresImport, out string error)
    {
        reference = string.Empty;
        requiresImport = false;
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
        if (!IsSupportedDesignImage(path))
        {
            error = "Design images must be PNG, TGA, or BLP files.";
            return false;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
        var relative = Path.GetRelativePath(projectDirectory, Path.GetFullPath(path)).Replace('\\', '/');
        if (!TextureAssetResolver.TryNormalizeProjectAsset(relative, out reference, out var reason))
        {
            reference = string.Empty;
            requiresImport = true;
            return true;
        }
        return true;
    }

    private bool TryImportProjectAsset(string sourcePath, out string reference, out string error)
    {
        reference = string.Empty;
        error = string.Empty;
        try
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
            // When the project itself already lives inside the intended artwork directory (a
            // project directory named "assets"), importing beside the project is the clean
            // portable result: avoiding assets/assets/foo.png. Otherwise the established
            // <project>/assets/ import folder applies.
            var artworkDirectory = Path.GetFileName(projectDirectory).Equals("assets", StringComparison.OrdinalIgnoreCase)
                ? projectDirectory
                : Path.Combine(projectDirectory, "assets");
            Directory.CreateDirectory(artworkDirectory);
            var source = Path.GetFullPath(sourcePath);
            var stem = Path.GetFileNameWithoutExtension(source);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            var destination = Path.Combine(artworkDirectory, $"{stem}{extension}");
            var suffix = 2;
            while (File.Exists(destination) && !FilesAreIdentical(source, destination))
                destination = Path.Combine(artworkDirectory, $"{stem}-{suffix++}{extension}");
            if (!File.Exists(destination))
                File.Copy(source, destination, overwrite: false);
            reference = Path.GetRelativePath(projectDirectory, destination).Replace('\\', '/');
            if (!TextureAssetResolver.TryNormalizeProjectAsset(reference, out reference, out var reason))
            {
                error = reason!;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or PathTooLongException)
        {
            error = $"Could not import the selected image: {ex.Message}";
            return false;
        }
    }

    private static bool FilesAreIdentical(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
            return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        var leftHash = System.Security.Cryptography.SHA256.HashData(leftStream);
        var rightHash = System.Security.Cryptography.SHA256.HashData(rightStream);
        return leftHash.AsSpan().SequenceEqual(rightHash);
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
            FrameKind.FONTSTRING => new FrameVisual { Text = new TextVisual("Text", "CENTER", "MIDDLE", "GameFontNormal") },
            FrameKind.TEXTURE => new FrameVisual { Texture = new TextureVisual(
                designAsset, TexCoords.Full, null, null, null, null) },
            // A new bar starts as a visibly filled half (0..100, default 50) rather than empty:
            // an empty bar is indistinguishable from a missing renderer, and the authored range
            // is what identifies it as a bar on the canvas.
            FrameKind.STATUSBAR => new FrameVisual
            {
                StatusBar = new StatusBarVisual(MinValue: 0, MaxValue: 100, DefaultValue: 50),
            },
            _ => null,
        };
        var frame = new FrameDef
        {
            Name = internalName,
            Parent = parent,
            Kind = kind,
            Width = kind == FrameKind.TEXTURE ? 64 : 120,
            Height = kind == FrameKind.FONTSTRING ? 24 : (kind == FrameKind.STATUSBAR ? 16 : 64),
            Point = AnchorPoint.CENTER,
            RelativePoint = AnchorPoint.CENTER,
            Visual = visual,
        };
        var designObject = new DesignObjectMetadata
        {
            FrameName = internalName,
            DisplayName = displayName,
            DesignAsset = kind == FrameKind.TEXTURE ? designAsset : null,
            TextStyle = kind == FrameKind.FONTSTRING ? new DesignTextStyleMetadata { BaseStyle = "GameFontNormal" } : null,
        };
        Project = Project with
        {
            Frames = [.. Project.Frames, frame],
            Editor = Project.Editor with
            {
                DesignObjects = [.. Project.Editor.DesignObjects, designObject],
                DesignOrder = [.. Project.Editor.EffectiveDesignOrder(Project), internalName],
            },
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
        if (IsV2Project)
        {
            foreach (var option in WorkspaceOptions) option.Refresh();
            RebuildTree(Project);
            NotifySelectionInspection();
            Status = workspace == WorkspaceExperience.Design
                ? "DESIGN: schema-v2 semantic authoring."
                : "INSPECT: schema-v2 ownership, diagnostics, and typed anchors.";
            return;
        }
        Project = Project with { Editor = Project.Editor with { Workspace = workspace == WorkspaceExperience.Design ? "design" : "inspect" } };
        IsDirty = true;
        foreach (var option in WorkspaceOptions) option.Refresh();
        RebuildTree(Project);
        _selectionSyncDepth++;
        try
        {
            SelectedTreeNode = FindNode(SelectedName);
        }
        finally
        {
            _selectionSyncDepth--;
        }
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

    /// <summary>Builds the state-membership chooser for the current selection, or null without one.</summary>
    /// <remarks>
    /// A fresh chooser reads the live project, so its rows are always the states that exist right
    /// now and its pre-checks describe what the selection currently shares - Mixed when it does
    /// not share one. Nothing is written until <see cref="ApplyStateMembership"/> runs, which is
    /// what makes Cancel a genuine no-op.
    /// </remarks>
    public StateMembershipChooser? CreateStateMembershipChooser() =>
        _selectedNames.Count == 0 ? null : new StateMembershipChooser(Project.Editor, _selectedNames);

    /// <summary>
    /// Applies one chooser decision to the whole selection: bulk assignment, not a per-object merge.
    /// </summary>
    /// <returns>True only when the project actually changed.</returns>
    public bool ApplyStateMembership(StateMembershipChooser chooser)
    {
        if (!chooser.TryGetResult(out var allStates, out var stateIds))
        {
            Status = chooser.ValidationMessage;
            return false;
        }

        string[] ids = allStates ? [] : [.. stateIds];
        var description = StateMembershipChooser.Describe(Project.Editor, ids);
        return ApplyMembershipToSelection((_, _) => ids,
            count => $"Set state membership to {description} for {count} object(s).",
            $"State membership is already {description}; nothing changed.");
    }

    /// <summary>Assigns every selected object to All States.</summary>
    public void AssignSelectionToAllStates() =>
        ApplyMembershipToSelection((_, _) => [],
            count => $"Assigned {count} object(s) to All States.",
            "State membership is already All States; nothing changed.");

    /// <summary>Adds the chosen authored state to every selected object's own membership.</summary>
    public void AssignSelectionToSelectedState()
    {
        if (SelectedAuthoredState?.Id is not { } id)
            return;
        var name = SelectedAuthoredState.Name;
        ApplyMembershipToSelection((_, current) => current.Contains(id, StringComparer.Ordinal) ? current : [.. current, id],
            count => $"Added \"{name}\" to {count} object(s).",
            $"Already assigned to \"{name}\"; nothing changed.");
    }

    /// <summary>Drops the chosen authored state from every selected object's membership.</summary>
    public void RemoveSelectionFromSelectedState()
    {
        if (SelectedAuthoredState?.Id is not { } id)
            return;
        var name = SelectedAuthoredState.Name;
        ApplyMembershipToSelection((_, current) => [.. current.Where(item => item != id)],
            count => $"Removed \"{name}\" from {count} object(s).",
            $"Not assigned to \"{name}\"; nothing changed.");
    }

    /// <summary>
    /// Rewrites state membership for every editable object in the current selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rewrite is computed per object and compared against that object's own membership, so
    /// applying what an object already has is a genuine no-op: the project is not rebuilt, the
    /// canvas is not re-laid out and the dirty flag is not raised. An apply that does change
    /// something runs through <see cref="UpdateEditor"/>, which marks the project dirty and
    /// re-resolves the layout, so the canvas reflects the new membership immediately.
    /// </para>
    /// <para>
    /// Locked objects are never rewritten. They stay selected and are reported in the status line
    /// rather than silently skipped, so a mixed locked-plus-editable selection changes exactly the
    /// editable DESIGN objects and says how many it left alone.
    /// </para>
    /// </remarks>
    private bool ApplyMembershipToSelection(
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>> nextMembership,
        Func<int, string> changedStatus,
        string unchangedStatus)
    {
        var selected = _selectedNames.Where(name => Project.Contains(name))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0)
        {
            Status = "No frame selected.";
            return false;
        }

        var editable = selected.Where(name => !Project.Editor.IsLocked(name)).ToArray();
        if (editable.Length == 0)
        {
            Status = "Nothing changed: every selected object is locked.";
            return false;
        }

        var values = Project.Editor.DesignObjects.ToList();
        var changed = 0;
        foreach (var name in editable)
        {
            var current = Project.Editor.DesignObjectFor(name)?.StateIds ?? [];
            var next = nextMembership(name, current);
            if (StateMembershipChooser.SameMembership(current, next))
                continue;

            var index = values.FindIndex(item => item.FrameName == name);
            var existing = index >= 0 ? values[index] : new DesignObjectMetadata { FrameName = name };
            var updated = existing with { StateIds = [.. next] };
            if (index >= 0)
                values[index] = updated;
            else
                values.Add(updated);
            changed++;
        }

        if (changed == 0)
        {
            Status = unchangedStatus;
            return false;
        }

        var lockedSkipped = selected.Length - editable.Length;
        var status = changedStatus(changed);
        if (lockedSkipped > 0)
            status += $" {lockedSkipped} locked object(s) left unchanged.";
        UpdateEditor(Project.Editor with { DesignObjects = values }, status);
        return true;
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
        if (IsV2Project)
        {
            DeleteV2Selection();
            return;
        }
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
                DesignOrder = [.. Project.Editor.DesignOrder.Where(item => item != name)],
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
    /// <param name="name">The frame under the pointer, or null for empty canvas.</param>
    /// <param name="additive">
    /// True when the user held Ctrl/Cmd/Shift, which means "add to or remove from the selection"
    /// instead of "select only this".
    /// </param>
    public void OnCanvasSelectionRequested(string? name, bool additive = false)
    {
        if (additive)
            ToggleSelection(name);
        else
            Select(name);
    }

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
        ConfigureAssets();
        SaveLocalSettings();
        RefreshAssetPresentation(_wowClient.Message);
        NotifyWoWClientState();
        ReloadV2TemplateRegistry();
    }

    public void ClearWoWClientPath()
    {
        _wowClient = _wowAssets.ValidateClient(null);
        ConfigureAssets();
        SaveLocalSettings();
        RefreshAssetPresentation("WoW client selection cleared; existing managed cache remains available.");
        NotifyWoWClientState();
        ReloadV2TemplateRegistry();
    }

    public void RevalidateWoWClient()
    {
        _wowClient = _wowAssets.ValidateClient(_wowClient.ClientPath);
        ConfigureAssets();
        RefreshAssetPresentation(_wowClient.Message);
        NotifyWoWClientState();
        ReloadV2TemplateRegistry();
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

    /// <summary>Legacy advanced manual roots; the managed stock cache is no longer a "root" here.</summary>
    private IReadOnlyList<string> LegacyAssetRoots() => [.. AssetRoots];

    /// <summary>
    /// Configures resolution order: project-owned artwork beside the project file, logical WoW
    /// references through the configured client and its managed cache, then any legacy roots.
    /// </summary>
    private void ConfigureAssets()
    {
        Assets.Configure(_assetSourcePath, LegacyAssetRoots(), ProjectPath,
            EnumerateDesignReferences());
        Assets.ConfigureWowAssetSource(_wowAssets, _wowClient);
    }

    private IEnumerable<string> EnumerateDesignReferences()
    {
        if (V2Document is { } v2)
        {
            foreach (var reference in v2.Editor?.ProjectAssets.SelectMany(asset =>
                         new[] { asset.SourceReference, asset.PreviewReference, asset.PreparedReference }) ?? [])
                yield return reference;
            foreach (var reference in v2.Nodes.SelectMany(node => new[]
                     {
                         node.AuthoredProperties.Texture?.TextureReference,
                         node.AuthoredProperties.StatusBar?.TextureReference,
                     }).OfType<string>().Where(reference => !IsWowClientReference(reference)))
                yield return reference;
            yield break;
        }
        foreach (var reference in Project.Editor.DesignObjects.Select(item => item.DesignAsset).OfType<string>())
            yield return reference;
        foreach (var reference in Project.Frames
            .Select(frame => frame.Visual?.StatusBar?.BarTexture)
            .OfType<string>()
            .Where(reference => !IsWowClientReference(reference)))
            yield return reference;
    }

    private IReadOnlyList<string> StockAssetReferences() => EnumerateAssetReferences()
        .Where(reference => Assets.Resolve(reference).SourceKind is not (AssetSourceKind.SourceRelative or AssetSourceKind.ProjectRelative))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private IReadOnlyList<string> MissingStockAssetReferences() => StockAssetReferences()
        .Where(reference => !Assets.Resolve(reference).CanRender)
        .ToArray();

    private IEnumerable<string> EnumerateAssetReferences()
    {
        if (V2Document is { } v2)
        {
            foreach (var reference in v2.Nodes.SelectMany(node => new[]
                     {
                         node.AuthoredProperties.Texture?.TextureReference,
                         node.AuthoredProperties.StatusBar?.TextureReference,
                     }).OfType<string>())
                yield return reference;
            yield break;
        }
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

    private static string? ResolveFunctionalSourcePath(Project project, string? projectPath)
    {
        var reference = project.FunctionalExport?.Source.ReferencePath;
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        if (Path.IsPathRooted(reference))
            return reference;
        return projectPath is { Length: > 0 }
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, reference))
            : null;
    }

    private void RefreshFunctionalSourceOptions()
    {
        FunctionalValueSourceOptions.Clear();
        var path = _functionalSourcePath ?? ResolveFunctionalSourcePath(Project, ProjectPath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            var imported = FrameXmlImporter.ImportFile(path);
            if (!imported.Ok || imported.Project is null)
                return;
            foreach (var name in imported.Project.Frames
                         .Where(frame => frame.Kind is FrameKind.FONTSTRING or FrameKind.STATUSBAR)
                         .Select(frame => frame.Name).OrderBy(name => name, StringComparer.Ordinal))
                FunctionalValueSourceOptions.Add(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The export command reports a missing/unreadable association. Loading a saved visual
            // design must remain possible even when its optional functional source is offline.
        }
    }

    private void NotifyFunctionalExportState()
    {
        OnPropertyChanged(nameof(HasFunctionalExport));
        OnPropertyChanged(nameof(CanExportFunctionalDesign));
        OnPropertyChanged(nameof(FunctionalExportSummary));
        OnPropertyChanged(nameof(CanMapFunctionalValue));
    }

    private void ReplaceFrame(string name, FrameDef updated, string? rename = null)
    {
        if (Project.Editor.IsLocked(name))
        {
            Status = $"{name} is locked. Unlock the element or its group before editing geometry.";
            RelaidOut(Project, name, null);
            return;
        }
        var original = Project.Find(name);
        if (original is not null && Project.Source?.IsReadOnlyXml == true)
        {
            if (!CanEditImportedGeometry(original))
            {
                Status = ImportedGeometryDiagnostic(original);
                RelaidOut(Project, name, null);
                return;
            }
            if (rename is not null || !SupportedImportedGeometryOnly(original, updated))
            {
                Status = "Functional FrameXML editing is layout-only: frame identity, parent, anchor relationship, visibility, and appearance remain read-only.";
                RelaidOut(Project, name, null);
                return;
            }
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
                DesignOrder = [.. editor.DesignOrder.Select(item => item == name ? targetName : item)],
            };
        }
        Project = Project with { Frames = frames, Editor = editor };
        RefreshGroups(Project);
        IsDirty = true;
        RelaidOut(Project, targetName, null);
    }

    private bool CanEditImportedGeometry(FrameDef frame) =>
        Project.Source?.IsReadOnlyXml != true ||
        (frame.SourceLocation is not null && !frame.Placeholder && !frame.SetAllPoints && frame.ExtraAnchors.Count == 0);

    private static string ImportedGeometryDiagnostic(FrameDef frame) => frame switch
    {
        { Placeholder: true } => "This is a synthesized stand-in and has no safe source element to patch.",
        { SourceLocation: null } => "This frame has no unambiguous source location and is locked for functional export.",
        { SetAllPoints: true } => "This frame uses setAllPoints; explicit size and offsets do not safely control its runtime geometry.",
        { ExtraAnchors.Count: > 0 } => "This frame has multiple anchors; FrameForge will not guess which constraint should move.",
        _ => string.Empty,
    };

    private static bool SupportedImportedGeometryOnly(FrameDef before, FrameDef after) =>
        before with { Width = after.Width, Height = after.Height, OffsetX = after.OffsetX, OffsetY = after.OffsetY } == after;

    private void RelaidOut(Project project, string? selection, string? status)
    {
        // Selection is a model identity.  Normalize it at the refresh boundary so every
        // caller (delete, load, tree, or canvas) gets the same explicit no-selection state.
        if (selection is not null && !project.Contains(selection))
            selection = null;
        selection = NormalizeSelection(project, selection);
        SelectedName = selection;

        RefreshPresentation(project);
        _selectionSyncDepth++;
        try
        {
            RebuildTree(project);
            SelectedTreeNode = FindNode(selection);
        }
        finally
        {
            _selectionSyncDepth--;
        }
        Editor.Refresh(project, selection);
        Editor.RefreshResolved(selection is not null && Layout.Frames.TryGetValue(selection, out var selectedLayout)
            ? selectedLayout
            : null);
        RefreshComposition(selection);
        _syncingDesignUi = true;
        DesignNameDraft = selection is null ? string.Empty : project.Editor.DisplayNameFor(project.Find(selection)!);
        DesignTextDraft = selection is null
            ? string.Empty
            : project.Editor.DesignObjectFor(selection)?.TextOverride
              ?? project.Find(selection)?.Visual?.Text?.Text
              ?? string.Empty;
        RefreshTextStyleUi(project, selection);
        RefreshStatusBarUi(project, selection);
        RefreshRuntimeBindingUi(project, selection);
        _syncingDesignUi = false;
        CanvasSelectionNames.Clear();
        foreach (var name in Layout.PaintOrder)
            CanvasSelectionNames.Add(name);

        OnPropertyChanged(nameof(ProjectDescription));
        NotifySelectionInspection();
        NotifySelectionSet();
        SelectionSummary = IsMultiSelection ? MultiSelectionSummary : Editor.ResolvedSummary;

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
    /// Brings the ordered selection back in line with the project it now belongs to and returns
    /// the object that should be primary afterwards.
    /// </summary>
    /// <remarks>
    /// <paramref name="selection"/> is the primary the caller asked for. When it is not already in
    /// the set the caller meant "this is the selection now" - adding a frame, deleting one, loading
    /// a document - so the set collapses to it instead of growing. Callers that genuinely mean
    /// "add one" mutate the set first and pass the new primary, which is already a member.
    /// <para>
    /// The returned primary is always the last surviving entry rather than whatever was passed in.
    /// Deleting the primary out of a multi-selection, or switching design state and losing it,
    /// both leave a perfectly good selection behind, and reporting no selection when there is one
    /// would throw away work the user can still see on the canvas.
    /// </para>
    /// </remarks>
    private string? NormalizeSelection(Project project, string? selection)
    {
        if (selection is not null && !_selectedNames.Contains(selection, StringComparer.Ordinal))
        {
            _selectedNames.Clear();
            _selectedNames.Add(selection);
        }

        _selectedNames.RemoveAll(name => !project.Contains(name));
        return _selectedNames.Count > 0 ? _selectedNames[^1] : null;
    }

    /// <summary>
    /// Drops selected objects that the current view cannot show, and reports it.
    /// </summary>
    /// <remarks>
    /// Visibility is not pruned on every refresh on purpose. Filtering the canvas down to frames
    /// only does not make the other selected frames go away - the user can still name them in the
    /// tree and align them deliberately. Switching DESIGN state is different: objects that are not
    /// members of the active state are not drawn at all, so a selection of four where two are
    /// invisible is a selection the user cannot check, and one more state switch would silently
    /// change what a later drag moves.
    /// </remarks>
    private void PruneInvisibleSelection()
    {
        var removed = new List<string>();
        _selectedNames.RemoveAll(name =>
        {
            if (Project.Find(name) is { } frame && ViewPolicy.EffectiveVisible(frame, Layout))
                return false;
            removed.Add(name);
            return true;
        });

        if (removed.Count == 0)
            return;

        ApplySelection($"{removed.Count} selected object(s) left the selection because the active state does not show them.");
    }

    /// <summary>Tells the UI that the shape of the selection changed, not just the primary.</summary>
    private void NotifySelectionSet()
    {
        OnPropertyChanged(nameof(SelectedNames));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(IsMultiSelection));
        OnPropertyChanged(nameof(ShowsSingleObjectEditors));
        OnPropertyChanged(nameof(CanAlignSelection));
        OnPropertyChanged(nameof(CanDistributeSelection));
        OnPropertyChanged(nameof(MultiSelectionSummary));
    }

    private void RefreshPresentation(Project project)
    {

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
        previewProject = previewProject with
        {
            Frames = [.. previewProject.Frames.Select(frame =>
            {
                if (frame.Kind != FrameKind.FONTSTRING
                    || project.Editor.DesignObjectFor(frame.Name)?.TextOverride is not { } textOverride)
                    return frame;
                var text = frame.Visual?.Text ?? new TextVisual(null);
                return frame with
                {
                    Visual = (frame.Visual ?? new FrameVisual()) with { Text = text with { Text = textOverride } },
                };
            })],
        };
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
    }

    private void RefreshTextStyleUi(Project project, string? selection)
    {
        var frame = project.Find(selection);
        var metadata = project.Editor.DesignObjectFor(selection)?.TextStyle;
        var effective = frame is { Kind: FrameKind.FONTSTRING }
            ? DesignTextStyleResolver.Resolve(frame, project.Editor.DesignObjectFor(selection), _stockTemplates)
            : null;
        SelectedTextStyle = TextStyleOptions.FirstOrDefault(option => option.Name == effective?.BaseStyle);
        TextStyleSizeDraft = metadata?.Size is { } size ? Number(size) : effective?.Style is { } style ? Number(style.Size) : string.Empty;
        TextStyleColorDraft = metadata?.Color is { } color ? ColorHex(color) : effective?.Style is { } styled ? ColorHex(styled.Color) : string.Empty;
        SelectedTextOutline = metadata?.Outline switch { "NONE" => "None", "NORMAL" => "Normal", "THICK" => "Thick", _ => "Style default" };
        SelectedTextShadow = metadata?.Shadow switch { true => "On", false => "Off", _ => "Style default" };
        SelectedTextAlignment = metadata?.JustifyH switch { "LEFT" => "Left", "CENTER" => "Center", "RIGHT" => "Right", _ => "Style default" };
        TextStyleValidation = string.Empty;
    }

    private void RefreshStatusBarUi(Project project, string? selection)
    {
        var frame = project.Find(selection);
        var bar = frame is { Kind: FrameKind.STATUSBAR } ? frame.Visual?.StatusBar : null;
        StatusBarMinDraft = bar?.MinValue is { } min ? Number(min) : string.Empty;
        StatusBarMaxDraft = bar?.MaxValue is { } max ? Number(max) : string.Empty;
        StatusBarValueDraft = bar?.DefaultValue is { } value ? Number(value) : string.Empty;
        StatusBarTextureDraft = bar?.BarTexture ?? string.Empty;
        StatusBarColorDraft = bar?.BarColor is { } color ? ColorHex(color) : string.Empty;
        StatusBarValidation = string.Empty;
        OnPropertyChanged(nameof(SelectedStatusBarFractionText));
        OnPropertyChanged(nameof(SelectedStatusBarColorSwatch));
        OnPropertyChanged(nameof(SelectedStatusBarTexturePhysical));
    }

    private void RefreshRuntimeBindingUi(Project project, string? selection)
    {
        _syncingDesignUi = true;
        try
        {
            var metadata = project.Editor.DesignObjectFor(selection);
            RuntimeValueRequiredDraft = metadata?.RuntimeValueRequired == true;
            RuntimeBindingDraft = metadata?.RuntimeBinding ?? string.Empty;
            RuntimeBindingValidation = string.Empty;
            SelectedFunctionalValueSource = project.FunctionalExport?.Values
                .FirstOrDefault(item => item.DesignFrameName == selection)?.SourceFrameName;
        }
        finally
        {
            _syncingDesignUi = false;
        }
        OnPropertyChanged(nameof(RuntimeBindingOperationSummary));
        OnPropertyChanged(nameof(CanMapFunctionalValue));
    }

    private static string ColorHex(ColorRgba color) => $"#{Channel(color.A):X2}{Channel(color.R):X2}{Channel(color.G):X2}{Channel(color.B):X2}";
    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static bool TryParseColor(string value, out ColorRgba color)
    {
        color = default;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return false;
        color = new ColorRgba(((packed >> 16) & 0xff) / 255d, ((packed >> 8) & 0xff) / 255d,
            (packed & 0xff) / 255d, ((packed >> 24) & 0xff) / 255d);
        return true;
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
        OnPropertyChanged(nameof(CanEditLayoutStructure));
        OnPropertyChanged(nameof(SelectedGeometryEditDiagnostic));
        OnPropertyChanged(nameof(SelectedOrigin));
        OnPropertyChanged(nameof(SelectedElementKind));
        OnPropertyChanged(nameof(SelectedSourceFile));
        OnPropertyChanged(nameof(SelectedSourcePath));
        OnPropertyChanged(nameof(SelectedSourceLocation));
        OnPropertyChanged(nameof(SelectedInheritance));
        OnPropertyChanged(nameof(SelectedParentName));
        OnPropertyChanged(nameof(SelectedSourceAnchor));
        OnPropertyChanged(nameof(SelectedGroupMembership));
        OnPropertyChanged(nameof(SelectedPreviewProvenance));
        OnPropertyChanged(nameof(IsSelectionLocked));
        OnPropertyChanged(nameof(DesignLockedSummary));
        OnPropertyChanged(nameof(SelectedElementLocked));
        OnPropertyChanged(nameof(SelectedGroupLocked));
        OnPropertyChanged(nameof(SelectedStateMembership));
        OnPropertyChanged(nameof(HasConceptualStockFramework));
        OnPropertyChanged(nameof(ConceptualStockFramework));
        OnPropertyChanged(nameof(IsStockFrameworkSelected));
        OnPropertyChanged(nameof(StockFrameworkAction));
        OnPropertyChanged(nameof(IsDesignImageSelected));
        OnPropertyChanged(nameof(IsDesignTextSelected));
        OnPropertyChanged(nameof(IsDesignStatusBarSelected));
        OnPropertyChanged(nameof(CanEditDesignStatusBar));
        OnPropertyChanged(nameof(IsRuntimeBindingEligible));
        OnPropertyChanged(nameof(CanEditRuntimeBinding));
        OnPropertyChanged(nameof(RuntimeBindingOperationSummary));
        OnPropertyChanged(nameof(SelectedStatusBarFractionText));
        OnPropertyChanged(nameof(SelectedStatusBarColorSwatch));
        OnPropertyChanged(nameof(SelectedStatusBarTexturePhysical));
        OnPropertyChanged(nameof(CanEditTextStyle));
        OnPropertyChanged(nameof(CanPasteTextStyle));
        OnPropertyChanged(nameof(SelectedTextBaseStyle));
        OnPropertyChanged(nameof(SelectedTextFont));
        OnPropertyChanged(nameof(SelectedTextEffectiveSize));
        OnPropertyChanged(nameof(SelectedTextEffectiveColor));
        OnPropertyChanged(nameof(SelectedTextEffectiveOutline));
        OnPropertyChanged(nameof(SelectedTextEffectiveShadow));
        OnPropertyChanged(nameof(SelectedTextEffectiveAlignment));
        OnPropertyChanged(nameof(SelectedTextOverrides));
        OnPropertyChanged(nameof(SelectedTextColorSwatch));
        OnPropertyChanged(nameof(CanChangeDesignOrder));
        OnPropertyChanged(nameof(SelectedSourceText));
        OnPropertyChanged(nameof(SelectedDesignTextOverride));
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
        _selectedPreviewState = PreviewStateOptions.FirstOrDefault(state => state.Id == project.Editor.PreviewStateId)
                                ?? _previewStates.DefaultStateFor(project);
        OnPropertyChanged(nameof(SelectedPreviewState));
        OnPropertyChanged(nameof(PreviewStateExplanation));
        OnPropertyChanged(nameof(PreviewSimulationSummary));
    }

    /// <summary>
    /// Rebuilds the tree through the current structural filter and search.
    /// </summary>
    /// <remarks>
    /// Filtering and searching decide what is DISPLAYED and never what EXISTS: the projection is
    /// discarded and recomputed from the project every time, so a filter cannot delete a widget and
    /// clearing the search box always restores the full hierarchy.
    /// <para>
    /// Rebuilding the <c>TreeRoots</c> collection makes the TreeView clear and re-realize rows,
    /// which writes the cleared selection back through the TwoWay binding. That write is feedback
    /// from the projection changing and must not touch the model's selection, so the whole rebuild
    /// is done under <see cref="_selectionSyncDepth"/>.
    /// </para>
    /// </remarks>
    private void RebuildTree(Project project)
    {
        _selectionSyncDepth++;
        try
        {
            if (_discardTreeExpansionOnNextRebuild)
                _discardTreeExpansionOnNextRebuild = false;
            else
                CaptureTreeExpansion(TreeRoots);

            ExpandSelectedPath(project, SelectedName);
            RebuildTreeCore(project);
        }
        finally
        {
            _selectionSyncDepth--;
        }
    }

    private void CaptureTreeExpansion(IEnumerable<FrameTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!node.IsConceptual)
            {
                if (node.IsExpanded)
                    _expandedTreeNames.Add(node.Name);
                else
                    _expandedTreeNames.Remove(node.Name);
            }
            CaptureTreeExpansion(node.Children);
        }
    }

    /// <summary>Expands only the ancestors needed to reveal the exact selected identity.</summary>
    private void ExpandSelectedPath(Project project, string? selectedName)
    {
        for (var frame = project.Find(selectedName); frame?.Parent is { } parent; frame = project.Find(parent))
            _expandedTreeNames.Add(parent);
    }

    private void RebuildTreeCore(Project project)
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
                    stock.Locked, stock.Name, stock.Name, true, stock.Expanded,
                    IsSelected: _selectedNames.Contains(representative.Name, StringComparer.Ordinal),
                    IsPrimarySelection: representative.Name == SelectedName));
            }
        }

        var roots = Workspace == WorkspaceExperience.Design && stock is not null
            ? project.Frames.Where(frame => !stockMembers.Contains(frame.Name)
                && (frame.Parent is null || stockMembers.Contains(frame.Parent)))
            : FrameHierarchy.Children(project, null);
        if (Workspace == WorkspaceExperience.Design)
        {
            var designRanks = project.Editor.EffectiveDesignOrder(project)
                .Select((name, index) => (name, index))
                .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
            // DESIGN tree convention: bottom item is front, matching back-to-front paint order.
            roots = roots.OrderBy(frame => designRanks.GetValueOrDefault(frame.Name, -1));
        }
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
            Workspace == WorkspaceExperience.Design ? DesignTreeDisplayName(project, frame) : frame.Name,
            IsExpanded: _expandedTreeNames.Contains(frame.Name),
            IsSelected: _selectedNames.Contains(frame.Name, StringComparer.Ordinal),
            IsPrimarySelection: frame.Name == SelectedName);
    }

    /// <summary>
    /// Gives an imported panel the useful name of its direct background artwork while keeping the
    /// artwork itself distinct. This prevents an anonymous texture from masquerading as the owning
    /// source frame in DESIGN (for example, Record versus Hunt Panel Record).
    /// </summary>
    private static string DesignTreeDisplayName(Project project, FrameDef frame)
    {
        var display = project.Editor.DisplayNameFor(frame);
        if (frame.Kind == FrameKind.FRAME && FindRepresentativeArtwork(project, frame) is { } artwork)
            return project.Editor.DisplayNameFor(artwork);

        if (frame.Kind == FrameKind.TEXTURE && frame.Anonymous && frame.Parent is { } parentName
            && project.Find(parentName) is { Kind: FrameKind.FRAME } parent
            && ReferenceEquals(FindRepresentativeArtwork(project, parent), frame))
            return $"{display} Artwork";

        return display;
    }

    private static FrameDef? FindRepresentativeArtwork(Project project, FrameDef frame)
    {
        var frameDisplay = project.Editor.DisplayNameFor(frame);
        return FrameHierarchy.Children(project, frame.Name)
            .FirstOrDefault(child => child.Kind == FrameKind.TEXTURE && child.Anonymous
                && child.Visual?.Texture?.File is { Length: > 0 }
                && project.Editor.DisplayNameFor(child).EndsWith(frameDisplay, StringComparison.OrdinalIgnoreCase));
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
