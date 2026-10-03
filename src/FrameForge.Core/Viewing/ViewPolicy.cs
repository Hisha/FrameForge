using FrameForge.Core.Models;

namespace FrameForge.Core.Viewing;

/// <summary>
/// What the canvas is for. Three first-class modes, not a set of independent switches.
/// </summary>
/// <remarks>
/// The Phase 2 canvas drew one thing: a wireframe with labels. That is genuinely useful for
/// understanding a FrameXML file and useless for designing a WoW UI, and the two jobs want
/// opposite things from the same screen. Rather than bolt a "hide labels" checkbox onto a
/// wireframe, the canvas is given an explicit mode, because each mode has a coherent default set
/// and the defaults are the point.
/// <para>
/// This enum lives in Core, and so does every decision made from it, so that "what does Preview
/// actually show" is a testable fact rather than a property of Avalonia brushes.
/// </para>
/// </remarks>
public enum CanvasViewMode
{
    /// <summary>
    /// The engineering view: bounds, names, badges, anchors, offsets, zero-area markers,
    /// stand-ins and unresolved frames. This is what Phase 2 drew, and it remains the default
    /// so that opening a file never surprises anyone.
    /// </summary>
    DEBUG = 0,

    /// <summary>
    /// "What should this UI look like in WoW?" Visual content only. No names, no anchor
    /// connectors, no geometry labels, no helper structures. Widgets FrameForge cannot yet
    /// reproduce faithfully are drawn as restrained stand-ins, never as invented artwork.
    /// </summary>
    PREVIEW = 1,

    /// <summary>
    /// The design view: the clean Preview rendering, plus the selection's own geometry and
    /// anchor relationship. The answer to "show me the UI, but show me exactly what controls
    /// the thing I have selected" - and the mode intended to become the default for editing.
    /// </summary>
    HYBRID = 2,
}

/// <summary>Display helpers for <see cref="CanvasViewMode"/>.</summary>
public static class CanvasViewModes
{
    /// <summary>Short display name for the mode selector.</summary>
    public static string Label(this CanvasViewMode mode) => mode switch
    {
        CanvasViewMode.DEBUG => "Debug",
        CanvasViewMode.PREVIEW => "Preview",
        CanvasViewMode.HYBRID => "Hybrid",
        _ => mode.ToString(),
    };

    /// <summary>One-line explanation, shown as the selector's tooltip.</summary>
    public static string Description(this CanvasViewMode mode) => mode switch
    {
        CanvasViewMode.DEBUG => "Bounds, names, anchors and diagnostics. Everything is shown.",
        CanvasViewMode.PREVIEW => "Visual content only. No debug labels or helper structures.",
        CanvasViewMode.HYBRID => "Preview rendering, plus geometry and anchors for the selection.",
        _ => string.Empty,
    };

    /// <summary>True when the mode draws diagnostic overlays for widgets other than the selection.</summary>
    public static bool ShowsDebugOverlayForAll(this CanvasViewMode mode) => mode == CanvasViewMode.DEBUG;
}

/// <summary>
/// Which widgets' LABELS are drawn.
/// </summary>
/// <remarks>
/// Phase 2 drew every widget's name at once. On a 52-element import that is an unreadable wall
/// of overlapping text, which is the single biggest reason the canvas felt hostile. The policy is
/// explicit so the fix is a deliberate choice rather than a consequence of zoom level.
/// </remarks>
public enum LabelPolicy
{
    /// <summary>No labels at all. The Preview default.</summary>
    NONE = 0,

    /// <summary>Only the selected widget is labelled. The Hybrid default.</summary>
    SELECTED = 1,

    /// <summary>Every visible widget is labelled. The Debug default, and the Phase 2 behaviour.</summary>
    ALL = 2,
}

/// <summary>Human-facing names for <see cref="LabelPolicy"/>.</summary>
public static class LabelPolicies
{
    /// <summary>Display name, e.g. <c>Selected</c>.</summary>
    public static string Label(this LabelPolicy policy) => policy switch
    {
        LabelPolicy.NONE => "None",
        LabelPolicy.SELECTED => "Selected",
        LabelPolicy.ALL => "All",
        _ => policy.ToString(),
    };
}

