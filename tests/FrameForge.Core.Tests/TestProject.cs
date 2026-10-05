using FrameForge.Core;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// Shared builders for the geometry tests. Keeping them here means each test file reads
/// as geometry intent rather than as object-initialization noise.
/// </summary>
internal static class TestProject
{
    public static readonly Screen Standard = new(1024, 768);

    /// <summary>A frame with sensible defaults, overridden per test.</summary>
    public static FrameDef Frame(
        string name,
        string? parent = null,
        double width = 100,
        double height = 100,
        AnchorPoint point = AnchorPoint.TOPLEFT,
        string? relativeTo = null,
        AnchorPoint relativePoint = AnchorPoint.TOPLEFT,
        double offsetX = 0,
        double offsetY = 0,
        bool visible = true,
        SizeReference? sizeReference = null,
        Stratum? stratum = null,
        int? level = null,
        bool setAllPoints = false,
        IReadOnlyList<FrameAnchor>? extraAnchors = null) => new()
        {
            Name = name,
            Parent = parent,
            Width = width,
            Height = height,
            Point = point,
            RelativeTo = relativeTo,
            RelativePoint = relativePoint,
            OffsetX = offsetX,
            OffsetY = offsetY,
            Visible = visible,
            SizeReference = sizeReference,
            Stratum = stratum,
            Level = level,
            SetAllPoints = setAllPoints,
            ExtraAnchors = extraAnchors ?? [],
        };

    public static Project Project(params FrameDef[] frames) => ProjectFactory.Create("test", frames, Standard);

    /// <summary>Resolves a single frame anchored to the screen with the given relationship.</summary>
    public static FrameRect Anchored(
        AnchorPoint point,
        AnchorPoint relativePoint,
        double offsetX,
        double offsetY,
        double width = 100,
        double height = 100)
    {
        var result = LayoutResolver.Resolve(Project(Frame("F", width: width, height: height, point: point,
            relativePoint: relativePoint, offsetX: offsetX, offsetY: offsetY)));
        Assert.True(result.Rects.TryGetValue("F", out var rect), "frame did not resolve");
        return rect;
    }

    /// <summary>The 355x500 window the Native Hunts example hangs everything under.</summary>
    public static FrameDef ParentFrame(double width = 355, double height = 500, double offsetX = 0, double offsetY = 0) =>
        Frame("Parent", width: width, height: height, offsetX: offsetX, offsetY: offsetY);
}