using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FrameForge.Core.Templates;

public sealed record BlizzardTemplateXmlSource(
    string LogicalPath,
    string Xml,
    string? ArchivePath = null,
    string? Build = null,
    string? Locale = null,
    string? Sha256 = null);

/// <summary>Parses only the approved build-12340 button-template closure.</summary>
public static class BlizzardTemplateParser
{
    public static BlizzardTemplateRegistry Parse(
        IEnumerable<BlizzardTemplateXmlSource> sources,
        IEnumerable<BlizzardAssetDependency>? assets = null,
        IEnumerable<BlizzardTemplateDiagnostic>? initialDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return new Builder(sources, assets ?? [], initialDiagnostics ?? []).Build();
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, RawDefinition> _raw = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BlizzardAssetDependency> _assets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BlizzardResolvedTemplate> _templates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BlizzardFontProperties> _fonts = new(StringComparer.Ordinal);
        private readonly List<BlizzardTemplateDiagnostic> _diagnostics;

        public Builder(
            IEnumerable<BlizzardTemplateXmlSource> sources,
            IEnumerable<BlizzardAssetDependency> assets,
            IEnumerable<BlizzardTemplateDiagnostic> initialDiagnostics)
        {
            _diagnostics = [.. initialDiagnostics];
            foreach (var asset in assets)
                _assets[Normalize(asset.LogicalPath)] = asset;
            foreach (var source in sources)
                Index(source);
        }

        public BlizzardTemplateRegistry Build()
        {
            foreach (var name in BlizzardTemplateRegistry.ApprovedTemplates)
                ResolveTemplate(name, []);
            return new BlizzardTemplateRegistry(_templates, _fonts, _diagnostics);
        }

        private void Index(BlizzardTemplateXmlSource source)
        {
            XDocument document;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(source.Xml), settings);
                document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                Add(BlizzardTemplateDiagnosticSeverity.Error, "invalid-template-source",
                    $"Could not parse '{source.LogicalPath}': {ex.Message}", source: Source(source, null));
                return;
            }