/// <summary>
/// Which categories of widget the canvas draws. A flag set so the toolbar can offer independent
/// toggles, but always applied as one value so a mode can set a coherent default in one step.
/// </summary>
/// <remarks>
/// These control VIEWPORT VISIBILITY ONLY. Nothing here removes anything from the project: a
/// hidden FontString is still in the tree, still in the saved file, and still selected. Filtering
/// the model to match a view would mean the saved project depended on what the user happened to
/// be looking at.
/// </remarks>
[Flags]
public enum VisibilityFilter
{
    /// <summary>Nothing is drawn.</summary>
    NONE = 0,

    /// <summary>Structural <c>&lt;Frame&gt;</c> widgets.</summary>
    FRAMES = 1 << 0,

    /// <summary><c>&lt;Button&gt;</c> widgets.</summary>
    BUTTONS = 1 << 1,

    /// <summary><c>&lt;FontString&gt;</c> widgets.</summary>
    TEXT = 1 << 2,

    /// <summary><c>&lt;Texture&gt;</c> widgets.</summary>
    TEXTURES = 1 << 3,

    /// <summary>
    /// Diagnostic-only structures: synthesized stand-ins for frames the source references but
    /// does not define, and bare zero-area frames that have nothing to draw.
    /// </summary>
    HELPERS = 1 << 4,

    /// <summary>Widgets whose imported state is <c>hidden</c>, which WoW would not draw.</summary>
    HIDDEN = 1 << 5,

    /// <summary>Every widget, including hidden ones and helper structures.</summary>
    ALL = FRAMES | BUTTONS | TEXT | TEXTURES | HELPERS | HIDDEN,
}

/// <summary>Human-facing names and ordering for the visibility toggles.</summary>
public static class VisibilityFilters
{
    /// <summary>One toggle, in toolbar order: its label and the flag it controls.</summary>
    public static readonly (string Label, VisibilityFilter Flag, string ToolTip)[] Toggles =
    [
        ("Frames", VisibilityFilter.FRAMES, "Structural <Frame> widgets"),
        ("Buttons", VisibilityFilter.BUTTONS, "<Button> widgets"),
        ("Text", VisibilityFilter.TEXT, "<FontString> widgets"),
        ("Textures", VisibilityFilter.TEXTURES, "<Texture> widgets"),
        ("Helpers", VisibilityFilter.HELPERS, "Stand-ins and bare zero-area frames"),
        ("Hidden", VisibilityFilter.HIDDEN, "Widgets the source marked hidden"),
    ];
}

/// <summary>
/// What a widget IS for navigation purposes, independent of how it is drawn.
/// </summary>
/// <remarks>
/// A stand-in for <c>LFDParentFrame</c> is structurally a frame, so it navigates with frames.
/// That is why <see cref="FrameCategory.HELPER"/> collapses into
/// <see cref="FrameCategory.STRUCTURE"/>: every widget stays reachable from exactly one of the
/// tree's structural or visual filters, so filtering can never hide part of the document.
/// </remarks>
public enum FrameCategory
{
    /// <summary>Containers and controls: frames, buttons, stand-ins.</summary>
    STRUCTURE = 0,

    /// <summary>Things that paint: textures, font strings, status bars.</summary>
    VISUAL = 1,
}

/// <summary>How the frames tree is narrowed.</summary>
public enum TreeFilter
{
    /// <summary>The complete imported hierarchy.</summary>
    ALL = 0,

    /// <summary>Frames, buttons and stand-ins: the containers that give the layout its shape.</summary>
    STRUCTURE = 1,

    /// <summary>Textures, font strings and status bars: the things the player actually sees.</summary>
    VISUAL = 2,
}

/// <summary>Human-facing names for <see cref="TreeFilter"/>.</summary>
public static class TreeFilters
{
    /// <summary>Display name.</summary>
    public static string Label(this TreeFilter filter) => filter switch
    {
        TreeFilter.ALL => "All",
        TreeFilter.STRUCTURE => "Structure",
        TreeFilter.VISUAL => "Visual",
        _ => filter.ToString(),
    };

