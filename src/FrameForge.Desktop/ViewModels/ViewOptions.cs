using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Inspection;

namespace FrameForge.Desktop.ViewModels;

/// <summary>
/// One entry in the canvas mode selector.
/// </summary>
/// <remarks>
/// A wrapper rather than the bare enum so the selector can show a name that means something
/// ("Debug", "Preview") and carry the mode's own description as its tooltip, instead of making the
/// user learn three all-caps words and read the manual to find out what Preview is allowed to hide.
/// </remarks>
public sealed partial class CanvasModeOption : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    internal CanvasModeOption(MainWindowViewModel owner, CanvasViewMode mode)
    {
        _owner = owner;
        Mode = mode;
    }

    /// <summary>The mode this entry selects.</summary>
    public CanvasViewMode Mode { get; }

    /// <summary>Displayed name.</summary>
    public string Label => Mode.Label();

    /// <summary>What this mode shows and hides, shown as the tooltip.</summary>
    public string Description => Mode.Description();

    /// <summary>Whether the canvas is in this mode right now.</summary>
    public bool IsSelected
    {
        get => _owner.ViewMode == Mode;
        set
        {
            if (value)
                _owner.SetViewMode(Mode);
        }
    }

    /// <summary>Re-reads the owning view model, for when the mode changed some other way.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    public override string ToString() => Label;
}

/// <summary>One entry in the label policy selector.</summary>
public sealed partial class LabelPolicyOption : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    internal LabelPolicyOption(MainWindowViewModel owner, LabelPolicy policy)
    {
        _owner = owner;
        Policy = policy;
    }

    /// <summary>The policy this entry selects.</summary>
    public LabelPolicy Policy { get; }

    /// <summary>Displayed name.</summary>
    public string Label => Policy.Label();

    /// <summary>Whether the canvas is using this policy right now.</summary>
    public bool IsSelected
    {
        get => _owner.LabelPolicy == Policy;
        set
        {
            if (value)
                _owner.LabelPolicy = Policy;
        }
    }

    /// <summary>Re-reads the owning view model.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    public override string ToString() => Label;
}

/// <summary>
/// One visibility category toggle in the toolbar.
/// </summary>
/// <remarks>
/// Reads and writes the owning view model's single <c>CanvasFilter</c> rather than keeping a flag of
/// its own. A toggle with private state is the classic way for a toolbar control to disagree with
/// the thing it is supposed to control, and the disagreement only shows up after a mode change
/// resets the filter behind the toggle's back.
/// </remarks>
public sealed partial class VisibilityToggle : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    internal VisibilityToggle(MainWindowViewModel owner, string label, VisibilityFilter flag, string toolTip)
    {
        _owner = owner;
        Label = label;
        Flag = flag;
        ToolTip = toolTip;
    }

    /// <summary>Button text.</summary>
    public string Label { get; }

    /// <summary>The category this toggle controls.</summary>
    public VisibilityFilter Flag { get; }

    /// <summary>What the category contains, shown as the tooltip.</summary>
    public string ToolTip { get; }

    /// <summary>Whether this category is currently drawn and pickable.</summary>
    public bool IsOn
    {
        get => _owner.IsCategoryVisible(Flag);
        set
        {
            if (IsOn == value)
                return;

            _owner.SetCategoryVisible(Flag, value);
            OnPropertyChanged(nameof(IsOn));
        }
    }

    /// <summary>Re-reads the owning view model's filter.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(IsOn));
}

/// <summary>One non-destructive origin visibility toggle.</summary>
public sealed partial class OriginVisibilityToggle : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    internal OriginVisibilityToggle(MainWindowViewModel owner, string label, OriginVisibility flag, string toolTip)
    {
        _owner = owner;
        Label = label;
        Flag = flag;
        ToolTip = toolTip;
    }

    public string Label { get; }
    public OriginVisibility Flag { get; }
    public string ToolTip { get; }

    public bool IsOn
    {
        get => _owner.OriginFilter.HasFlag(Flag);
        set
        {
            if (IsOn == value)
                return;
            _owner.SetOriginVisible(Flag, value);
            OnPropertyChanged(nameof(IsOn));
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsOn));
}

/// <summary>One structural filter in the tree selector.</summary>
public sealed partial class TreeFilterOption : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    internal TreeFilterOption(MainWindowViewModel owner, TreeFilter filter)
    {
        _owner = owner;
        Filter = filter;
    }

    /// <summary>The filter this entry selects.</summary>
    public TreeFilter Filter { get; }

    /// <summary>Displayed name.</summary>
    public string Label => Filter.Label();

    /// <summary>Whether the tree is narrowed this way.</summary>
    public bool IsSelected
    {
        get => _owner.TreeFilter == Filter;
        set
        {
            if (value)
                _owner.TreeFilter = Filter;
        }
    }

    /// <summary>Re-reads the owning view model.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    public override string ToString() => Label;
}
