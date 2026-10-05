using System.Globalization;
using FrameForge.Core.Models;

namespace FrameForge.Desktop.ViewModels;

/// <summary>One option in the Parent / Relative To pickers.</summary>
/// <param name="Name">The frame name, or null for the "(none)" entry.</param>
public sealed record FrameOption(string? Name)
{
    /// <summary>Displayed text; the screen is spelled out rather than left blank.</summary>
    public string Display => Name ?? "(screen / none)";

    public override string ToString() => Display;
}

/// <summary>A node in the frame tree.</summary>
/// <param name="Frame">The frame this node shows.</param>
/// <param name="Children">Direct children, so the tree mirrors the parent relationship.</param>
public sealed record FrameTreeNode(
    FrameDef Frame,
    IReadOnlyList<FrameTreeNode> Children,
    string OriginLabel,
    bool IsLocked,
    string GroupNames,
    string? DisplayNameOverride = null,
    bool IsConceptual = false,
    bool IsExpanded = true,
    bool IsSelected = false,
    bool IsPrimarySelection = false)
{
    /// <summary>Frame name, shown as the tree label.</summary>
    public string Name => Frame.Name;
    public string DisplayName => IsConceptual
        ? $"{(IsLocked ? "🔒" : "🔓")} {DisplayNameOverride ?? Frame.Name}"
        : DisplayNameOverride ?? Frame.Name;

    /// <summary>
    /// Marker for a selected row, so a multi-selection is visible in the tree.
    /// </summary>
    /// <remarks>
    /// The TreeView can only highlight one item, and it highlights the primary. The other members
    /// of the selection would otherwise vanish from the tree the moment the user Ctrl+clicked them
    /// on the canvas, which reads as "the click did nothing".
    /// </remarks>
    public string SelectionBadge => IsPrimarySelection ? "◉" : IsSelected ? "•" : string.Empty;

    public bool ShowsSelectionBadge => IsSelected || IsPrimarySelection;

    /// <summary>
    /// What kind of FrameXML widget this is, so the tree does not imply a hierarchy of panels
    /// where the file actually contains textures and text.
    /// </summary>
    public string KindBadge => Frame.Placeholder ? "stand-in" : Frame.Kind.Badge();

    /// <summary>True when the widget's size came from somewhere other than the file.</summary>
    public bool IsFill => Frame.SetAllPoints;

    /// <summary>True when the source element had no name of its own.</summary>
    public bool IsAnonymous => Frame.Anonymous;

    public string LockBadge => IsLocked ? "🔒" : string.Empty;

    public string GroupBadge => string.IsNullOrWhiteSpace(GroupNames) ? string.Empty : $"[{GroupNames}]";

    /// <summary>Size beside the name; parent-relative sizes are marked as such.</summary>
    public string SizeText
    {
        get
        {
            if (Frame.SetAllPoints)
                return "fills anchor target";

            var width = Format(Frame.Width);
            var height = Format(Frame.Height);
            return Frame.SizeReferenceOrDefault == SizeReference.PARENT
                ? $"{width} x {height} (of parent)"
                : $"{width} x {height}";
        }
    }

    private static string Format(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}