    /// <summary>The filters offered in the tree, in order.</summary>
    public static IReadOnlyList<TreeFilter> All { get; } = [TreeFilter.ALL, TreeFilter.STRUCTURE, TreeFilter.VISUAL];
}

/// <summary>
/// Every visibility, labelling and default decision the canvas makes, as pure functions.
/// </summary>
/// <remarks>
/// The point of this class is that "Preview hides helpers" is a statement about FrameForge's
/// policy that can be asserted in a unit test, not a fact buried in an Avalonia render pass.
/// The canvas asks these questions; it never answers them for itself.
/// </remarks>
public static class ViewPolicy
{
    /// <summary>
    /// The visibility set a mode starts with.
    /// </summary>
    /// <remarks>
    /// Debug keeps everything because diagnosing a layout means seeing the hidden panels and the
    /// stand-ins that the real geometry depends on. Preview and Hybrid both drop helpers and
    /// hidden widgets, because a designer looking at "the UI" does not want a purple dashed box
    /// for a frame that does not exist in the addon.
    /// </remarks>
    public static VisibilityFilter DefaultsFor(CanvasViewMode mode) => mode switch
    {
        CanvasViewMode.DEBUG => VisibilityFilter.ALL,
        CanvasViewMode.PREVIEW => VisibilityFilter.FRAMES | VisibilityFilter.BUTTONS
            | VisibilityFilter.TEXT | VisibilityFilter.TEXTURES,
        CanvasViewMode.HYBRID => VisibilityFilter.FRAMES | VisibilityFilter.BUTTONS
            | VisibilityFilter.TEXT | VisibilityFilter.TEXTURES,
        _ => VisibilityFilter.ALL,
    };

    /// <summary>The label policy a mode starts with.</summary>
    /// <remarks>
    /// Debug defaults to All because that is the Phase 2 behaviour and switching to Debug must not
    /// silently hide the information people came to Debug for. Preview defaults to None and
    /// Hybrid to Selected.
    /// </remarks>
    public static LabelPolicy DefaultLabelPolicyFor(CanvasViewMode mode) => mode switch
    {
        CanvasViewMode.DEBUG => LabelPolicy.ALL,
        CanvasViewMode.PREVIEW => LabelPolicy.NONE,
        CanvasViewMode.HYBRID => LabelPolicy.SELECTED,
        _ => LabelPolicy.ALL,
    };

    /// <summary>
    /// True when the widget exists only to explain the layout, rather than to be part of the UI.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Two tempting candidates are excluded:
    /// <list type="bullet">
    /// <item>An <b>anonymous</b> widget is not a helper. Most of NativeHuntsFrame.xml's
    /// background art is unnamed, and classifying anonymity as diagnostic-only would hide the
    /// artwork from Preview - the exact opposite of what Preview is for.</item>
    /// <item>A <b>zero-area</b> widget is not automatically a helper. A FontString with no
    /// <c>&lt;Size&gt;</c> is sized by WoW's font metrics and does paint real text; it is
    /// invisible here only because FrameForge cannot measure the font. It is treated as helper
    /// only when it also has no retained visual content, which is the case that genuinely cannot
    /// be drawn: WoW's <c>NativeHuntsFrameInitializer</c>, a bare frame whose only job is to
    /// carry <c>&lt;Scripts&gt;</c>.</item>
    /// </list>
    /// </remarks>
    public static bool IsHelper(FrameDef frame)
    {
        if (frame.Placeholder)
            return true;

        return !frame.HasArea && (frame.Visual is null || frame.Visual.IsEmpty);
    }

    /// <summary>What the widget is for navigation.</summary>
    public static FrameCategory CategoryOf(FrameDef frame) => frame.Kind switch
    {
        FrameKind.TEXTURE or FrameKind.FONTSTRING or FrameKind.STATUSBAR => FrameCategory.VISUAL,
        _ => FrameCategory.STRUCTURE,
    };

    /// <summary>One widget's category flag.</summary>
    private static VisibilityFilter CategoryFlag(FrameKind kind) => kind switch
    {
        FrameKind.TEXTURE => VisibilityFilter.TEXTURES,
        FrameKind.FONTSTRING => VisibilityFilter.TEXT,
        FrameKind.BUTTON => VisibilityFilter.BUTTONS,
        _ => VisibilityFilter.FRAMES,
    };

