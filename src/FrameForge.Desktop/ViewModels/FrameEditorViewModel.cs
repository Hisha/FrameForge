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
/// Every setter writes back through <see cref="Commit"/>, which asks the owning view model to
/// replace the frame and re-resolve the layout. Nothing here touches Avalonia types, and
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

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public FrameEditorViewModel(
        Action<string, FrameDef, string?> commit,
        Func<IReadOnlyList<FrameOption>> parentOptions,
        Func<IReadOnlyList<AnchorPoint>> anchorOptions)
    {
        _commit = commit;
        _parentOptions = parentOptions;
        _anchorOptions = anchorOptions;
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
        ValidationMessage = string.Empty;
    }

    /// <summary>Mirrors the engine's output for the edited frame.</summary>
    public void RefreshResolved(FrameLayout? layout)
    {
        Resolved = layout?.Rect;
        AnchoredTo = layout?.AnchoredTo;
        HasIssues = layout is { Issues.Count: > 0 };
        IssueText = layout is { } detail ? string.Join("; ", detail.Issues) : string.Empty;
        OnPropertyChanged(nameof(Resolved));
        OnPropertyChanged(nameof(AnchoredTo));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(IssueText));
        OnPropertyChanged(nameof(ResolvedSummary));
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

    partial void OnNameChanged(string value)
    {
        if (Frame is null || string.IsNullOrWhiteSpace(value) || value == Frame.Name)
            return;

        // A rename must rewrite every reference to the frame, not just the frame itself.
        Commit(Frame with { Name = value.Trim() }, rename: value.Trim());
    }

    partial void OnParentChanged(FrameOption? value) => CommitField(frame => frame with { Parent = value?.Name });

    partial void OnRelativeToChanged(FrameOption? value) => CommitField(frame => frame with { RelativeTo = value?.Name });

    partial void OnPointChanged(AnchorPoint value) => CommitField(frame => frame with { Point = value });

    partial void OnRelativePointChanged(AnchorPoint value) => CommitField(frame => frame with { RelativePoint = value });

    partial void OnIsVisibleChanged(bool value) => CommitField(frame => frame with { Visible = value });

    partial void OnSizeReferenceChanged(SizeReference value) =>
        CommitField(frame => frame with { SizeReference = value });

    partial void OnWidthChanged(string value) => CommitNumber("width", value, (frame, width) => frame with { Width = width });

    partial void OnHeightChanged(string value) => CommitNumber("height", value, (frame, height) => frame with { Height = height });

    partial void OnOffsetXChanged(string value) => CommitNumber("offsetX", value, (frame, x) => frame with { OffsetX = x });

    partial void OnOffsetYChanged(string value) => CommitNumber("offsetY", value, (frame, y) => frame with { OffsetY = y });

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

    private void CommitNumber(string label, string text, Func<FrameDef, double, FrameDef> change)
    {
        if (Frame is null)
            return;

        if (!TryParseNumber(text, out var value))
        {
            ValidationMessage = $"{label} must be a number.";
            return;
        }

        if (change(Frame, value) == Frame)
            return;

        ValidationMessage = string.Empty;
        _commit(Frame.Name, change(Frame, value), null);
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