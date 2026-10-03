namespace FrameForge.Core.Import;

/// <summary>Diagnostic codes emitted by <see cref="FrameXmlImporter"/>.</summary>
/// <remarks>
/// Stable strings, not prose: the summary UI filters on these and the tests assert on them, so
/// rewording a message must not break either.
/// </remarks>
public static class FrameXmlDiagnosticCodes
{
    /// <summary>The document is not well-formed XML, or is not a WoW <c>&lt;Ui&gt;</c> document.</summary>
    public const string MalformedXml = "malformed-xml";

    /// <summary>An XML attribute could not be read as a number, so the element used a documented fallback.</summary>
    public const string BadNumber = "bad-number";

    /// <summary>An <c>&lt;Anchor point="..."&gt;</c> named something that is not one of the nine anchor points.</summary>
    public const string UnknownAnchorPoint = "unknown-anchor-point";

    /// <summary>An element FrameForge has no layout model for was found. It is counted, not dropped.</summary>
    public const string UnsupportedElement = "unsupported-element";

    /// <summary>The element declares more than one <c>&lt;Anchor&gt;</c>.</summary>
    public const string MultipleAnchors = "multiple-anchors";

    /// <summary>A second or later anchor could not be solved by the current geometry engine.</summary>
    public const string ExtraAnchorNotSolved = "extra-anchor-not-solved";

    /// <summary>An element has no <c>&lt;Size&gt;</c> and WoW would have sized it from something else.</summary>
    public const string AutomaticSize = "automatic-size";

    /// <summary><c>setAllPoints</c> was found and is modelled as a fill relationship.</summary>
    public const string SetAllPoints = "set-all-points";

    /// <summary>
    /// <c>relativePoint</c> was omitted, so WoW's own substitution rule (use the anchor's own
    /// point) was applied. Informational, not a defect.
    /// </summary>
    public const string RelativePointDefaulted = "relative-point-defaulted";

    /// <summary>An <c>&lt;Offset&gt;&c; used <c>&lt;Scale&gt;</c>, which is a fraction of the target size.</summary>
    public const string ScaleOffsetUnsupported = "scale-offset-unsupported";

    /// <summary>A <c>relativeTo</c> or <c>parent</c> names a frame this document does not define.</summary>
    public const string UnresolvedReference = "unresolved-reference";

    /// <summary>A stand-in frame was synthesized so the subtree could still be inspected.</summary>
    public const string ExternalFramePlaceholder = "external-frame-placeholder";

    /// <summary>An <c>inherits="Template"</c> template could not be resolved.</summary>
    public const string UnresolvedTemplate = "unresolved-template";

    /// <summary>A <c>&lt;Template&gt;</c> definition was found but not applied.</summary>
    public const string TemplateDefinition = "template-definition";

    /// <summary>An <c>&lt;Include&gt;</c> was found and its content was not loaded.</summary>
    public const string IncludeNotLoaded = "include-not-loaded";

    /// <summary>A <c>&lt;Scripts&gt;</c> block exists. FrameForge never executes Lua.</summary>
    public const string ScriptsNotExecuted = "scripts-not-executed";

    /// <summary>A <c>&lt;Script file="..."&gt;</c> declaration exists. FrameForge never executes Lua.</summary>
    public const string ScriptFileNotExecuted = "script-file-not-executed";

    /// <summary>A <c>&lt;Layer level="..."</c> had to be folded into FrameForge's seven strata.</summary>
    public const string LayerStratumMapped = "layer-stratum-mapped";

    /// <summary>
    /// Paint facts were retained in the model but Preview still substitutes a stand-in, because
    /// FrameForge does not decode BLP or TGA textures and does not emulate Blizzard's fonts.
    /// </summary>
    public const string VisualRetainedNotRendered = "visual-retained-not-rendered";

    /// <summary>A <c>&lt;Color&gt;</c> child declared channels outside 0..1.</summary>
    public const string ColorOutOfRange = "color-out-of-range";

    /// <summary>The element had no name, so FrameForge generated a stable internal identity.</summary>
    public const string AnonymousElement = "anonymous-element";

    /// <summary>Two source elements claimed the same effective name.</summary>
    public const string DuplicateName = "duplicate-name";

    /// <summary>A <c>$parent</c> name could not be expanded because its parent was unknown.</summary>
    public const string UnexpandedParentName = "unexpanded-parent-name";

    /// <summary>The document declared no layout elements at all.</summary>
    public const string NoLayoutElements = "no-layout-elements";
}
