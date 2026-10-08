using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;

namespace FrameForge.Desktop.ViewModels;

/// <summary>
/// Editable view of the selected frame.
/// </summary>
/// <remarks>
/// Text setters update edit buffers only. Enter or LostFocus calls <see cref="CommitBufferedField"/>,
/// which asks the owning view model to replace the frame and re-resolve the layout once. Nothing here touches Avalonia types, and
/// nothing here recomputes geometry itself: <see cref="Refresh"/> mirrors whatever the
/// engine resolved.
/// <para>
/// Numeric fields are edited as strings and parsed on commit. A half-typed value like "-"
/// must not be able to corrupt the model, and a bad entry is reported in the status bar
/// instead of silently becoming zero.
/// </para>
/// </remarks>
public sealed partial class FrameEditorViewModel : ObservableObject
{
    private readonly Action<string, FrameDef, string?> _commit;
    private readonly Func<IReadOnlyList<FrameOption>> _parentOptions;
    private readonly Func<IReadOnlyList<AnchorPoint>> _anchorOptions;
    private readonly Func<FrameDef, IEnumerable<string>> _assetDescription;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public FrameEditorViewModel(
        Action<string, FrameDef, string?> commit,
        Func<IReadOnlyList<FrameOption>> parentOptions,
        Func<IReadOnlyList<AnchorPoint>> anchorOptions,
        Func<FrameDef, IEnumerable<string>>? assetDescription = null)
    {
        _commit = commit;
        _parentOptions = parentOptions;
        _anchorOptions = anchorOptions;
        _assetDescription = assetDescription ?? (_ => []);
    }

    /// <summary>True when a frame is selected and safe to edit.</summary>
    [ObservableProperty]
    private bool _hasSelection;

    /// <summary>Absolute bounds resolved by the engine, for the inspector summary.</summary>
    public FrameRect? Resolved { get; private set; }

    /// <summary>Anchor target reported by the engine: relativeTo, else parent, else screen.</summary>
    public string? AnchoredTo { get; private set; }

    /// <summary>True when the engine could not resolve this frame.</summary>
    public bool HasIssues { get; private set; }

    /// <summary>Problems the engine reported for this frame.</summary>
    public string IssueText { get; private set; } = string.Empty;

    /// <summary>The frame currently being edited. Null when nothing is selected.</summary>
    public FrameDef? Frame { get; private set; }

    /// <summary>
    /// The anchors the source declared after the first one, shown read-only.
    /// </summary>
    /// <remarks>
    /// FrameForge keeps them rather than dropping them, but it also does not pretend to edit
    /// them: WoW solves several anchors at once and there is no safe way to rewrite one without
    /// knowing the order the game would apply them in. They are displayed so the user can see
    /// what the file asked for, which is the thing that was otherwise invisible.
    /// </remarks>
    public ObservableCollection<AnchorLine> ExtraAnchors { get; } = [];

    /// <summary>True when the selected frame declared more than one anchor.</summary>
    public bool HasExtraAnchors => ExtraAnchors.Count > 0;

    /// <summary>Where this frame came from, when it came from an imported file.</summary>
    public string SourceNote { get; private set; } = string.Empty;

    /// <summary>Anchors the engine could not honour, joined for display.</summary>
    public string AnchorNote { get; private set; } = string.Empty;

    /// <summary>The authoritative primary anchor retained from the project/source model.</summary>
    public string SourceAnchorSummary
    {
        get
        {
            if (Frame is null)
                return string.Empty;
            var target = Frame.RelativeTo ?? Frame.Parent ?? "screen";
            return $"{Frame.Point} → {Frame.RelativePoint} of {target}; offset ({Number(Frame.OffsetX)}, {Number(Frame.OffsetY)})";
        }
    }

    /// <summary>
    /// What the source declared visually, one line per fact, read-only.
    /// </summary>
    /// <remarks>
    /// These are the values FrameForge retained from the file, not what it rendered. The distinction
    /// is the whole point of showing them: it understands a texture path and a literal string, and it
    /// does not decode the art or run the Lua. A runtime string reads as "runtime" here precisely so
    /// nobody reads the panel and concludes FrameForge knows the text the add-on will show.
    /// </remarks>
    public ObservableCollection<string> VisualLines { get; } = [];

