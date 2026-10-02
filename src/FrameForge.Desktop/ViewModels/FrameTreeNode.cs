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
public sealed record FrameTreeNode(FrameDef Frame, IReadOnlyList<FrameTreeNode> Children)
{
    /// <summary>Frame name, shown as the tree label.</summary>
    public string Name => Frame.Name;

    /// <summary>Size beside the name; parent-relative sizes are marked as such.</summary>
    public string SizeText
    {
        get
        {
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