using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using FrameForge.Core.Models;

namespace FrameForge.Core.Import;

/// <summary>
/// Reads a World of Warcraft 3.3.5a FrameXML document into a FrameForge project.
/// </summary>
/// <remarks>
/// <para>
/// The importer is deliberately READ-ONLY and lives in Core, not in the editor: it turns a
/// document into a <see cref="Project"/> plus an explicit account of what it could not carry
/// across. It never writes XML. Export and rewriting are a later milestone, and until they
/// exist, no code path in FrameForge can modify the source file.
/// </para>
/// <para>
/// WoW semantics it reproduces, because they are load-bearing for real FrameXML:
/// <list type="bullet">
/// <item><c>relativeTo</c> omitted means "the frame's parent"; no parent means UIParent.</item>
/// <item><c>relativePoint</c> omitted means the SAME point as <c>point</c>, which is WoW's own
/// substitution and is why <c>&lt;Anchor point="TOP"/&gt;</c> centres a label.</item>
/// <item>An element with no <c>parent</c> is parented to UIParent, which in this model is a root
/// frame anchored to the screen.</item>
/// <item><c>$parentFoo</c> expands against the ENCLOSING frame's effective name, so a nested
/// frame's children resolve to the names the game actually creates.</item>
/// <item><c>setAllPoints</c> fills the reference frame and ignores size and offsets.</item>
/// <item>A <c>&lt;Layer level&gt;</c> is a draw-order stratum.</item>
/// </list>
/// </para>
/// <para>
/// What it deliberately does NOT do: execute Lua, resolve <c>inherits</c> templates defined
/// outside the document, load <c>&lt;Include&gt;</c>, or measure Blizzard fonts. Each of those
/// produces a diagnostic, because an import that silently looked complete would be worse than
/// one that admits the gap.
/// </para>
/// </remarks>
public static class FrameXmlImporter
{
    /// <summary>The namespace Blizzard FrameXML declares; elements are also accepted unqualified.</summary>
    public const string WowNamespace = "http://www.blizzard.com/wow/ui/";

    /// <summary>
    /// WoW's implicit root frame. Elements parented or anchored to it are parented to the screen,
    /// so it must never be mistaken for an external frame the file forgot to define.
    /// </summary>
    public const string UiParentName = "UIParent";

    /// <summary>Imports a document already in memory.</summary>
    /// <param name="xml">The FrameXML text.</param>
    /// <param name="sourceFileName">File name used for display and project naming.</param>
    /// <param name="options">Optional overrides; <see cref="FrameXmlImportOptions.Default"/> otherwise.</param>
    public static FrameXmlImportResult Import(
        string xml,
        string? sourceFileName = null,
        FrameXmlImportOptions? options = null)
    {
        var settings = options ?? FrameXmlImportOptions.Default;
        var fileName = FileNameOf(sourceFileName);

        if (string.IsNullOrWhiteSpace(xml))
            return Failure(fileName, "The file is empty.");

        XDocument document;
        try
        {
            // DTD processing stays off and no resolver is used: this parses a file the user
            // chose, and a FrameXML document never needs external entities.
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
            };

            using var reader = XmlReader.Create(new StringReader(xml), readerSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            return Failure(fileName,
                $"Line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message.TrimEnd('.')}.");
        }

        var root = document.Root;
        if (root is null)
            return Failure(fileName, "The document has no root element.");

        if (!string.Equals(root.Name.LocalName, "Ui", StringComparison.Ordinal))
            return Failure(fileName, $"Expected a <Ui> root element but found <{root.Name.LocalName}>.");

        if (root.Name.NamespaceName.Length > 0 && root.Name.NamespaceName != WowNamespace)
        {
            return Failure(fileName,
                $"The root element is in namespace \"{root.Name.NamespaceName}\", which is not a World of Warcraft FrameXML namespace.");
        }

        var builder = new Builder(settings, fileName);
        builder.Run(root);

        if (builder.Elements.Count == 0)
        {
            builder.Report(FrameXmlSeverity.Error, FrameXmlDiagnosticCodes.NoLayoutElements,
                "The document declares no Frame, Button, FontString, Texture or StatusBar elements.");
        }

        return new FrameXmlImportResult
        {
            Project = builder.BuildProject(),
            SourceFileName = fileName,
            Diagnostics = builder.Diagnostics,
            Elements = builder.Elements,
        };
    }