    /// <summary>True when the selected widget declared anything visual.</summary>
    public bool HasVisual => VisualLines.Count > 0;

    /// <summary>True when the frame was synthesized to stand in for an external one.</summary>
    public bool IsPlaceholder => Frame?.Placeholder ?? false;

    /// <summary>True when the source element was anonymous.</summary>
    public bool IsAnonymous => Frame?.Anonymous ?? false;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private FrameOption? _parent;
    [ObservableProperty] private FrameOption? _relativeTo;
    [ObservableProperty] private string _width = string.Empty;
    [ObservableProperty] private string _height = string.Empty;
    [ObservableProperty] private AnchorPoint _point = AnchorPoint.TOPLEFT;
    [ObservableProperty] private AnchorPoint _relativePoint = AnchorPoint.TOPLEFT;
    [ObservableProperty] private string _offsetX = string.Empty;
    [ObservableProperty] private string _offsetY = string.Empty;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private SizeReference _sizeReference = SizeReference.SCREEN;

    /// <summary>Picker options for Parent and Relative To; refreshed on every selection change.</summary>
    public IReadOnlyList<FrameOption> FrameOptions => _parentOptions();

    /// <summary>Every anchor point, for the Point and Relative Point pickers.</summary>
    public IReadOnlyList<AnchorPoint> AnchorOptions => _anchorOptions();

    /// <summary>Size-reference options. PARENT means the width/height are fractions.</summary>
    public IReadOnlyList<SizeReference> SizeReferenceOptions { get; } =
        [SizeReference.SCREEN, SizeReference.PARENT];

