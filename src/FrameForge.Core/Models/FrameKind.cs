namespace FrameForge.Core.Models;

/// <summary>
/// Which World of Warcraft widget a <see cref="FrameDef"/> stands for.
/// </summary>
/// <remarks>
/// v0.1 modelled every element as an undifferentiated frame, which was accurate for
/// hand-authored FrameForge data but not for imported FrameXML: a <c>&lt;Texture&gt;</c> is
/// not a <c>&lt;Frame&gt;</c>, and a tree that draws both as the same rectangle lies about the
/// file it came from.
/// <para>
/// The member names are deliberately the canonical FrameXML element names (upper-cased), so
/// the project format can round-trip the XML element without a translation table.
/// </para>
/// <para>
/// Every widget inherits the same layout model - size, anchors, parent, visibility - because
/// WoW derives them from a shared <c>LayoutFrame</c> base. Kind only affects how FrameForge
/// presents the element, not how it resolves.
/// </para>
/// </remarks>
public enum FrameKind
{
    /// <summary>A <c>&lt;Frame&gt;</c>: the generic container.</summary>
    FRAME,

    /// <summary>A <c>&lt;Button&gt;</c>.</summary>
    BUTTON,

    /// <summary>A <c>&lt;FontString&gt;</c>. Auto-sizes to its text when no <c>&lt;Size&gt;</c> is given.</summary>
    FONTSTRING,

    /// <summary>A <c>&lt;Texture&gt;</c>.</summary>
    TEXTURE,

    /// <summary>A <c>&lt;StatusBar&gt;</c>. Geometry is a FrameDef; its value semantics are not modelled.</summary>
    STATUSBAR,

    /// <summary>Any other FrameXML widget (CheckButton, Slider, EditBox, ...).</summary>
    OTHER,
}

/// <summary>Canonical FrameXML spellings for <see cref="FrameKind"/> members.</summary>
public static class FrameKinds
{
    /// <summary>The WoW element name a kind was imported from, e.g. <c>FontString</c>.</summary>
    public static string TagName(this FrameKind kind) => kind switch
    {
        FrameKind.FRAME => "Frame",
        FrameKind.BUTTON => "Button",
        FrameKind.FONTSTRING => "FontString",
        FrameKind.TEXTURE => "Texture",
        FrameKind.STATUSBAR => "StatusBar",
        _ => "Element",
    };

    /// <summary>A short, human-facing badge, e.g. <c>font</c> for a FontString.</summary>
    public static string Badge(this FrameKind kind) => kind switch
    {
        FrameKind.FRAME => "frame",
        FrameKind.BUTTON => "button",
        FrameKind.FONTSTRING => "text",
        FrameKind.TEXTURE => "tex",
        FrameKind.STATUSBAR => "bar",
        _ => "elem",
    };
}
