using FrameForge.Core.Models;

namespace FrameForge.Core;

/// <summary>Factories for projects and frames, with every default in one place.</summary>
public static class ProjectFactory
{
    /// <summary>
    /// Anchor a frame gets when no other anchor is supplied. Matches a WoW
    /// <c>&lt;Anchor point="TOPLEFT"/&gt;</c> with no relativeTo.
    /// </summary>
    public const AnchorPoint DefaultPoint = AnchorPoint.TOPLEFT;

    /// <summary>Creates a project with copied frames.</summary>
    public static Project Create(string name, IEnumerable<FrameDef> frames, Screen? screen = null) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name,
        Screen = screen ?? Screen.Default,
        Frames = frames.Select(f => f with { }).ToArray(),
    };

    /// <summary>
    /// A new project containing a single 355x500 window anchored at the screen TOPLEFT.
    /// That size matches the Native Hunts window, so a fresh project is immediately
    /// recognisable.
    /// </summary>
    public static Project Empty(string name = "Untitled") => Create(name,
    [
        new FrameDef
        {
            Name = "MainWindow",
            Width = 355,
            Height = 500,
            Point = AnchorPoint.TOPLEFT,
            RelativePoint = AnchorPoint.TOPLEFT,
        },
    ]);

    /// <summary>A genuinely blank design workspace; objects are added explicitly by the designer.</summary>
    public static Project Blank(string name = "Untitled") => Create(name, []);
}
