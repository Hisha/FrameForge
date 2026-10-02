using FrameForge.Core.Models;

namespace FrameForge.Core.Examples;

/// <summary>
/// The built-in example project: an approximation of the Native Hunts interface geometry
/// (mod-native-hunts -&gt; Interface/FrameXML/NativeHuntsFrame.xml) at 1024x768.
/// </summary>
/// <remarks>
/// This exists to give FrameForge something real to validate against from day one. Nothing
/// in the engine or the editor knows about Native Hunts; this is ordinary FrameForge data
/// that happens to describe that particular UI.
/// <para>
/// Two deliberate departures from the source XML:
/// <list type="number">
/// <item>The NativeHuntsFrame root uses <c>setAllPoints="true"</c> (it fills UIParent).
/// FrameForge has no setAllPoints yet, so the root is modelled as a plain 355x500 frame
/// anchored TOPLEFT of the screen, the closest faithful representation of the window
/// content area.</item>
/// <item>In the source XML Identity, HuntState, Idle and Record all start
/// <c>hidden="true"</c> and are shown by Lua depending on hunt state. They are modelled
/// here as visible so the deliberate overlap between them is immediately inspectable.
/// Toggle them off in the inspector to emulate the individual states.</item>
/// </list>
/// </para>
/// <para>
/// The geometry is intentionally NOT "fixed": Identity, State and Idle overlap on purpose
/// and are part of what this layout is meant to show.
/// </para>
/// </remarks>
public static class NativeHuntsExample
{
    /// <summary>The project name.</summary>
    public const string ProjectName = "Native Hunts";

    /// <summary>The screen the geometry was authored against.</summary>
    public static Screen Screen => new(1024, 768);

    /// <summary>Creates a fresh copy of the example project.</summary>
    public static Project CreateProject() => ProjectFactory.Create(ProjectName,
    [
        new FrameDef
        {
            Name = "MainWindow",
            Width = 355,
            Height = 500,
            Point = AnchorPoint.TOPLEFT,
            RelativePoint = AnchorPoint.TOPLEFT,
        },
        new FrameDef
        {
            Name = "Content",
            Parent = "MainWindow",
            Width = 296,
            Height = 406,
            Point = AnchorPoint.TOP,
            RelativeTo = "MainWindow",
            RelativePoint = AnchorPoint.TOP,
            OffsetX = 12,
            OffsetY = -44,
        },
        new FrameDef
        {
            Name = "Identity",
            Parent = "Content",
            Width = 280,
            Height = 88,
            Point = AnchorPoint.TOP,
            RelativeTo = "Content",
            RelativePoint = AnchorPoint.TOP,
            OffsetY = -26,
        },
        new FrameDef
        {
            Name = "State",
            Parent = "Content",
            Width = 280,
            Height = 118,
            Point = AnchorPoint.TOP,
            RelativeTo = "Identity",
            RelativePoint = AnchorPoint.BOTTOM,
            OffsetY = -30,
        },
        new FrameDef
        {
            Name = "Idle",
            Parent = "Content",
            Width = 280,
            Height = 212,
            Point = AnchorPoint.TOP,
            RelativeTo = "Identity",
            RelativePoint = AnchorPoint.TOP,
        },
        new FrameDef
        {
            Name = "Record",
            Parent = "Content",
            Width = 280,
            Height = 86,
            Point = AnchorPoint.BOTTOM,
            RelativeTo = "Content",
            RelativePoint = AnchorPoint.BOTTOM,
            OffsetY = 8,
        },
    ], Screen);
}