    /// <summary>Loads a frame (or clears the editor when null).</summary>
    public void Refresh(Project project, string? name)
    {
        Frame = project.Find(name);
        HasSelection = Frame is not null;

        if (Frame is null)
        {
            Resolved = null;
            AnchoredTo = null;
            HasIssues = false;
            IssueText = string.Empty;
            ValidationMessage = string.Empty;
            SourceNote = string.Empty;
            ExtraAnchors.Clear();
            VisualLines.Clear();
            OnPropertyChanged(nameof(HasVisual));
            RefreshAnchorNotes(null);
            Name = string.Empty;
            Parent = null;
            RelativeTo = null;
            Width = string.Empty;
            Height = string.Empty;
            OffsetX = string.Empty;
            OffsetY = string.Empty;
            Point = RelativePoint = AnchorPoint.TOPLEFT;
            IsVisible = true;
            SizeReference = SizeReference.SCREEN;
            OnPropertyChanged(nameof(SourceAnchorSummary));
            return;
        }

        Name = Frame.Name;
        Parent = Match(Frame.Parent);
        RelativeTo = Match(Frame.RelativeTo);
        Width = Number(Frame.Width);
        Height = Number(Frame.Height);
        Point = Frame.Point;
        RelativePoint = Frame.RelativePoint;
        OffsetX = Number(Frame.OffsetX);
        OffsetY = Number(Frame.OffsetY);
        IsVisible = Frame.Visible;
        SizeReference = Frame.SizeReferenceOrDefault;
        SourceNote = DescribeSource(Frame, imported: project.Source is { IsReadOnlyXml: true });
        ValidationMessage = string.Empty;

        OnPropertyChanged(nameof(SourceNote));
        OnPropertyChanged(nameof(SourceAnchorSummary));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(IsAnonymous));
    }

    /// <summary>
    /// One line describing where the selected frame came from.
    /// </summary>
    /// <remarks>
    /// Shown read-only, because the imported document is the authority on its own widgets.
    /// A <c>FontString</c> whose size came from <c>GameFontHighlightSmall</c> looks identical to
    /// one that declared its size unless the user is told which, and "0 x 0" in a width/height
    /// box is otherwise indistinguishable from a mistake.
    /// </remarks>
    private static string DescribeSource(FrameDef frame, bool imported)
    {
        // Empty means "this frame was not imported", which is what lets the view hide the box
        // instead of showing provenance for a project the user built by hand.
        if (!imported)
            return string.Empty;

        if (frame.Placeholder)
            return $"Stand-in for \"{frame.Name}\", which this file references but does not define. " +
                   "Its size is FrameForge's guess; the real one is decided at runtime.";

        var parts = new List<string> { $"imported as <{frame.Kind.TagName()}>" };

        if (frame.Anonymous)
            parts.Add("unnamed in the source, so FrameForge generated this identity");
        else if (frame.SourceName is { } source && source != frame.Name)
            parts.Add($"written as \"{source}\" before $parent expansion");

        if (frame.Inherits is { Length: > 0 } inherits)
            parts.Add($"declares inherits=\"{inherits}\"; effective machine-local resolution is reported below when available");

        if (frame.SetAllPoints)
            parts.Add("setAllPoints: fills its anchor target, ignoring size and offsets");

        if (!frame.Visible)
            parts.Add("hidden in the source (hidden=\"true\")");

        return string.Join("  |  ", parts) + ".";
    }

    /// <summary>Mirrors the engine's output for the edited frame.</summary>
    public void RefreshResolved(FrameLayout? layout)
    {
        Resolved = layout?.Rect;
        AnchoredTo = layout?.AnchoredTo;
        HasIssues = layout is { Issues.Count: > 0 };
        IssueText = layout is { } detail ? string.Join("; ", detail.Issues) : string.Empty;

        ExtraAnchors.Clear();
        if (layout is { } withAnchors)
        {
            foreach (var anchor in withAnchors.Anchors.Where(a => !a.Primary))
                ExtraAnchors.Add(AnchorLine.From(anchor));
        }

        RefreshAnchorNotes(layout);
        RefreshVisualLines();

        OnPropertyChanged(nameof(Resolved));
        OnPropertyChanged(nameof(AnchoredTo));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssueText));
        OnPropertyChanged(nameof(ResolvedSummary));
        OnPropertyChanged(nameof(HasExtraAnchors));
    }

    /// <summary>Rebuilds the declared-visual lines for the current frame.</summary>
    private void RefreshVisualLines()
    {
        VisualLines.Clear();

        if (Frame?.Visual?.Describe() is { } description)
        {
            foreach (var line in description.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                VisualLines.Add(line);
        }

        if (Frame is { } frame)
        {
            foreach (var line in _assetDescription(frame))
                VisualLines.Add(line);
        }

        OnPropertyChanged(nameof(HasVisual));
    }

    /// <summary>Re-runs local asset diagnostics after roots or caches change.</summary>
    public void RefreshAssetLines() => RefreshVisualLines();

    private void RefreshAnchorNotes(FrameLayout? layout)
    {
        AnchorNote = layout is { UnresolvedAnchorNotes.Count: > 0 } notes
            ? string.Join("  ", notes.UnresolvedAnchorNotes)
            : string.Empty;

        OnPropertyChanged(nameof(AnchorNote));
    }

    /// <summary>One-line summary of the resolved geometry, for the inspector header.</summary>
    public string ResolvedSummary
    {
        get
        {
            if (!HasSelection)
                return "No frame selected.";

            if (Resolved is not { } rect)
                return "Unresolved: the engine could not position this frame.";

            var number = (double value) => value == Math.Floor(value)
                ? value.ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

            return $"x {number(rect.Left)} .. {number(rect.Right)}    " +
                   $"y {number(rect.Bottom)} .. {number(rect.Top)}    " +
                   $"({number(rect.Width)} x {number(rect.Height)})    " +
                   $"anchored to {AnchoredTo ?? "screen"}";
        }
    }

    partial void OnParentChanged(FrameOption? value) => CommitField(frame => frame with { Parent = value?.Name });

    partial void OnRelativeToChanged(FrameOption? value) => CommitField(frame => frame with { RelativeTo = value?.Name });

    partial void OnPointChanged(AnchorPoint value) => CommitField(frame => frame with { Point = value });

    partial void OnRelativePointChanged(AnchorPoint value) => CommitField(frame => frame with { RelativePoint = value });

    partial void OnIsVisibleChanged(bool value) => CommitField(frame => frame with { Visible = value });

    partial void OnSizeReferenceChanged(SizeReference value) =>
        CommitField(frame => frame with { SizeReference = value });

    /// <summary>Commits one buffered text field at an explicit Enter/LostFocus boundary.</summary>
    public bool CommitBufferedField(string field)
    {
        if (Frame is null)
            return false;
        return field switch
        {
            "name" => CommitName(),
            "width" => CommitNumber("width", Width, (frame, value) => frame with { Width = value }),
            "height" => CommitNumber("height", Height, (frame, value) => frame with { Height = value }),
            "offsetX" => CommitNumber("offset X", OffsetX, (frame, value) => frame with { OffsetX = value }),
            "offsetY" => CommitNumber("offset Y", OffsetY, (frame, value) => frame with { OffsetY = value }),
            _ => false,
        };
    }

    public void CancelBufferedField(string field)
    {
        if (Frame is null)
            return;
        switch (field)
        {
            case "name": Name = Frame.Name; break;
            case "width": Width = Number(Frame.Width); break;
            case "height": Height = Number(Frame.Height); break;
            case "offsetX": OffsetX = Number(Frame.OffsetX); break;
            case "offsetY": OffsetY = Number(Frame.OffsetY); break;
        }
        ValidationMessage = string.Empty;
    }

    private bool CommitName()
    {
        var value = Name.Trim();
        if (value.Length == 0)
        {
            ValidationMessage = "name must not be empty.";
            return false;
        }
        if (value == Frame!.Name)
        {
            ValidationMessage = string.Empty;
            return true;
        }
        ValidationMessage = string.Empty;
        Commit(Frame with { Name = value }, rename: value);
        return true;
    }

    private FrameOption? Match(string? name) =>
        FrameOptions.FirstOrDefault(o => o.Name == name) ?? (name is null ? FrameOptions.FirstOrDefault() : null);

    private static string Number(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);

    private void CommitField(Func<FrameDef, FrameDef> change)
    {
        if (Frame is null)
            return;

        var updated = change(Frame);
        if (updated == Frame)
            return;

        ValidationMessage = string.Empty;
        _commit(Frame.Name, updated, null);
    }

    private bool CommitNumber(string label, string text, Func<FrameDef, double, FrameDef> change)
    {
        if (Frame is null)
            return false;

        if (!TryParseNumber(text, out var value))
        {
            ValidationMessage = $"{label} must be a number.";
            return false;
        }

        if (change(Frame, value) == Frame)
        {
            ValidationMessage = string.Empty;
            return true;
        }

        ValidationMessage = string.Empty;
        _commit(Frame.Name, change(Frame, value), null);
        return true;
    }

    /// <summary>
    /// Applies a drag delta to the frame's OFFSETS and nothing else.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the drag interaction: <c>Point</c>, <c>RelativeTo</c> and
    /// <c>RelativePoint</c> are preserved, so the frame stays anchored exactly as authored.
    /// The incoming delta is already in model units, so dragging down the screen decreases
    /// <c>offsetY</c>.
    /// </remarks>
    public void ApplyDrag(double modelDx, double modelDy)
    {
        if (Frame is null)
            return;

        var updated = Frame with
        {
            OffsetX = Frame.OffsetX + modelDx,
            OffsetY = Frame.OffsetY + modelDy,
        };

        OffsetX = Number(updated.OffsetX);
        OffsetY = Number(updated.OffsetY);
        _commit(Frame.Name, updated, null);
    }

    /// <summary>Commits a frame update, optionally treating it as a rename.</summary>
    public void Commit(FrameDef updated, string? rename = null)
    {
        var oldName = Frame?.Name ?? updated.Name;
        _commit(oldName, updated, rename);
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
/// <summary>
/// One resolved anchor relationship, formatted for the read-only list in the inspector.
/// </summary>
/// <remarks>
/// Every field is a copy of something the engine already resolved; nothing here recomputes
/// geometry. The point of the record is that the XAML can bind a <c>TextBlock.Text</c> without
/// a converter per field, and that the wording cannot drift away from the model.
/// </remarks>
public sealed record AnchorLine(
    string Label,
    string Point,
    string Target,
    string RelativePoint,
    string Offsets,
    bool Holds,
    string Note)
{
    /// <summary>Builds the line from one resolved anchor.</summary>
    public static AnchorLine From(ResolvedAnchor anchor)
    {
        var number = (double value) => value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

        return new AnchorLine(
            anchor.Label,
            anchor.Point.ToString(),
            anchor.TargetIsPlaceholder ? $"{anchor.Target} (stand-in)" : anchor.Target ?? "screen / UIParent",
            anchor.RelativePoint.ToString(),
            $"{number(anchor.OffsetX)}, {number(anchor.OffsetY)}",
            anchor.Resolved,
            anchor.Note ?? "Holds.");
    }
}