    /// <summary>
    /// Whether the canvas draws this widget, given its resolved visibility and the current set.
    /// </summary>
    /// <remarks>
    /// Two different notions of "hidden" are in play, and conflating them is the trap:
    /// <list type="bullet">
    /// <item><b>Resolved</b> visibility folds in the ancestors. A texture inside a panel the
    /// source hid is not on screen, and drawing it would leave a floating backdrop over whatever
    /// replaced the panel.</item>
    /// <item><b>Own</b> visibility is the element's own <c>hidden</c> attribute. When the user asks
    /// to see hidden widgets they want the hidden SUBTREE - NativeHunts keeps four state panels,
    /// each with its own children, behind its own flag - so resolved visibility is deliberately
    /// ignored in that case. Inspecting "show all four panels at once" is the whole point of the
    /// HIDDEN toggle.</item>
    /// </list>
    /// </remarks>
    public static bool IsVisible(FrameDef frame, bool effectiveVisible, VisibilityFilter filter)
    {
        if (IsHelper(frame))
            return filter.HasFlag(VisibilityFilter.HELPERS);

        if (!filter.HasFlag(VisibilityFilter.HIDDEN) && !effectiveVisible)
            return false;

        return filter.HasFlag(CategoryFlag(frame.Kind));
    }

    /// <summary>
    /// Resolves effective visibility through the layout, falling back to the widget's own flag when
    /// no layout is available.
    /// </summary>
    public static bool IsVisible(FrameDef frame, Geometry.LayoutResult? layout, VisibilityFilter filter) =>
        IsVisible(frame, EffectiveVisible(frame, layout), filter);

    /// <summary>The widget's resolved visibility, including its ancestors'.</summary>
    public static bool EffectiveVisible(FrameDef frame, Geometry.LayoutResult? layout) =>
        layout is not null && layout.Frames.TryGetValue(frame.Name, out var detail)
            ? detail.EffectiveVisible
            : frame.Visible;

    /// <summary>Whether this widget's name label is drawn.</summary>
    public static bool ShouldDrawLabel(
        FrameDef frame,
        bool selected,
        LabelPolicy policy,
        VisibilityFilter filter,
        bool effectiveVisible)
    {
        if (!IsVisible(frame, effectiveVisible, filter))
            return false;

        return policy switch
        {
            LabelPolicy.NONE => false,
            LabelPolicy.SELECTED => selected,
            LabelPolicy.ALL => true,
            _ => false,
        };
    }

    /// <summary>
    /// Whether this widget's name label is drawn, resolving visibility through the layout.
    /// </summary>
    /// <remarks>
    /// Named separately rather than overloading <see cref="ShouldDrawLabel(FrameDef, bool, LabelPolicy, VisibilityFilter, bool)"/>
    /// so that two adjacent overloads differing only in the type of a trailing argument cannot be
    /// confused at a call site.
    /// </remarks>
    public static bool ShouldDrawLabelIn(
        FrameDef frame,
        bool selected,
        LabelPolicy policy,
        VisibilityFilter filter,
        Geometry.LayoutResult? layout) =>
        ShouldDrawLabel(frame, selected, policy, filter, EffectiveVisible(frame, layout));

    /// <summary>
    /// Whether the mode draws debug-only structures - zero-area markers, stand-in outlines,
    /// unresolved strips - for widgets other than the selection.
    /// </summary>
    public static bool ShowsDebugStructures(CanvasViewMode mode) => mode == CanvasViewMode.DEBUG;

    /// <summary>
    /// Whether the selected widget gets its anchors and resolved bounds drawn.
    /// </summary>
    /// <remarks>
    /// True in Hybrid, which is the mode whose whole purpose is showing what controls the
    /// selection, and in Debug. False in Preview, which must stay free of geometry clutter.
    /// </remarks>
    public static bool ShowsSelectionGeometry(CanvasViewMode mode) =>
        mode is CanvasViewMode.DEBUG or CanvasViewMode.HYBRID;
}