    /// <summary>
    /// Imports a document from disk, READ-ONLY.
    /// </summary>
    /// <remarks>
    /// The path is recorded as informational source metadata only. FrameForge has no XML writer
    /// at all, so importing cannot modify the source; the test suite pins that guarantee against
    /// a copy of the real Native Hunts document.
    /// </remarks>
    public static FrameXmlImportResult ImportFile(string path, FrameXmlImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fileName = FileNameOf(Path.GetFileName(path));
        if (!File.Exists(path))
            return Failure(fileName, "The file no longer exists.");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(fileName, $"The file could not be read: {ex.Message}");
        }

        return Import(text, fileName, options);
    }

    private static FrameXmlImportResult Failure(string fileName, string message) => new()
    {
        SourceFileName = fileName,
        Diagnostics =
        [
            new FrameXmlDiagnostic(
                FrameXmlSeverity.Error,
                FrameXmlDiagnosticCodes.MalformedXml,
                message,
                Element: "document"),
        ],
    };

    private static string FileNameOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Path.GetFileName(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>Maps a FrameXML element name to the widget FrameForge models it as.</summary>
    private static FrameKind? LayoutKind(string localName) => localName switch
    {
        "Frame" => FrameKind.FRAME,
        "Button" => FrameKind.BUTTON,
        "CheckButton" => FrameKind.BUTTON,
        "FontString" => FrameKind.FONTSTRING,
        "Texture" => FrameKind.TEXTURE,
        "StatusBar" => FrameKind.STATUSBAR,
        _ => null,
    };

    /// <summary>
    /// Frames a Layer level onto FrameForge's seven strata.
    /// </summary>
    /// <remarks>
    /// ARTWORK and OVERLAY are not strata in FrameForge's schema, so they fold into MEDIUM and
    /// HIGH. That is a presentation decision, not a claim that the source said MEDIUM, which is
    /// why the importer says so out loud instead of folding silently.
    /// </remarks>
    private static Stratum? MapStratum(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        null or "" or "PARENT" => null,
        "BACKGROUND" => Stratum.BACKGROUND,
        "LOW" => Stratum.LOW,
        "MEDIUM" => Stratum.MEDIUM,
        "HIGH" => Stratum.HIGH,
        "DIALOG" => Stratum.DIALOG,
        "FULLSCREEN" => Stratum.FULLSCREEN,
        "TOOLTIP" => Stratum.TOOLTIP,
        "ARTWORK" => Stratum.MEDIUM,
        "OVERLAY" => Stratum.HIGH,
        _ => Stratum.MEDIUM,
    };

    /// <summary>
    /// Accumulates frames and diagnostics during one import.
    /// </summary>
    /// <remarks>
    /// Element names are matched by LOCAL name, because FrameXML elements live in Blizzard's
    /// namespace and a naive <c>element.Element("Size")</c> would silently find nothing.
    /// <para>
    /// Frames, parents and anchor targets are all held as NAME STRINGS, resolved once the whole
    /// document has been walked. A reference to a frame declared later in the file therefore
    /// resolves correctly instead of being dropped, and every name the document mentions gets
    /// the chance to become a real frame or a clearly-marked placeholder.
    /// </para>
    /// </remarks>
    private sealed class Builder(FrameXmlImportOptions options, string fileName)
    {
        private readonly List<FrameDef> _frames = [];
        private readonly List<FrameXmlDiagnostic> _diagnostics = [];
        private readonly List<FrameXmlElementSummary> _elements = [];
        private readonly Dictionary<string, FrameKind> _kinds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reportedOnce = new(StringComparer.Ordinal);
        private int _anonymousCounter;

        public IReadOnlyList<FrameXmlDiagnostic> Diagnostics => _diagnostics;

        public IReadOnlyList<FrameXmlElementSummary> Elements => _elements;

        /// <summary>Where in the document we are; this decides a child's parent.</summary>
        private readonly record struct Context(string? ParentName, Stratum? Stratum);

        public void Run(XElement root)
        {
            foreach (var child in Children(root))
                Visit(child, new Context(null, null));

            var affectedByPlaceholder = CreatePlaceholders();
            ReportPlaceholderReferences(affectedByPlaceholder);
        }

        public void Report(FrameXmlSeverity severity, string code, string message, string? frame = null, string? element = null) =>
            _diagnostics.Add(new FrameXmlDiagnostic(severity, code, message, frame, element));

        /// <summary>Reports at most once per subject, so 21 identical templates produce one line.</summary>
        private void ReportOnce(string key, FrameXmlSeverity severity, string code, string message, string? frame = null, string? element = null)
        {
            if (_reportedOnce.Add(key))
                Report(severity, code, message, frame, element);
        }

        private static IEnumerable<XElement> Children(XElement element) =>
            element.Elements().Where(e => e.NodeType != System.Xml.XmlNodeType.Comment
                                       && e.NodeType != System.Xml.XmlNodeType.ProcessingInstruction
                                       && e.NodeType != System.Xml.XmlNodeType.Whitespace);

        /// <summary>First child element with this LOCAL name, ignoring the FrameXML namespace.</summary>
        private static XElement? Child(XElement element, string localName) =>
            Children(element).FirstOrDefault(e => e.Name.LocalName == localName);

        private static IReadOnlyList<XElement> ChildrenNamed(XElement element, string localName) =>
            [.. Children(element).Where(e => e.Name.LocalName == localName)];

        /// <summary>Element containers that hold widgets without themselves being widgets.</summary>
        private static bool IsContainer(string localName) => localName is
            "Layers" or "Frames" or "Text" or "TitleText" or "HighlightText" or "StatusBarText" or
            "HitRectInsets" or "Dimensions" or "SpecialFrames";

        /// <summary>
        /// Elements that carry paint, data or script text rather than geometry.
        /// </summary>
        /// <remarks>
        /// Everything here is handled where it matters - <c>Size</c>, <c>Anchor</c>,
        /// <c>Offset</c> and <c>AbsDimension</c> are read by the geometry code - and is otherwise
        /// skipped in silence. Warning about them would bury the findings that matter: a user who
        /// sees forty warnings about <c>&lt;Color&gt;</c> and <c>&lt;OnClick&gt;</c> stops reading the
        /// one warning that says a frame's size could not be resolved.
        /// </remarks>
        private static bool IsNonLayoutData(string localName) => localName is
            "Size" or "Anchors" or "Anchor" or "Offset" or "AbsDimension" or "Scale" or "Dimensions"
            or "Color" or "TexCoords" or "BarTexture" or "BarColor" or "Font" or "FontFile" or "FontHeight"
            or "Highlight" or "HighlightColor" or "Disabled" or "DisabledColor" or "Pushed" or "Normal"
            or "HighlightTexture" or "DisabledTexture" or "PushedTexture" or "NormalTexture"
            or "VertexColor" or "DrawLayer" or "TileMode" or "BlinkTime" or "Left" or "Right"
            or "Top" or "Bottom" or "Middle" or "Attributes" or "NormalTexture"
            or "Sequences" or "Sequence" or "Animations" or "Animation" or "Actions" or "Action"
            or "Condition" or "Load" or "Unload";

        private void Visit(XElement element, Context context)
        {
            var name = element.Name.LocalName;

            switch (name)
            {
                case "Script":
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.ScriptFileNotExecuted,
                        $"<Script file=\"{Attr(element, "file")}\"/> is recorded but not executed. FrameForge never runs Lua, " +
                        "so layout this script performs at runtime is NOT represented in the preview.",
                        context.ParentName,
                        "Script");
                    return;

                case "Include":
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.IncludeNotLoaded,
                        $"<Include file=\"{Attr(element, "file")}\"/> was not read. Templates and frames defined there are not imported.",
                        context.ParentName,
                        "Include");
                    return;

                case "Scripts":
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.ScriptsNotExecuted,
                        "This element declares <Scripts>. FrameForge never executes Lua, so runtime layout it performs " +
                        "(SetPoint, SetSize, Show/Hide) is not reflected here.",
                        context.ParentName,
                        "Scripts");
                    foreach (var script in Children(element))
                        Visit(script, context);
                    return;

                case "Template":
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.TemplateDefinition,
                        "A <Template> definition was found. FrameForge does not apply templates, so elements inheriting it keep " +
                        "only the attributes written on the element itself.",
                        context.ParentName,
                        "Template");
                    return;

                case "Layer":
                    VisitLayer(element, context);
                    return;
            }

            if (LayoutKind(name) is { } kind)
            {
                BuildFrame(element, kind, context);
                return;
            }

            if (IsContainer(name))
            {
                foreach (var child in Children(element))
                    Visit(child, context);
                return;
            }

            // Paint, data and script-handler elements carry no geometry. Skip them quietly; the
            // <Scripts> warning above already accounts for everything inside a handler.
            if (IsNonLayoutData(name) || name.StartsWith("On", StringComparison.Ordinal))
                return;

            // Not a layout widget and not a container FrameForge knows. Report it once by
            // element name so an unfamiliar widget is still visible, then keep descending: real
            // FrameXML nests widgets in wrappers this list does not need to enumerate.
            ReportOnce(
                $"unsupported:{name}",
                FrameXmlSeverity.Warning,
                FrameXmlDiagnosticCodes.UnsupportedElement,
                $"<{name}> is not a FrameForge layout element, so no geometry was read from it.",
                context.ParentName,
                name);

            foreach (var child in Children(element))
                Visit(child, context);
        }

        private void VisitLayer(XElement layer, Context context)
        {
            var level = Attr(layer, "level");
            var mapped = MapStratum(level);

            if (!string.IsNullOrWhiteSpace(level) && !string.Equals(level, "PARENT", StringComparison.OrdinalIgnoreCase))
            {
                var normalized = level.Trim().ToUpperInvariant();
                if (normalized is "ARTWORK" or "OVERLAY")
                {
                    ReportOnce(
                        $"stratum:{normalized}",
                        FrameXmlSeverity.Info,
                        FrameXmlDiagnosticCodes.LayerStratumMapped,
                        $"FrameXML Layer level \"{normalized}\" has no equivalent in FrameForge's seven strata; its contents are " +
                        $"drawn in the {(mapped == Stratum.MEDIUM ? "MEDIUM" : "HIGH")} stratum.",
                        element: "Layer");
                }
                else if (!Enum.TryParse<Stratum>(normalized, ignoreCase: false, out _))
                {
                    ReportOnce(
                        $"stratum:{normalized}",
                        FrameXmlSeverity.Warning,
                        FrameXmlDiagnosticCodes.LayerStratumMapped,
                        $"Unknown Layer level \"{level}\"; the layer was drawn in the MEDIUM stratum.",
                        element: "Layer");
                }
            }

            var next = new Context(context.ParentName, mapped ?? context.Stratum);
            foreach (var child in Children(layer))
                Visit(child, next);
        }

        private void BuildFrame(XElement element, FrameKind kind, Context context)
        {
            var rawName = Attr(element, "name");
            var inherits = Attr(element, "inherits");
            var setAllPoints = ReadBool(element, "setAllPoints") || ReadBool(element, "setAllPointsToParent");

            string name;
            var anonymous = false;
            string? sourceName = null;

            if (string.IsNullOrWhiteSpace(rawName))
            {
                anonymous = true;
                name = NextAnonymousName(kind);
                ReportOnce(
                    "anonymous",
                    FrameXmlSeverity.Info,
                    FrameXmlDiagnosticCodes.AnonymousElement,
                    "Some elements have no name (background textures, unlabelled FontStrings). FrameForge gave each a stable " +
                    "internal identity such as Texture#1 so the tree and its anchors stay addressable. The '#' name space cannot " +
                    "collide with a real WoW frame name.",
                    name,
                    element.Name.LocalName);
            }
            else
            {
                sourceName = rawName;
                name = ExpandParentName(rawName, context.ParentName);

                // A $parent prefix at the top level expands onto UIParent, which in this model is
                // not a frame at all. Renaming keeps the two from colliding.
                if (string.Equals(name, UiParentName, StringComparison.Ordinal))
                {
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.UnexpandedParentName,
                        $"<{element.Name.LocalName} name=\"{rawName}\"> is at the top level, so there is no parent for " +
                        "\"$parent\" to expand onto. FrameForge renamed it; in WoW it would have collided with UIParent.",
                        null,
                        element.Name.LocalName);
                    name = NextAnonymousName(kind);
                }

                if (_kinds.ContainsKey(name))
                {
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.DuplicateName,
                        $"Two elements resolve to the name \"{name}\"; the later one was renamed so names stay unique.",
                        name,
                        element.Name.LocalName);
                    name = NextAnonymousName(kind);
                }
            }

            // An explicit parent attribute REPLACES the XML nesting parent, which is how
            // NativeHuntsFrame attaches itself to Blizzard's LFDParentFrame. parent="UIParent"
            // is an explicit request for the screen, so it is not confused with "no parent".
            var parent = Attr(element, "parent") is { Length: > 0 } rawParent
                ? NormalizeReference(rawParent, context.ParentName)
                : context.ParentName;

            var (width, height, hasSize) = ReadSize(element, name);
            var anchors = ReadAnchors(element, name, context.ParentName);

            var frame = new FrameDef
            {
                Name = name,
                Parent = parent,
                Width = width,
                Height = height,
                Point = anchors.Count > 0 ? anchors[0].Point : ProjectFactory.DefaultPoint,
                RelativeTo = anchors.Count > 0 ? anchors[0].RelativeTo : null,
                RelativePoint = anchors.Count > 0 ? anchors[0].RelativePoint : ProjectFactory.DefaultPoint,
                OffsetX = anchors.Count > 0 ? anchors[0].OffsetX : 0,
                OffsetY = anchors.Count > 0 ? anchors[0].OffsetY : 0,
                ExtraAnchors = anchors.Count > 1 ? [.. anchors.Skip(1)] : [],
                Visible = !ReadBool(element, "hidden"),
                Stratum = context.Stratum,
                Kind = kind,
                SetAllPoints = setAllPoints,
                SourceName = sourceName,
                Anonymous = anonymous,
                Inherits = inherits,
            };

            var support = AssessSupport(element, name, kind, inherits, hasSize, setAllPoints);

            _frames.Add(frame);
            _kinds[name] = kind;
            _elements.Add(new FrameXmlElementSummary(name, kind, support));

            foreach (var child in Children(element))
                Visit(child, new Context(name, context.Stratum));
        }

        /// <summary>
        /// Decides how completely one element's layout came through, and says so.
        /// </summary>
        /// <remarks>
        /// Full/Partial/Unsupported describes the GEOMETRY only: does the rectangle FrameForge draws
        /// match the rectangle WoW would build? Everything the importer could not carry across that
        /// is not geometric - paint, text, script behaviour - is reported as a diagnostic and does
        /// not change this count, because downgrading every element that has a click handler would
        /// make the number meaningless.
        /// </remarks>
        private FrameXmlSupport AssessSupport(
            XElement element,
            string name,
            FrameKind kind,
            string? inherits,
            bool hasSize,
            bool setAllPoints)
        {
            var support = FrameXmlSupport.Full;
            var sizeKnown = hasSize || setAllPoints;

            if (setAllPoints)
            {
                ReportOnce(
                    "setAllPoints",
                    FrameXmlSeverity.Info,
                    FrameXmlDiagnosticCodes.SetAllPoints,
                    "setAllPoints is modelled as \"fills the reference frame\": its size and offsets are ignored, exactly as in WoW.",
                    name,
                    "setAllPoints");
            }
            else if (!hasSize)
            {
                // A Frame without <Size> really is 0 x 0 in WoW, so only widgets whose size WoW
                // derives for us are a gap here: FontStrings measure their text, and a templated
                // widget inherits its size from a template this file does not define.
                if (kind == FrameKind.FONTSTRING)
                {
                    var sizedBy = inherits is { Length: > 0 }
                        ? $"its text and the inherited font template \"{inherits}\""
                        : "its text and the inherited Blizzard font";

                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.AutomaticSize,
                        $"No <Size> and no setAllPoints. WoW sizes this FontString from {sizedBy}, which FrameForge cannot " +
                        "measure, so it is shown at 0 x 0 at its anchor point.",
                        name,
                        "Size");
                    support = FrameXmlSupport.Partial;
                }
                else if (inherits is { Length: > 0 })
                {
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.AutomaticSize,
                        $"No <Size>. WoW takes the size from the inherited template \"{inherits}\", which was not resolved, so this " +
                        "element is shown at 0 x 0 at its anchor point.",
                        name,
                        "Size");
                    support = FrameXmlSupport.Partial;
                }
            }

            if (inherits is { Length: > 0} && sizeKnown)
            {
                if (kind == FrameKind.FONTSTRING)
                {
                    // Font templates change text metrics, not geometry. FrameForge draws text as a
                    // labelled box, so an explicitly sized FontString's rectangle is still exact.
                    ReportOnce(
                        "fonttemplate",
                        FrameXmlSeverity.Info,
                        FrameXmlDiagnosticCodes.UnresolvedTemplate,
                        "Font templates (GameFont*) affect text metrics only. FrameForge renders FontStrings as labelled boxes, so " +
                        "their geometry is unaffected; the inherited style was NOT applied.",
                        name,
                        "inherits");
                }
                else
                {
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.UnresolvedTemplate,
                        $"Inherits template \"{inherits}\", which is defined outside this file and was not resolved. Inherited size, " +
                        "offsets and child layers are NOT represented; only the attributes written on this element are.",
                        name,
                        "inherits");
                    support = FrameXmlSupport.Partial;
                }
            }

            ReportIgnoredPaint(element, name);

            if (kind == FrameKind.STATUSBAR)
            {
                ReportOnce(
                    "statusbar",
                    FrameXmlSeverity.Info,
                    FrameXmlDiagnosticCodes.StatusBarValueIgnored,
                    "StatusBar values (minValue/maxValue/defaultValue, BarTexture, BarColor) are data, not layout. FrameForge shows " +
                    "the bar's rectangle only.",
                    name,
                    "StatusBar");
            }

            return support;
        }

        /// <summary>Records paint-only attributes once, so their absence from the model is visible.</summary>
        private void ReportIgnoredPaint(XElement element, string name)
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;

                if (attribute.Name.LocalName is "file" or "texCoords" or "text" or "justifyH" or "justifyV"
                    or "alpha" or "normalizeTexCoords" or "scale" or "blendMode" or "drawLayer" or "id")
                {
                    ReportOnce(
                        "paint",
                        FrameXmlSeverity.Info,
                        FrameXmlDiagnosticCodes.PaintAttributeIgnored,
                        "Paint-only attributes (file, TexCoords, Color, text, justifyH/V, alpha, id) do not affect layout. FrameForge " +
                        "draws wireframes, not Blizzard artwork or fonts.",
                        name,
                        element.Name.LocalName);
                    return;
                }
            }
        }

        private (double Width, double Height, bool HasSize) ReadSize(XElement element, string name)
        {
            var size = Child(element, "Size");
            if (size is null)
                return (0, 0, false);

            return (ReadNumber(size, "x", name, "Size"), ReadNumber(size, "y", name, "Size"), true);
        }

        /// <summary>
        /// Reads every <c>&lt;Anchor&gt;</c>, in document order, keeping all of them.
        /// </summary>
        /// <param name="element">The widget being built.</param>
        /// <param name="name">Its resolved name, used in diagnostics.</param>
        /// <param name="enclosingName">
        /// Its effective name, which is what a <c>$parentFoo</c> reference resolves against.
        /// </param>
        private List<FrameAnchor> ReadAnchors(XElement element, string name, string? enclosingName)
        {
            var anchors = new List<FrameAnchor>();
            var container = Child(element, "Anchors");
            if (container is null)
                return anchors;

            var defaultedRelativePoint = false;

            foreach (var anchorElement in ChildrenNamed(container, "Anchor"))
            {
                var pointText = Attr(anchorElement, "point");
                var point = ProjectFactory.DefaultPoint;

                if (string.IsNullOrWhiteSpace(pointText))
                {
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.UnknownAnchorPoint,
                        $"<Anchor> on \"{name}\" has no point attribute; {ProjectFactory.DefaultPoint} was used.",
                        name,
                        "Anchor");
                }
                else if (AnchorPoints.TryParse(pointText.Trim().ToUpperInvariant(), out var parsedPoint))
                {
                    point = parsedPoint;
                }
                else
                {
                    Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.UnknownAnchorPoint,
                        $"Anchor point \"{pointText}\" is not one of the nine anchor points; {ProjectFactory.DefaultPoint} was used.",
                        name,
                        "Anchor");
                }

                // WoW's rule: an omitted relativePoint becomes the SAME point. Storing the
                // substituted value keeps the imported model equal to the effective game state
                // instead of a guess that happens to render the same.
                var relativePointText = Attr(anchorElement, "relativePoint");
                var relativePoint = point;
                if (!string.IsNullOrWhiteSpace(relativePointText))
                {
                    if (AnchorPoints.TryParse(relativePointText.Trim().ToUpperInvariant(), out var parsed))
                    {
                        relativePoint = parsed;
                    }
                    else
                    {
                        Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.UnknownAnchorPoint,
                            $"relativePoint \"{relativePointText}\" is not one of the nine anchor points; {point} was used.",
                            name,
                            "Anchor");
                    }
                }
                else
                {
                    defaultedRelativePoint = true;
                }

                var (offsetX, offsetY) = ReadOffset(anchorElement, name);

                anchors.Add(FrameAnchor.Create(
                    point,
                    NormalizeReference(Attr(anchorElement, "relativeTo"), enclosingName),
                    relativePoint,
                    offsetX,
                    offsetY));
            }

            if (defaultedRelativePoint)
            {
                ReportOnce(
                    "relativePointDefault",
                    FrameXmlSeverity.Info,
                    FrameXmlDiagnosticCodes.RelativePointDefaulted,
                    "Some <Anchor> elements omit relativePoint. WoW substitutes the anchor's own point, and FrameForge stores that " +
                    "substituted value, so the inspector shows the effective game behaviour rather than a silent default.",
                    element: "Anchor");
            }

            if (anchors.Count > 1)
            {
                Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.MultipleAnchors,
                    $"This element declares {anchors.Count} anchors. FrameForge KEEPS all of them and solves anchors that share a " +
                    "target point (the usual top-left / top-right stretch), but it cannot solve a frame pinned to two different " +
                    "points of two different targets. Anchor 1 is editable in the inspector; the rest are shown read-only.",
                    name,
                    "Anchors");
            }

            return anchors;
        }

        private (double X, double Y) ReadOffset(XElement anchor, string name)
        {
            var offset = Child(anchor, "Offset");
            if (offset is null)
                return (0, 0);

            var absolute = Child(offset, "AbsDimension");
            if (absolute is not null)
                return (ReadNumber(absolute, "x", name, "AbsDimension"), ReadNumber(absolute, "y", name, "AbsDimension"));

            if (Child(offset, "Scale") is not null)
            {
                Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.ScaleOffsetUnsupported,
                    "<Offset><Scale> is a fraction of the target's size. FrameForge models absolute offsets only, so this anchor " +
                    "was placed at 0,0. Resize the target frame to rescale it.",
                    name,
                    "Scale");
            }

            return (0, 0);
        }

        private double ReadNumber(XElement element, string attribute, string name, string elementName)
        {
            var text = Attr(element, attribute);
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                double.IsFinite(value))
            {
                return value;
            }

            Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.BadNumber,
                $"<{elementName} {attribute}=\"{text}\"> is not a finite number; 0 was used.",
                name,
                elementName);
            return 0;
        }

        private static bool ReadBool(XElement element, string attribute)
        {
            var text = Attr(element, attribute);
            return text is not null && (text == "true" || text == "1");
        }

        private static string? Attr(XElement element, string name)
        {
            var attribute = element.Attribute(name) ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == name);
            var value = attribute?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// Resolves <c>$parentFoo</c> against the ENCLOSING frame's effective name.
        /// </summary>
        /// <remarks>
        /// A parent of null means the element is parented to UIParent in WoW, so a <c>$parent</c>
        /// prefix expands to "UIParent". The expansion is deliberately not done against the raw
        /// attribute value: a <c>$parent</c> chain must resolve to the name the game actually
        /// creates, which is the enclosing frame's EFFECTIVE (already-expanded) name.
        /// </remarks>
        private string ExpandParentName(string raw, string? enclosingName)
        {
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith("$parent", StringComparison.Ordinal))
                return trimmed;

            var baseName = enclosingName ?? UiParentName;
            return baseName + trimmed["$parent".Length..];
        }

        /// <summary>
        /// Turns a raw <c>parent</c> or <c>relativeTo</c> attribute into the model's own reference:
        /// <c>$parentFoo</c> expanded, and UIParent folded onto the screen.
        /// </summary>
        /// <remarks>
        /// Returning null is meaningful and is what "the screen" means throughout the model.
        /// NativeHuntsFrame's state panels anchor to <c>relativeTo="$parentIdentity"</c> and its
        /// initializer to <c>parent="UIParent"</c>; neither is an external frame the file forgot to
        /// define, so neither may become a placeholder.
        /// </remarks>
        private string? NormalizeReference(string? raw, string? enclosingName)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var expanded = ExpandParentName(raw, enclosingName);
            return string.Equals(expanded, UiParentName, StringComparison.Ordinal) ? null : expanded;
        }

        private string NextAnonymousName(FrameKind kind)
        {
            string candidate;
            do
            {
                candidate = $"{kind.TagName()}#{++_anonymousCounter}";
            }
            while (_kinds.ContainsKey(candidate));

            return candidate;
        }

        /// <summary>
        /// Inserts a marked stand-in for every frame the document references but does not define.
        /// </summary>
        /// <remarks>
        /// The alternative - leaving the reference dangling - is what would make a whole subtree
        /// vanish from the preview, because a frame with an unknown anchor target cannot be
        /// positioned. A placeholder keeps the geometry inspectable and is flagged three ways:
        /// <see cref="FrameDef.Placeholder"/> in the model, a warning here, and distinct canvas
        /// and tree rendering in the editor.
        /// </remarks>
        private HashSet<string> CreatePlaceholders()
        {
            var affected = new HashSet<string>(StringComparer.Ordinal);
            if (!options.CreateExternalFramePlaceholders)
                return affected;

            var declared = new HashSet<string>(_kinds.Keys, StringComparer.Ordinal);
            var missing = new List<string>();

            foreach (var frame in _frames)
            {
                foreach (var reference in References(frame))
                {
                    if (reference is not null && !declared.Contains(reference) && !missing.Contains(reference, StringComparer.Ordinal))
                        missing.Add(reference);
                }
            }

            for (var i = 0; i < missing.Count; i++)
            {
                var name = missing[i];

                _frames.Add(new FrameDef
                {
                    Name = name,
                    Width = options.ExternalFrameSize.Width,
                    Height = options.ExternalFrameSize.Height,
                    Point = AnchorPoint.CENTER,
                    RelativePoint = AnchorPoint.CENTER,
                    OffsetX = options.ExternalFramePosition.X + i * 24,
                    OffsetY = options.ExternalFramePosition.Y - i * 24,
                    Stratum = Stratum.BACKGROUND,
                    Kind = FrameKind.FRAME,
                    SourceName = name,
                    Placeholder = true,
                });

                _kinds[name] = FrameKind.FRAME;

                Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.ExternalFramePlaceholder,
                    $"\"{name}\" is referenced but never defined in this file. FrameForge inserted a marked " +
                    $"{Num(options.ExternalFrameSize.Width)} x {Num(options.ExternalFrameSize.Height)} placeholder so its subtree stays " +
                    "inspectable. The real size is decided at runtime by Blizzard's XML or by Lua, so anything anchored to this frame " +
                    "is APPROXIMATE. Resize the placeholder to explore.",
                    name,
                    "placeholder");
            }

            // Anything that now anchors to a placeholder is only partly known.
            var byName = new Dictionary<string, FrameDef>(StringComparer.Ordinal);
            foreach (var frame in _frames)
                byName[frame.Name] = frame;

            var placeholderNames = new HashSet<string>(
                byName.Values.Where(f => f.Placeholder).Select(f => f.Name),
                StringComparer.Ordinal);

            for (var i = 0; i < _elements.Count; i++)
            {
                var summary = _elements[i];
                if (byName.TryGetValue(summary.Frame, out var frame) &&
                    !frame.Placeholder &&
                    References(frame).Any(r => r is not null && placeholderNames.Contains(r)))
                {
                    _elements[i] = summary with { Support = FrameXmlSupport.Partial };
                    affected.Add(summary.Frame);
                }
            }

            return affected;
        }

        private void ReportPlaceholderReferences(HashSet<string> affected)
        {
            if (affected.Count == 0)
                return;

            Report(FrameXmlSeverity.Warning, FrameXmlDiagnosticCodes.UnresolvedReference,
                $"{affected.Count} element(s) anchor to a placeholder stand-in instead of to a frame the source defines: " +
                $"{string.Join(", ", affected.Order(StringComparer.Ordinal))}. Their absolute position depends on a size that only " +
                "exists at runtime.",
                element: "relativeTo");
        }

        private static IEnumerable<string?> References(FrameDef frame)
        {
            foreach (var anchor in frame.AllAnchors)
                yield return anchor.RelativeTo ?? frame.Parent;
        }

        public Project BuildProject()
        {
            var name = fileName.Length == 0
                ? "Imported FrameXML"
                : Path.GetFileNameWithoutExtension(fileName);

            return new Project
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Imported FrameXML" : name,
                Screen = options.Screen,
                Frames = _frames.ToArray(),
                Source = new ProjectSource
                {
                    Type = SourceTypes.WowFrameXml,
                    FileName = fileName.Length == 0 ? null : fileName,
                    ReadOnly = true,
                },
            };
        }

        private static string Num(double value) => value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
