using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;

namespace FrameForge.Desktop.Services;

public sealed record V2DungeonFinderStarterResult(UiDocument? Document, IReadOnlyList<string> Errors)
{
    public bool Success => Document is not null && Errors.Count == 0;
}

public sealed record V2DungeonFinderTemplateSource(string LogicalPath, string PhysicalPath);

/// <summary>
/// Narrow, read-only ingestion for the stock 3.3.5a Dungeon Finder definition. It intentionally
/// creates V2 semantic nodes directly: no V1 Project, projection, or V1 layout resolver participates.
/// This is not a general-purpose FrameXML importer.
/// </summary>
public static class V2DungeonFinderStarter
{
    private static readonly XNamespace ReferenceMetadataNamespace = "urn:frameforge:reference-ingestion";
    private static readonly XName ReferenceSourceAttribute = ReferenceMetadataNamespace + "source";
    private static readonly XName ReferenceTemplateAttribute = ReferenceMetadataNamespace + "template";
    public const string SourceIdentity = "wow-3.3.5a-12340:Interface/FrameXML/LFDFrame.xml:LFDParentFrame";
    public const string SourcePath = @"Interface\FrameXML\LFDFrame.xml";
    public static IReadOnlyList<string> TemplateSourcePaths { get; } =
    [
        @"Interface\FrameXML\LFGFrame.xml",
        @"Interface\FrameXML\UIPanelTemplates.xml",
        @"Interface\FrameXML\UIDropDownMenuTemplates.xml",
        @"Interface\FrameXML\MoneyFrame.xml",
        @"Interface\FrameXML\ItemButtonTemplate.xml",
    ];
    private static readonly HashSet<string> NodeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Frame", "Button", "CheckButton", "StatusBar", "Texture", "FontString", "EditBox",
        "ScrollFrame", "Slider", "MessageFrame", "ScrollingMessageFrame", "Cooldown", "Model",
        "PlayerModel", "DressUpModel", "TabardModel",
    };

    public static V2DungeonFinderStarterResult Create(string xmlPath,
        IEnumerable<V2DungeonFinderTemplateSource>? templateSources = null)
    {
        if (!File.Exists(xmlPath)) return new(null, [$"The materialized client source does not exist: {xmlPath}"]);
        XDocument xml;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(xmlPath, settings);
            xml = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return new(null, [$"The client LFDFrame.xml could not be read: {ex.Message}"]);
        }

        var stockElement = xml.Root?.Elements().FirstOrDefault(element =>
            IsNode(element) && string.Equals((string?)element.Attribute("name"), "LFDParentFrame", StringComparison.Ordinal));
        if (stockElement is null) return new(null, ["The client LFDFrame.xml did not define LFDParentFrame."]);

        var templates = TemplateExpander.Create(xml, templateSources ?? [], out var templateErrors);
        if (templateErrors.Count > 0) return new(null, templateErrors);

        var rawNodes = new List<RawNode>();
        ParseNode(stockElement, null, null, rawNodes, templates);
        if (rawNodes.Count == 0)
            return new(null, ["The client Dungeon Finder definition contained no supported visual elements."]);

        var document = UiDocumentFactory.Create("DungeonFinderDesignRoot", "ModuleUiHost", 1024, 768);
        var root = document.CompositionRoots[0];
        var referenceId = SemanticId.New();
        var wrapperId = SemanticId.New();
        var lockGroupId = SemanticId.New();
        foreach (var raw in rawNodes) raw.Id = SemanticId.New();
        var named = rawNodes.Where(item => item.RuntimeName is not null)
            .GroupBy(item => item.RuntimeName!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
        var externals = new HashSet<string>(StringComparer.Ordinal);
        NodeEditorMetadata Metadata(string source = SourcePath) => new()
        {
            ReferenceOnly = true, ReferenceCompositionId = referenceId, ReferenceSource = source,
        };

        var stockRoot = rawNodes[0];
        var wrapperWidth = stockRoot.Width is > 0 ? stockRoot.Width.Value : 355;
        var wrapperHeight = stockRoot.Height is > 0 ? stockRoot.Height.Value : 440;
        var wrapper = new UiNode
        {
            Id = wrapperId,
            Kind = UiNodeKind.Frame,
            DisplayLabel = "Blizzard Dungeon Finder Reference",
            Owner = OwnerReference.Root(root.Id),
            Children = [stockRoot.Id],
            Anchors = [Center(AnchorTarget.Root())],
            AuthoredProperties = new AuthoredProperties
            {
                Frame = new FrameProperties
                {
                    Width = wrapperWidth, Height = wrapperHeight, Visible = true, Strata = FrameStrata.Background,
                },
            },
            Editor = Metadata() with { Collapsed = true },
        };

        var nodes = new List<UiNode> { wrapper };
        foreach (var raw in rawNodes)
        {
            var owner = raw.Parent is null ? OwnerReference.Node(wrapperId) : OwnerReference.Node(raw.Parent.Id);
            IReadOnlyList<UiAnchor> anchors = raw.Anchors.Count == 0
                ? [Center(AnchorTarget.Parent())]
                : raw.Anchors.Select(anchor => ConvertAnchor(anchor, raw, named, externals)).ToArray();
            if (raw.SetAllPoints)
            {
                anchors =
                [
                    new UiAnchor { Point = AnchorPoint.TOPLEFT, RelativePoint = AnchorPoint.TOPLEFT, Target = AnchorTarget.Parent() },
                    new UiAnchor { Point = AnchorPoint.BOTTOMRIGHT, RelativePoint = AnchorPoint.BOTTOMRIGHT, Target = AnchorTarget.Parent() },
                ];
            }
            nodes.Add(new UiNode
            {
                Id = raw.Id,
                Kind = raw.Kind,
                RuntimeName = raw.RuntimeName,
                DisplayLabel = raw.DisplayLabel,
                Owner = owner,
                Children = raw.Children.Select(item => item.Id).ToArray(),
                Anchors = anchors,
                AuthoredProperties = Properties(raw),
                Editor = Metadata(raw.ReferenceSource) with
                {
                    ReferenceTemplate = raw.TemplateOrigin ?? raw.Inherits,
                    ReferenceAutoWidth = raw.ReferenceAutoWidth,
                    ReferenceAutoHeight = raw.ReferenceAutoHeight,
                },
            });
        }

        var members = nodes.Select(node => node.Id).ToArray();
        var originals = nodes.Select(node => node with { Editor = null }).ToArray();
        document = document with
        {
            CompositionRoots = [root with { Children = [wrapperId] }],
            Nodes = nodes,
            ExternalReferences =
            [
                .. document.ExternalReferences,
                .. externals.Where(name => document.ExternalReferences.All(existing => existing.GlobalName != name))
                    .Select(name => new ExternalReference
                    {
                        GlobalName = name, ExpectedSource = SourcePath,
                        Description = "External anchor referenced by the stock Dungeon Finder definition.",
                    }),
            ],
            Editor = new DocumentEditorMetadata
            {
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DocumentEditorMetadata.ProjectNameKey] = "Dungeon Finder UI",
                },
                Groups =
                [
                    new SemanticEditorGroup
                    {
                        Id = lockGroupId, Name = "Blizzard Dungeon Finder Reference", Members = members, Locked = true,
                    },
                ],
                ReferenceCompositions =
                [
                    new SemanticReferenceComposition
                    {
                        Id = referenceId, Name = "Blizzard Dungeon Finder Reference", SourceIdentity = SourceIdentity,
                        RootNodeId = wrapperId, LockGroupId = lockGroupId, Members = members, OriginalNodes = originals,
                    },
                ],
                PreviewStates =
                [
                    // Blizzard authors LFDParentFrame hidden and shows it from Lua. Preserve that
                    // authored fact on the reference node, but make the editor's default
                    // presentation explicitly model the opened Dungeon Finder. Presentation
                    // overrides are transient and never enter export.
                    new SemanticPreviewState
                    {
                        Id = SemanticId.New(), Name = "Dungeon Finder open",
                        Overrides =
                        [
                            new SemanticPreviewOverride { NodeId = stockRoot.Id, Visible = true },
                        ],
                    },
                ],
            },
        };
        var diagnostics = UiDocumentValidator.Validate(document);
        var labels = document.Nodes.ToDictionary(node => node.Id, node => node.DisplayLabel);
        var errors = diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
            .Select(item => $"{item.Code}: {item.Message}" +
                            (item.NodeId is { } id && labels.TryGetValue(id, out var label) ? $" Node: {label}." : string.Empty))
            .ToArray();
        return errors.Length > 0 ? new(null, errors) : new(document, []);
    }

    private static void ParseNode(XElement element, RawNode? parent, RegionDrawLayer? drawLayer,
        ICollection<RawNode> output, TemplateExpander templates)
    {
        element = templates.Expand(element);
        var declaredName = (string?)element.Attribute("name");
        var runtimeName = ExpandName(declaredName, parent?.RuntimeName);
        var ordinal = output.Count + 1;
        var kind = KindOf(element.Name.LocalName);
        var width = Dimension(element, "x");
        var height = Dimension(element, "y");
        var raw = new RawNode
        {
            Parent = parent,
            Kind = kind,
            RuntimeName = string.IsNullOrWhiteSpace(runtimeName) ? null : runtimeName,
            DisplayLabel = string.IsNullOrWhiteSpace(runtimeName) ? $"{element.Name.LocalName} {ordinal}" : runtimeName,
            Inherits = BlankToNull((string?)element.Attribute("inherits")),
            ReferenceSource = BlankToNull((string?)element.Attribute(ReferenceSourceAttribute)) ?? SourcePath,
            TemplateOrigin = BlankToNull((string?)element.Attribute(ReferenceTemplateAttribute)),
            Width = width.Value,
            Height = height.Value,
            ReferenceAutoWidth = width.ExplicitZero,
            ReferenceAutoHeight = height.ExplicitZero,
            Visible = !BoolAttribute(element, "hidden"),
            SetAllPoints = BoolAttribute(element, "setAllPoints"),
            Strata = ParseStrata((string?)element.Attribute("frameStrata")),
            Level = IntAttribute(element, "frameLevel"),
            DrawLayer = drawLayer,
            TextureReference = BlankToNull((string?)element.Attribute("file")),
            Text = BlankToNull((string?)element.Attribute("text")),
            JustifyH = BlankToNull((string?)element.Attribute("justifyH")),
            JustifyV = BlankToNull((string?)element.Attribute("justifyV")),
        };
        raw.Anchors.AddRange(ParseAnchors(element));
        raw.TexCoords = ParseTexCoords(element);
        raw.Tint = ApplyAlpha(ParseColor(element), DoubleAttribute(element, "alpha"));
        if (raw.Kind == UiNodeKind.StatusBar)
        {
            raw.Minimum = DoubleAttribute(element, "minValue");
            raw.Maximum = DoubleAttribute(element, "maxValue");
            raw.Value = DoubleAttribute(element, "defaultValue");
            var barTexture = Child(element, "BarTexture");
            raw.StatusTextureReference = BlankToNull((string?)barTexture?.Attribute("file"));
            raw.StatusFill = ParseColor(Child(element, "BarColor"));
        }
        output.Add(raw);
        parent?.Children.Add(raw);

        foreach (var layer in element.Elements().Where(item => Local(item, "Layers"))
                     .SelectMany(item => item.Elements().Where(child => Local(child, "Layer"))))
        {
            var layerKind = ParseLayer((string?)layer.Attribute("level"));
            foreach (var child in layer.Elements().Where(IsNode)) ParseNode(child, raw, layerKind, output, templates);
        }
        foreach (var frames in element.Elements().Where(item => Local(item, "Frames")))
            foreach (var child in frames.Elements().Where(IsNode)) ParseNode(child, raw, null, output, templates);
        foreach (var scrollChild in element.Elements().Where(item => Local(item, "ScrollChild")))
            foreach (var child in scrollChild.Elements().Where(IsNode)) ParseNode(child, raw, null, output, templates);
        foreach (var child in element.Elements().Where(IsNode)) ParseNode(child, raw, null, output, templates);
    }

    private static AuthoredProperties Properties(RawNode raw)
    {
        if (raw.Kind == UiNodeKind.Texture)
            return new AuthoredProperties
            {
                Region = new RegionProperties
                {
                    Width = raw.Width, Height = raw.Height, DrawLayer = raw.DrawLayer,
                    Tint = raw.Tint, Visible = raw.Visible,
                },
                Texture = new TextureProperties { TextureReference = raw.TextureReference, TexCoords = raw.TexCoords },
            };
        if (raw.Kind == UiNodeKind.FontString)
            return new AuthoredProperties
            {
                Region = new RegionProperties
                {
                    Width = raw.Width, Height = raw.Height, DrawLayer = raw.DrawLayer,
                    Tint = raw.Tint, Visible = raw.Visible,
                },
                FontString = new FontStringProperties
                {
                    Text = raw.Text, FontReference = raw.Inherits,
                    JustifyH = raw.JustifyH, JustifyV = raw.JustifyV,
                },
            };
        var properties = new AuthoredProperties
        {
            Frame = new FrameProperties
            {
                Width = raw.Width, Height = raw.Height, Visible = raw.Visible, Strata = raw.Strata, Level = raw.Level,
            },
        };
        if (raw.Kind == UiNodeKind.Button)
            properties = properties with { Button = new ButtonProperties { Enabled = true, Text = raw.Text } };
        if (raw.Kind == UiNodeKind.StatusBar)
            properties = properties with
            {
                StatusBar = new StatusBarProperties
                {
                    Minimum = raw.Minimum, Maximum = raw.Maximum, Value = raw.Value,
                    TextureReference = raw.StatusTextureReference, FillColor = raw.StatusFill,
                },
            };
        return properties;
    }

    private static UiAnchor ConvertAnchor(RawAnchor anchor, RawNode owner,
        IReadOnlyDictionary<string, SemanticId> named, ISet<string> externals)
    {
        AnchorTarget target;
        if (string.IsNullOrWhiteSpace(anchor.RelativeTo)) target = AnchorTarget.Parent();
        else if (string.Equals(anchor.RelativeTo, "UIParent", StringComparison.Ordinal)) target = AnchorTarget.Root();
        else if (named.TryGetValue(ExpandName(anchor.RelativeTo, owner.Parent?.RuntimeName) ?? anchor.RelativeTo, out var id))
            target = AnchorTarget.Local(id);
        else
        {
            externals.Add(anchor.RelativeTo);
            target = AnchorTarget.External(anchor.RelativeTo);
        }
        return new UiAnchor
        {
            Point = anchor.Point, RelativePoint = anchor.RelativePoint, Target = target,
            OffsetX = anchor.OffsetX, OffsetY = anchor.OffsetY,
        };
    }

    private static IReadOnlyList<RawAnchor> ParseAnchors(XElement element)
    {
        var anchors = Child(element, "Anchors")?.Elements().Where(item => Local(item, "Anchor")) ?? [];
        return anchors.Select(anchor =>
        {
            var offset = Child(anchor, "Offset");
            var absolute = offset is null ? Child(anchor, "AbsDimension") : Child(offset, "AbsDimension");
            return new RawAnchor(
                ParsePoint((string?)anchor.Attribute("point")),
                BlankToNull((string?)anchor.Attribute("relativeTo")),
                ParsePoint((string?)anchor.Attribute("relativePoint") ?? (string?)anchor.Attribute("point")),
                DoubleAttribute(anchor, "x") ?? DoubleAttribute(offset, "x") ?? DoubleAttribute(absolute, "x") ?? 0,
                DoubleAttribute(anchor, "y") ?? DoubleAttribute(offset, "y") ?? DoubleAttribute(absolute, "y") ?? 0);
        }).ToArray();
    }

    private static SourceDimension Dimension(XElement element, string axis)
    {
        var size = Child(element, "Size");
        var absolute = size is null ? null : Child(size, "AbsDimension");
        var value = DoubleAttribute(size, axis) ?? DoubleAttribute(absolute, axis);
        var explicitZero = value == 0;
        // In Blizzard reference XML, zero on either axis is the native sentinel for an
        // anchor-, font-, or runtime-derived extent. It is not a positive authored V2 size.
        return new SourceDimension(explicitZero ? null : value, explicitZero);
    }

    private static UiTexCoords? ParseTexCoords(XElement element)
    {
        var coords = Child(element, "TexCoords");
        var value = coords is null ? null : new UiTexCoords(
            DoubleAttribute(coords, "left") ?? 0, DoubleAttribute(coords, "right") ?? 1,
            DoubleAttribute(coords, "top") ?? 0, DoubleAttribute(coords, "bottom") ?? 1);
        return value?.IsValid == true && value != new UiTexCoords(0, 1, 0, 1) ? value : null;
    }

    private static UiColor? ParseColor(XElement? element)
    {
        var color = element is null ? null : Child(element, "Color") ?? (Local(element, "Color") || Local(element, "BarColor") ? element : null);
        if (color is null) return null;
        return new UiColor(DoubleAttribute(color, "r") ?? 1, DoubleAttribute(color, "g") ?? 1,
            DoubleAttribute(color, "b") ?? 1, DoubleAttribute(color, "a") ?? 1);
    }

    private static UiColor? ApplyAlpha(UiColor? color, double? alpha)
    {
        if (alpha is null) return color;
        var value = Math.Clamp(alpha.Value, 0, 1);
        return color is null
            ? new UiColor(1, 1, 1, value)
            : color with { Alpha = color.Alpha * value };
    }

    private static UiAnchor Center(AnchorTarget target) => new()
    {
        Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER, Target = target,
    };

    private static bool IsNode(XElement element) => NodeTags.Contains(element.Name.LocalName);
    private static bool Local(XElement? element, string name) =>
        element is not null && string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);
    private static XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(item => Local(item, name));
    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string? ExpandName(string? value, string? parent) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace("$parent", parent ?? string.Empty, StringComparison.Ordinal);
    private static bool BoolAttribute(XElement element, string name) =>
        bool.TryParse((string?)element.Attribute(name), out var value) && value;
    private static int? IntAttribute(XElement element, string name) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static double? DoubleAttribute(XElement? element, string name) =>
        double.TryParse((string?)element?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : null;
    private static AnchorPoint ParsePoint(string? value) =>
        Enum.TryParse<AnchorPoint>(value, true, out var point) ? point : AnchorPoint.CENTER;
    private static RegionDrawLayer ParseLayer(string? value) =>
        Enum.TryParse<RegionDrawLayer>(value, true, out var layer) ? layer : RegionDrawLayer.Artwork;
    private static FrameStrata? ParseStrata(string? value) =>
        Enum.TryParse<FrameStrata>(value, true, out var strata) ? strata : null;
    private static UiNodeKind KindOf(string tag) => tag.ToUpperInvariant() switch
    {
        "TEXTURE" => UiNodeKind.Texture,
        "FONTSTRING" => UiNodeKind.FontString,
        "BUTTON" or "CHECKBUTTON" => UiNodeKind.Button,
        "STATUSBAR" => UiNodeKind.StatusBar,
        _ => UiNodeKind.Frame,
    };

    /// <summary>
    /// Expands only the static template closure needed by the Dungeon Finder reference. The
    /// result remains transient XML used by the V2 ingester; it is never a V1 Project and is
    /// never persisted. Scripts are deliberately not executed.
    /// </summary>
    private sealed class TemplateExpander
    {
        private static readonly HashSet<string> SingletonChildren = new(StringComparer.OrdinalIgnoreCase)
        {
            "Size", "Anchors", "TexCoords", "Color", "BarColor",
        };
        private static readonly HashSet<string> NormalVisuals = new(StringComparer.OrdinalIgnoreCase)
        {
            "NormalTexture", "ThumbTexture",
        };
        private readonly Dictionary<string, XElement> _definitions = new(StringComparer.Ordinal);

        private TemplateExpander() { }

        public static TemplateExpander Create(XDocument primary,
            IEnumerable<V2DungeonFinderTemplateSource> sources, out IReadOnlyList<string> errors)
        {
            var result = new TemplateExpander();
            var failures = new List<string>();
            result.Index(primary, SourcePath);
            foreach (var source in sources)
            {
                if (!File.Exists(source.PhysicalPath))
                {
                    failures.Add($"Blizzard template source '{source.LogicalPath}' is unavailable at '{source.PhysicalPath}'.");
                    continue;
                }
                try
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                    using var reader = XmlReader.Create(source.PhysicalPath, settings);
                    result.Index(XDocument.Load(reader, LoadOptions.SetLineInfo), source.LogicalPath);
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"Blizzard template source '{source.LogicalPath}' could not be read: {ex.Message}");
                }
            }
            errors = failures;
            return result;
        }

        public XElement Expand(XElement instance)
        {
            var chain = new List<XElement>();
            foreach (var name in ParentNames(instance))
                AddDefinitionChain(name, chain, []);
            if (chain.Count == 0) return instance;
            chain.Add(instance);

            var expanded = new XElement(instance.Name);
            foreach (var source in chain)
            {
                foreach (var attribute in source.Attributes().Where(item => !item.IsNamespaceDeclaration))
                {
                    if (!ReferenceEquals(source, instance) && attribute.Name.LocalName is "name" or "virtual" or "inherits")
                        continue;
                    expanded.SetAttributeValue(attribute.Name, attribute.Value);
                }
            }

            foreach (var name in SingletonChildren)
            {
                var value = chain.SelectMany(item => item.Elements())
                    .LastOrDefault(item => Local(item, name));
                if (value is not null) expanded.Add(new XElement(value));
            }

            foreach (var source in chain)
            {
                foreach (var container in source.Elements().Where(item =>
                             Local(item, "Layers") || Local(item, "Frames") || Local(item, "ScrollChild")))
                    expanded.Add(new XElement(container));
                foreach (var child in source.Elements().Where(IsNode))
                    expanded.Add(new XElement(child));
            }

            // Button state regions are not ordinary FrameXML children, but their normal-state
            // texture is visible static artwork. Convert the effective normal/slider region into
            // an ordinary reference Texture so the existing V2 renderer can paint it.
            var normal = chain.SelectMany(item => item.Elements())
                .LastOrDefault(item => NormalVisuals.Contains(item.Name.LocalName));
            if (normal is not null)
            {
                var visual = ExpandVisual(normal);
                visual.Name = normal.Name.Namespace + "Texture";
                if (visual.Attribute("name") is null)
                    visual.SetAttributeValue("name", normal.Name.LocalName == "ThumbTexture"
                        ? "$parentThumbTexture"
                        : "$parentNormalTexture");
                if (visual.Elements().All(item => !Local(item, "Anchors")) &&
                    visual.Elements().All(item => !Local(item, "Size")))
                    visual.SetAttributeValue("setAllPoints", "true");
                expanded.Add(new XElement(expanded.Name.Namespace + "Layers",
                    new XElement(expanded.Name.Namespace + "Layer",
                        new XAttribute("level", normal.Name.LocalName == "ThumbTexture" ? "OVERLAY" : "ARTWORK"), visual)));
            }
            return expanded;
        }

        private XElement ExpandVisual(XElement visual)
        {
            var chain = new List<XElement>();
            foreach (var name in ParentNames(visual)) AddDefinitionChain(name, chain, []);
            chain.Add(visual);
            var result = new XElement(visual.Name);
            foreach (var source in chain)
            {
                foreach (var attribute in source.Attributes().Where(item => !item.IsNamespaceDeclaration &&
                             item.Name.LocalName is not ("name" or "virtual" or "inherits")))
                    result.SetAttributeValue(attribute.Name, attribute.Value);
            }
            foreach (var name in SingletonChildren)
            {
                var value = chain.SelectMany(item => item.Elements()).LastOrDefault(item => Local(item, name));
                if (value is not null) result.Add(new XElement(value));
            }
            return result;
        }

        private void AddDefinitionChain(string name, ICollection<XElement> result, HashSet<string> resolving)
        {
            if (!_definitions.TryGetValue(name, out var definition) || !resolving.Add(name)) return;
            foreach (var parent in ParentNames(definition)) AddDefinitionChain(parent, result, resolving);
            result.Add(definition);
            resolving.Remove(name);
        }

        private void Index(XDocument document, string logicalPath)
        {
            foreach (var element in document.Descendants().Where(item => IsNode(item) ||
                         NormalVisuals.Contains(item.Name.LocalName)))
                element.SetAttributeValue(ReferenceSourceAttribute, logicalPath);

            foreach (var element in document.Descendants().Where(item =>
                         BoolAttribute(item, "virtual") && item.Attribute("name") is not null))
            {
                var name = element.Attribute("name")!.Value;
                if (!name.Contains("$parent", StringComparison.Ordinal))
                {
                    _definitions.TryAdd(name, element);
                    foreach (var child in element.DescendantsAndSelf().Where(item => IsNode(item) ||
                                 NormalVisuals.Contains(item.Name.LocalName)))
                        child.SetAttributeValue(ReferenceTemplateAttribute, name);
                }
            }
        }

        private static IEnumerable<string> ParentNames(XElement element) =>
            ((string?)element.Attribute("inherits") ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class RawNode
    {
        public SemanticId Id { get; set; }
        public RawNode? Parent { get; init; }
        public List<RawNode> Children { get; } = [];
        public UiNodeKind Kind { get; init; }
        public string? RuntimeName { get; init; }
        public required string DisplayLabel { get; init; }
        public string? Inherits { get; init; }
        public string ReferenceSource { get; init; } = SourcePath;
        public string? TemplateOrigin { get; init; }
        public double? Width { get; init; }
        public double? Height { get; init; }
        public bool Visible { get; init; }
        public bool SetAllPoints { get; init; }
        public FrameStrata? Strata { get; init; }
        public int? Level { get; init; }
        public RegionDrawLayer? DrawLayer { get; init; }
        public string? TextureReference { get; init; }
        public string? Text { get; init; }
        public string? JustifyH { get; init; }
        public string? JustifyV { get; init; }
        public bool ReferenceAutoWidth { get; init; }
        public bool ReferenceAutoHeight { get; init; }
        public UiTexCoords? TexCoords { get; set; }
        public UiColor? Tint { get; set; }
        public List<RawAnchor> Anchors { get; } = [];
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }
        public double? Value { get; set; }
        public string? StatusTextureReference { get; set; }
        public UiColor? StatusFill { get; set; }
    }

    private sealed record RawAnchor(AnchorPoint Point, string? RelativeTo, AnchorPoint RelativePoint,
        double OffsetX, double OffsetY);
    private sealed record SourceDimension(double? Value, bool ExplicitZero);
}