            foreach (var element in document.Descendants().Where(item => item.Attribute("name") is not null))
            {
                var name = element.Attribute("name")!.Value;
                if (name.Contains("$parent", StringComparison.Ordinal))
                    continue;
                if (element.Name.LocalName is not ("Button" or "Texture" or "Font"))
                    continue;
                var raw = new RawDefinition(name, element.Name.LocalName, element, source, Source(source, element));
                if (!_raw.TryAdd(name, raw))
                {
                    Add(BlizzardTemplateDiagnosticSeverity.Warning, "duplicate-template-definition",
                        $"Definition '{name}' was already indexed; the first source-order definition remains authoritative.",
                        name, raw.Provenance);
                }
            }
        }

        private BlizzardResolvedTemplate? ResolveTemplate(string name, HashSet<string> chain)
        {
            if (_templates.TryGetValue(name, out var cached))
                return cached;
            if (!chain.Add(name))
            {
                Add(BlizzardTemplateDiagnosticSeverity.Error, "cyclic-template-inheritance",
                    $"Template inheritance contains a cycle at '{name}'.", name,
                    _raw.GetValueOrDefault(name)?.Provenance);
                return null;
            }
            if (!_raw.TryGetValue(name, out var raw) || raw.Kind != "Button")
            {
                Add(BlizzardTemplateDiagnosticSeverity.Error, "missing-template-definition",
                    $"Approved template '{name}' is not available as a Button definition.", name,
                    dependency: name);
                chain.Remove(name);
                return null;
            }

            var parents = ParentNames(raw.Element);
            var definitionDiagnostics = new List<BlizzardTemplateDiagnostic>();
            if (parents.Count > 1)
            {
                var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "unsupported-template-inheritance",
                    $"Template '{name}' declares multiple inheritance. This registry supports one ordered parent only.",
                    name, raw.Provenance);
                Add(diagnostic);
                definitionDiagnostics.Add(diagnostic);
            }

            BlizzardResolvedTemplate? parent = null;
            if (parents.Count == 1)
            {
                if (_raw.TryGetValue(parents[0], out var parentRaw) && parentRaw.Kind != "Button")
                {
                    var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "unsupported-template-parent-type",
                        $"Button template '{name}' cannot inherit non-Button definition '{parents[0]}'.",
                        name, raw.Provenance, parents[0]);
                    Add(diagnostic);
                    definitionDiagnostics.Add(diagnostic);
                }
                else
                {
                    parent = ResolveTemplate(parents[0], chain);
                    if (parent is null)
                    {
                        var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "missing-template-parent",
                            $"Template '{name}' cannot resolve parent '{parents[0]}'.",
                            name, raw.Provenance, parents[0]);
                        Add(diagnostic);
                        definitionDiagnostics.Add(diagnostic);
                    }
                }
            }

            var declared = ParseButton(raw, definitionDiagnostics);
            foreach (var font in new[] { declared.NormalFont, declared.HighlightFont, declared.DisabledFont }
                         .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal))
                ResolveFont(font!, [], definitionDiagnostics);

            var effective = parent is null ? declared : Merge(parent.EffectiveProperties, declared);
            var inheritance = parent is null ? new[] { name } : [.. parent.InheritanceChain, name];
            var dependencies = CollectAssets(effective, definitionDiagnostics);
            foreach (var fontName in new[] { effective.NormalFont, effective.HighlightFont, effective.DisabledFont }
                         .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal))
            {
                var font = ResolveFont(fontName!, [], definitionDiagnostics);
                if (font?.File is { Length: > 0 } file)
                    AddAsset(file, dependencies, definitionDiagnostics, name, font.Source);
            }

            if (name == "CharacterFrameTabButtonTemplate")
            {
                var scripts = Child(raw.Element, "Scripts");
                var onShow = scripts is null ? null : Child(scripts, "OnShow")?.Value;
                if (onShow?.Contains("PanelTemplates_TabResize(self, 0)", StringComparison.Ordinal) != true)
                {
                    var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Warning,
                        "unexpected-character-tab-resize-contract",
                        "The verified character-tab zero-padding resize contract was not found; no preview behavior was inferred.",
                        name, raw.Provenance);
                    Add(diagnostic);
                    definitionDiagnostics.Add(diagnostic);
                }
            }

            var definition = new BlizzardTemplateDefinition(name, raw.Kind,
                Bool(raw.Element.Attribute("virtual")?.Value), Array.AsReadOnly(parents.ToArray()), declared, raw.Provenance);
            var resolved = new BlizzardResolvedTemplate(definition, effective,
                Array.AsReadOnly(inheritance.ToArray()), Array.AsReadOnly(dependencies.Values
                    .OrderBy(item => item.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray()),
                Array.AsReadOnly(definitionDiagnostics.Distinct().ToArray()));
            _templates[name] = resolved;
            chain.Remove(name);
            return resolved;
        }

        private BlizzardButtonProperties ParseButton(RawDefinition raw,
            List<BlizzardTemplateDiagnostic> definitionDiagnostics)
        {
            foreach (var element in raw.Element.Descendants()
                         .Where(item => item.Name.LocalName is "RelDimension" or "RelValue"))
            {
                var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "unsupported-relative-dimension",
                    $"Template '{raw.Name}' uses relative dimensions, which are outside the audited registry semantics.",
                    raw.Name, Source(raw.Source, element));
                Add(diagnostic);
                definitionDiagnostics.Add(diagnostic);
            }

            var provenance = new Dictionary<string, BlizzardPropertyProvenance>(StringComparer.Ordinal);
            var (width, height) = Size(raw.Element);
            Record(provenance, "width", width, raw.Name, BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(provenance, "height", height, raw.Name, BlizzardTemplateValueOrigin.Declared, raw.Provenance);

            var normalFont = Child(raw.Element, "NormalFont")?.Attribute("style")?.Value;
            var highlightFont = Child(raw.Element, "HighlightFont")?.Attribute("style")?.Value;
            var disabledFont = Child(raw.Element, "DisabledFont")?.Attribute("style")?.Value;
            Record(provenance, "normalFont", normalFont, raw.Name, BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(provenance, "highlightFont", highlightFont, raw.Name, BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(provenance, "disabledFont", disabledFont, raw.Name, BlizzardTemplateValueOrigin.Declared, raw.Provenance);

            var states = new Dictionary<BlizzardButtonState, BlizzardTextureValue>();
            ParseState(BlizzardButtonState.Normal, "NormalTexture");
            ParseState(BlizzardButtonState.Pushed, "PushedTexture");
            ParseState(BlizzardButtonState.Disabled, "DisabledTexture");
            ParseState(BlizzardButtonState.Highlight, "HighlightTexture");

            void ParseState(BlizzardButtonState state, string elementName)
            {
                if (Child(raw.Element, elementName) is not { } element)
                    return;
                var value = ResolveTextureValue(element, raw, [], definitionDiagnostics);
                if (value is null)
                    return;
                states[state] = value;
                foreach (var item in value.Provenance)
                    provenance[$"stateTextures.{state}.{item.Key}"] = item.Value with
                    {
                        Property = $"stateTextures.{state}.{item.Key}",
                    };
            }

            var regions = new List<BlizzardVisualRegion>();
            if (Child(raw.Element, "Layers") is { } layers)
            {
                foreach (var layer in layers.Elements().Where(item => item.Name.LocalName == "Layer"))
                {
                    foreach (var texture in layer.Elements().Where(item => item.Name.LocalName == "Texture"))
                    {
                        if (ParseRegion(texture, layer.Attribute("level")?.Value, raw, definitionDiagnostics) is { } region)
                            regions.Add(region);
                    }
                }
            }
            if (Child(raw.Element, "ButtonText") is { } buttonText &&
                ParseRegion(buttonText, null, raw, definitionDiagnostics, BlizzardVisualRegionKind.ButtonText) is { } textRegion)
                regions.Add(textRegion);

            foreach (var region in regions)
            {
                Record(provenance, $"regions.{region.SymbolicName}.width", region.Width, raw.Name,
                    BlizzardTemplateValueOrigin.Declared, region.Source);
                Record(provenance, $"regions.{region.SymbolicName}.height", region.Height, raw.Name,
                    BlizzardTemplateValueOrigin.Declared, region.Source);
                if (region.Texture?.File is not null)
                    provenance[$"regions.{region.SymbolicName}.texture.file"] =
                        region.Texture.Provenance["file"] with
                        {
                            Property = $"regions.{region.SymbolicName}.texture.file",
                        };
                if (region.Anchors.Count > 0)
                    provenance[$"regions.{region.SymbolicName}.anchors"] = new BlizzardPropertyProvenance(
                        $"regions.{region.SymbolicName}.anchors", raw.Name,
                        BlizzardTemplateValueOrigin.Declared, region.Source);
            }

            var behaviors = new List<BlizzardKnownPreviewBehavior>();
            if (raw.Name == "CharacterFrameTabButtonTemplate" &&
                Child(Child(raw.Element, "Scripts"), "OnShow")?.Value
                    .Contains("PanelTemplates_TabResize(self, 0)", StringComparison.Ordinal) == true)
                behaviors.Add(BlizzardKnownPreviewBehavior.CharacterTabResizeToTextZeroPadding);

            if (Child(raw.Element, "Scripts") is not null)
            {
                var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Warning, "unsupported-template-scripts",
                    behaviors.Count == 0
                        ? $"Template '{raw.Name}' contains scripts. They are retained only as an explicit unsupported boundary and are never executed."
                        : $"Template '{raw.Name}' contains scripts. Only the audited character-tab resize behavior is identified for later preview handling; no Lua is executed.",
                    raw.Name, raw.Provenance);
                Add(diagnostic);
                definitionDiagnostics.Add(diagnostic);
            }

            return new BlizzardButtonProperties(width, height, normalFont, highlightFont, disabledFont,
                ReadOnly(states), Array.AsReadOnly(regions.ToArray()), Array.AsReadOnly(behaviors.ToArray()),
                ReadOnly(provenance));
        }

        private BlizzardVisualRegion? ParseRegion(XElement element, string? drawLayer, RawDefinition raw,
            List<BlizzardTemplateDiagnostic> diagnostics,
            BlizzardVisualRegionKind kind = BlizzardVisualRegionKind.Texture)
        {
            var name = element.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "unsupported-anonymous-template-region",
                    $"Template '{raw.Name}' contains an anonymous visual region; the initial registry requires symbolic region identities.",
                    raw.Name, Source(raw.Source, element));
                Add(diagnostic);
                diagnostics.Add(diagnostic);
                return null;
            }
            var source = Source(raw.Source, element);
            var (width, height) = Size(element);
            var texture = kind == BlizzardVisualRegionKind.Texture
                ? ResolveTextureValue(element, raw, [], diagnostics)
                : null;
            BlizzardButtonState? state = name.EndsWith("Disabled", StringComparison.Ordinal)
                ? BlizzardButtonState.Disabled
                : kind == BlizzardVisualRegionKind.Texture ? BlizzardButtonState.Normal : null;
            var anchors = Child(element, "Anchors")?.Elements()
                .Where(item => item.Name.LocalName == "Anchor")
                .Select(item => ParseAnchor(item, raw.Source)).ToArray() ?? [];
            return new BlizzardVisualRegion(name, kind, state, drawLayer, width, height, texture,
                Array.AsReadOnly(anchors), source);
        }

        private BlizzardTextureValue? ResolveTextureValue(XElement element, RawDefinition owner,
            HashSet<string> chain, List<BlizzardTemplateDiagnostic> diagnostics)
        {
            var source = Source(owner.Source, element);
            var inheritedName = element.Attribute("inherits")?.Value?.Trim();
            BlizzardTextureValue? inherited = null;
            if (!string.IsNullOrWhiteSpace(inheritedName))
            {
                if (inheritedName.Contains(','))
                {
                    var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "unsupported-texture-inheritance",
                        $"Texture state on '{owner.Name}' uses unsupported multiple inheritance '{inheritedName}'.",
                        owner.Name, source, inheritedName);
                    Add(diagnostic);
                    diagnostics.Add(diagnostic);
                    return null;
                }
                if (!chain.Add(inheritedName))
                {
                    var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "cyclic-texture-inheritance",
                        $"Texture inheritance contains a cycle at '{inheritedName}'.", owner.Name, source, inheritedName);
                    Add(diagnostic);
                    diagnostics.Add(diagnostic);
                    return null;
                }
                if (!_raw.TryGetValue(inheritedName, out var raw) || raw.Kind != "Texture")
                {
                    var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "missing-texture-definition",
                        $"Texture dependency '{inheritedName}' for '{owner.Name}' is unavailable.",
                        owner.Name, source, inheritedName);
                    Add(diagnostic);
                    diagnostics.Add(diagnostic);
                    return null;
                }
                inherited = ResolveTextureValue(raw.Element, raw, chain, diagnostics);
                chain.Remove(inheritedName);
            }

            var tex = Child(element, "TexCoords");
            var left = Number(tex, "left");
            var right = Number(tex, "right");
            var top = Number(tex, "top");
            var bottom = Number(tex, "bottom");
            var coordinates = tex is null
                ? inherited?.TexCoords
                : left is not null && right is not null && top is not null && bottom is not null
                    ? new BlizzardTexCoords(left.Value, right.Value, top.Value, bottom.Value)
                    : null;
            var (declaredWidth, declaredHeight) = Size(element);
            var anchorsElement = Child(element, "Anchors");
            var anchors = anchorsElement is null
                ? inherited?.Anchors ?? Array.Empty<BlizzardTemplateAnchor>()
                : Array.AsReadOnly(anchorsElement.Elements()
                    .Where(item => item.Name.LocalName == "Anchor")
                    .Select(item => ParseAnchor(item, owner.Source)).ToArray());
            var properties = inherited is null
                ? new Dictionary<string, BlizzardPropertyProvenance>(StringComparer.Ordinal)
                : inherited.Provenance.ToDictionary(item => item.Key,
                    item => item.Value with { Origin = BlizzardTemplateValueOrigin.InheritedDependency },
                    StringComparer.Ordinal);
            Record(properties, "symbolicName", element.Attribute("name")?.Value, owner.Name,
                BlizzardTemplateValueOrigin.Declared, source);
            Record(properties, "file", element.Attribute("file")?.Value, owner.Name,
                BlizzardTemplateValueOrigin.Declared, source);
            Record(properties, "width", declaredWidth, owner.Name,
                BlizzardTemplateValueOrigin.Declared, source);
            Record(properties, "height", declaredHeight, owner.Name,
                BlizzardTemplateValueOrigin.Declared, source);
            Record(properties, "texCoords", tex is null ? null : coordinates, owner.Name,
                BlizzardTemplateValueOrigin.Declared, tex is null ? source : Source(owner.Source, tex));
            Record(properties, "alphaMode", element.Attribute("alphaMode")?.Value, owner.Name,
                BlizzardTemplateValueOrigin.Declared, source);
            Record(properties, "anchors", anchorsElement is null ? null : anchors, owner.Name,
                BlizzardTemplateValueOrigin.Declared,
                anchorsElement is null ? source : Source(owner.Source, anchorsElement));
            return new BlizzardTextureValue(
                element.Attribute("name")?.Value,
                element.Attribute("file")?.Value ?? inherited?.File,
                declaredWidth ?? inherited?.Width,
                declaredHeight ?? inherited?.Height,
                coordinates,
                element.Attribute("alphaMode")?.Value ?? inherited?.AlphaMode,
                inheritedName ?? inherited?.Inherits,
                anchors,
                ReadOnly(properties),
                source);
        }

        private BlizzardFontProperties? ResolveFont(string name, HashSet<string> chain,
            List<BlizzardTemplateDiagnostic> templateDiagnostics)
        {
            if (_fonts.TryGetValue(name, out var cached))
                return cached;
            if (!chain.Add(name))
            {
                AddFontError("cyclic-font-inheritance", $"Font inheritance contains a cycle at '{name}'.", name);
                return null;
            }
            if (!_raw.TryGetValue(name, out var raw) || raw.Kind != "Font")
            {
                AddFontError("missing-font-definition", $"Font dependency '{name}' is unavailable.", name);
                chain.Remove(name);
                return null;
            }
            if (raw.Element.Descendants()
                    .FirstOrDefault(item => item.Name.LocalName is "RelDimension" or "RelValue") is { } relative)
            {
                AddFontError("unsupported-relative-dimension",
                    $"Font '{name}' uses relative dimensions, which are outside the audited registry semantics.",
                    name, Source(raw.Source, relative));
                chain.Remove(name);
                return null;
            }
            var parents = ParentNames(raw.Element);
            if (parents.Count > 1)
            {
                AddFontError("unsupported-font-inheritance",
                    $"Font '{name}' declares multiple inheritance, which is unsupported.", name, raw.Provenance);
                chain.Remove(name);
                return null;
            }
            var parent = parents.Count == 1 ? ResolveFont(parents[0], chain, templateDiagnostics) : null;
            if (parents.Count == 1 && parent is null)
            {
                chain.Remove(name);
                return null;
            }

            var properties = parent is null
                ? new Dictionary<string, BlizzardPropertyProvenance>(StringComparer.Ordinal)
                : parent.Provenance.ToDictionary(item => item.Key,
                    item => item.Value with { Origin = BlizzardTemplateValueOrigin.InheritedTemplate },
                    StringComparer.Ordinal);
            var file = raw.Element.Attribute("font")?.Value ?? parent?.File;
            var height = Number(Child(Child(raw.Element, "FontHeight"), "AbsValue"), "val") ?? parent?.Height;
            var color = Color(Child(raw.Element, "Color")) ?? parent?.Color;
            var justifyH = raw.Element.Attribute("justifyH")?.Value ?? parent?.JustifyH;
            var justifyV = raw.Element.Attribute("justifyV")?.Value ?? parent?.JustifyV;
            var outline = raw.Element.Attribute("outline")?.Value ?? parent?.Outline;
            var shadow = Child(raw.Element, "Shadow");
            var offset = Child(Child(shadow, "Offset"), "AbsDimension");
            var shadowX = Number(offset, "x") ?? parent?.ShadowX;
            var shadowY = Number(offset, "y") ?? parent?.ShadowY;
            var shadowColor = Color(Child(shadow, "Color")) ?? parent?.ShadowColor;
            Record(properties, "file", raw.Element.Attribute("font") is null ? null : file, name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "height", Number(Child(Child(raw.Element, "FontHeight"), "AbsValue"), "val"), name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "color", Color(Child(raw.Element, "Color")), name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "justifyH", raw.Element.Attribute("justifyH")?.Value, name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "justifyV", raw.Element.Attribute("justifyV")?.Value, name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "outline", raw.Element.Attribute("outline")?.Value, name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "shadowX", Number(offset, "x"), name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "shadowY", Number(offset, "y"), name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            Record(properties, "shadowColor", Color(Child(shadow, "Color")), name,
                BlizzardTemplateValueOrigin.Declared, raw.Provenance);
            var result = new BlizzardFontProperties(name, parents.SingleOrDefault(), file, height, color,
                justifyH, justifyV, outline, shadowX, shadowY, shadowColor, ReadOnly(properties), raw.Provenance);
            _fonts[name] = result;
            chain.Remove(name);
            return result;

            void AddFontError(string code, string message, string dependency,
                BlizzardSourceProvenance? source = null)
            {
                var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, code, message, name,
                    source ?? _raw.GetValueOrDefault(name)?.Provenance, dependency);
                Add(diagnostic);
                templateDiagnostics.Add(diagnostic);
            }
        }

        private Dictionary<string, BlizzardAssetDependency> CollectAssets(BlizzardButtonProperties properties,
            List<BlizzardTemplateDiagnostic> diagnostics)
        {
            var result = new Dictionary<string, BlizzardAssetDependency>(StringComparer.OrdinalIgnoreCase);
            foreach (var texture in properties.StateTextures.Values)
                if (texture.File is { Length: > 0 } file)
                    AddAsset(file, result, diagnostics, null,
                        texture.Provenance.GetValueOrDefault("file")?.Source ?? texture.Source);
            foreach (var texture in properties.VisualRegions.Select(item => item.Texture).Where(item => item is not null))
                if (texture!.File is { Length: > 0 } file)
                    AddAsset(file, result, diagnostics, null,
                        texture.Provenance.GetValueOrDefault("file")?.Source ?? texture.Source);
            return result;
        }

        private void AddAsset(string path, Dictionary<string, BlizzardAssetDependency> target,
            List<BlizzardTemplateDiagnostic> diagnostics, string? definition, BlizzardSourceProvenance source)
        {
            var normalized = Normalize(path);
            var asset = _assets.GetValueOrDefault(normalized) ??
                        new BlizzardAssetDependency(path, false, Diagnostic: "The asset was not supplied by the installed-client source.");
            target[normalized] = asset;
            if (asset.IsResolved)
                return;
            var diagnostic = New(BlizzardTemplateDiagnosticSeverity.Error, "unresolved-template-asset",
                $"Template asset '{path}' is unresolved. {asset.Diagnostic}".TrimEnd(), definition, source, path);
            Add(diagnostic);
            diagnostics.Add(diagnostic);
        }

        private static BlizzardButtonProperties Merge(BlizzardButtonProperties parent, BlizzardButtonProperties child)
        {
            var states = new Dictionary<BlizzardButtonState, BlizzardTextureValue>(parent.StateTextures);
            foreach (var item in child.StateTextures)
                states[item.Key] = item.Value;

            var regions = parent.VisualRegions.ToList();
            foreach (var region in child.VisualRegions)
            {
                var index = regions.FindIndex(item => item.SymbolicName == region.SymbolicName);
                if (index >= 0) regions[index] = region;
                else regions.Add(region);
            }

            var provenance = parent.Provenance.ToDictionary(item => item.Key,
                item => item.Value with { Origin = BlizzardTemplateValueOrigin.InheritedTemplate },
                StringComparer.Ordinal);
            foreach (var item in child.Provenance)
                provenance[item.Key] = item.Value;

            return new BlizzardButtonProperties(
                child.Width ?? parent.Width,
                child.Height ?? parent.Height,
                child.NormalFont ?? parent.NormalFont,
                child.HighlightFont ?? parent.HighlightFont,
                child.DisabledFont ?? parent.DisabledFont,
                ReadOnly(states), Array.AsReadOnly(regions.ToArray()),
                Array.AsReadOnly(parent.PreviewBehaviors.Concat(child.PreviewBehaviors).Distinct().ToArray()),
                ReadOnly(provenance));
        }

        private static BlizzardTemplateAnchor ParseAnchor(XElement element, BlizzardTemplateXmlSource source)
        {
            var offset = Child(element, "Offset");
            var dimensions = Child(offset, "AbsDimension") ?? offset;
            return new BlizzardTemplateAnchor(
                element.Attribute("point")?.Value,
                element.Attribute("relativeTo")?.Value,
                element.Attribute("relativePoint")?.Value,
                Number(dimensions, "x") ?? 0,
                Number(dimensions, "y") ?? 0,
                Source(source, element));
        }

        private static (double? Width, double? Height) Size(XElement element)
        {
            var size = Child(element, "Size");
            var dimensions = Child(size, "AbsDimension") ?? size;
            return (Number(dimensions, "x"), Number(dimensions, "y"));
        }

        private static IReadOnlyList<string> ParentNames(XElement element) =>
            (element.Attribute("inherits")?.Value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        private static XElement? Child(XElement? element, string name) =>
            element?.Elements().FirstOrDefault(item => item.Name.LocalName == name);

        private static double? Number(XElement? element, string attribute) =>
            double.TryParse(element?.Attribute(attribute)?.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

        private static BlizzardColor? Color(XElement? element) => element is null ? null :
            Number(element, "r") is { } red && Number(element, "g") is { } green && Number(element, "b") is { } blue
                ? new BlizzardColor(red, green, blue, Number(element, "a") ?? 1)
                : null;

        private static bool Bool(string? value) =>
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

        private static string Normalize(string value) => value.Trim().Replace('/', '\\');

        private static BlizzardSourceProvenance Source(BlizzardTemplateXmlSource source, XElement? element)
        {
            var line = element as IXmlLineInfo;
            return new BlizzardSourceProvenance(source.LogicalPath,
                line?.HasLineInfo() == true ? line.LineNumber : 0,
                line?.HasLineInfo() == true ? line.LinePosition : 0,
                source.Sha256 ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Xml))).ToLowerInvariant(),
                source.ArchivePath, source.Build, source.Locale);
        }

        private static IReadOnlyDictionary<TKey, TValue> ReadOnly<TKey, TValue>(IDictionary<TKey, TValue> values)
            where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(values));

        private static void Record<T>(IDictionary<string, BlizzardPropertyProvenance> target,
            string property, T? value, string definition, BlizzardTemplateValueOrigin origin,
            BlizzardSourceProvenance source)
        {
            if (value is not null)
                target[property] = new BlizzardPropertyProvenance(property, definition, origin, source);
        }

        private void Add(BlizzardTemplateDiagnostic diagnostic)
        {
            if (!_diagnostics.Contains(diagnostic))
                _diagnostics.Add(diagnostic);
        }

        private void Add(BlizzardTemplateDiagnosticSeverity severity, string code, string message,
            string? definition = null, BlizzardSourceProvenance? source = null, string? dependency = null) =>
            Add(New(severity, code, message, definition, source, dependency));

        private static BlizzardTemplateDiagnostic New(BlizzardTemplateDiagnosticSeverity severity,
            string code, string message, string? definition = null,
            BlizzardSourceProvenance? source = null, string? dependency = null) =>
            new(severity, code, message, definition, source, dependency);

        private sealed record RawDefinition(string Name, string Kind, XElement Element,
            BlizzardTemplateXmlSource Source, BlizzardSourceProvenance Provenance);
    }
}
