using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FrameForge.Core.Semantics.V2;

namespace FrameForge.Core.Export;

public sealed record V2FrameXmlDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SemanticId? NodeId = null,
    string? PropertyPath = null);

public sealed record V2ExportedControl(
    SemanticId Id,
    string RuntimeName,
    UiNodeKind Kind,
    SemanticId OwnerId,
    string OwnerRuntimeName,
    string LuaExpression);

public sealed record V2ExternalDependency(
    string Kind,
    string Identity,
    string? ExpectedSource,
    IReadOnlyList<string> Consumers);

public sealed record V2ExportedAsset(
    string ProjectReference,
    string SourcePhysicalPath,
    string OutputPath,
    string LogicalClientPath,
    string Format,
    string Sha256,
    IReadOnlyList<string> Consumers);

public sealed record V2FrameXmlExportPlan(
    UiDocument Document,
    CompositionRoot Root,
    string Xml,
    string Manifest,
    IReadOnlyDictionary<SemanticId, string> RuntimeNames,
    IReadOnlyList<V2ExportedControl> Controls,
    IReadOnlyList<V2ExternalDependency> ExternalDependencies,
    IReadOnlyList<V2ExportedAsset> Assets,
    IReadOnlyList<V2FrameXmlDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
}

public sealed record V2FrameXmlExportResult(
    bool Success,
    V2FrameXmlExportPlan Plan,
    IReadOnlyList<string> WrittenFiles)
{
    public string Summary => Success
        ? $"Exported {Plan.Controls.Count} controls and {Plan.Assets.Count} project asset(s)."
        : $"Export blocked by {Plan.Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error)} error(s).";
}

/// <summary>
/// Deterministic build-12340 FrameXML planning directly from the schema-v2 semantic graph.
/// This code deliberately has no dependency on FrameDef or UiDocumentProjection.
/// </summary>
public static partial class V2FrameXmlExporter
{
    public const string XmlFileName = "Design.xml";
    public const string ManifestFileName = "frameforge.manifest.json";
    public const string ManifestSchema = "frameforge-v2-framexml-export";
    public const int ManifestVersion = 1;
    private const string GeneratedPrefix = "FF2_";

    public static V2FrameXmlExportPlan Build(UiDocument document, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = UiDocumentValidator.Validate(document)
            .Select(item => new V2FrameXmlDiagnostic(item.Severity, item.Code, item.Message, item.NodeId, item.PropertyPath))
            .ToList();
        diagnostics.AddRange(document.Diagnostics.Select(item => new V2FrameXmlDiagnostic(
            item.Severity, item.Code, item.Message, item.NodeId, item.PropertyPath)));

        if (document.CompositionRoots.Count != 1)
            return InvalidPlan(document, diagnostics);

        var root = document.CompositionRoots[0];
        var names = AllocateRuntimeNames(document, root, diagnostics);
        ValidateExportProperties(document, diagnostics);
        var assets = BuildAssets(document, projectFilePath, names, diagnostics, out var texturePaths,
            out var assetDependencies);
        var dependencies = BuildExternalDependencies(document, root, names, assetDependencies, diagnostics);
        var controls = document.Nodes.Select(node => new V2ExportedControl(
                node.Id,
                names.GetValueOrDefault(node.Id, string.Empty),
                node.Kind,
                node.Owner.Id,
                node.Owner.Kind == OwnerKind.CompositionRoot
                    ? root.RuntimeName
                    : names.GetValueOrDefault(node.Owner.Id, string.Empty),
                "_G[\"" + names.GetValueOrDefault(node.Id, string.Empty) + "\"]"))
            .ToArray();

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new(document, root, string.Empty, string.Empty, names, controls, dependencies, assets, diagnostics);

        var xml = WriteXml(document, root, names, texturePaths);
        XDocument parsed;
        try
        {
            parsed = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            diagnostics.Add(Error("FFV2X-XML-001", $"Generated FrameXML did not parse: {ex.Message}"));
            return new(document, root, string.Empty, string.Empty, names, controls, dependencies, assets, diagnostics);
        }
        ValidateGeneratedStructure(document, root, names, parsed, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new(document, root, string.Empty, string.Empty, names, controls, dependencies, assets, diagnostics);

        var manifest = WriteManifest(document, root, controls, dependencies, assets, diagnostics, xml);
        return new(document, root, xml, manifest, names, controls, dependencies, assets, diagnostics);
    }

    public static V2FrameXmlExportResult Export(
        UiDocument document,
        string? projectFilePath,
        string destinationDirectory)
    {
        var plan = Build(document, projectFilePath);
        if (!plan.IsValid)
            return new(false, plan, []);

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("The export destination requires a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".frameforge-v2-staging-{Guid.NewGuid():N}");
        var written = new List<string>();
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var asset in plan.Assets)
            {
                var target = ResolvePackagePath(staging, asset.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(asset.SourcePhysicalPath, target, true);
                using var copied = File.OpenRead(target);
                if (!string.Equals(Hash(copied), asset.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Artwork '{asset.ProjectReference}' changed while export was staged.");
            }

            File.WriteAllText(Path.Combine(staging, XmlFileName), plan.Xml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(staging, ManifestFileName), plan.Manifest, new UTF8Encoding(false));

            if (Directory.Exists(destination))
                PublishIntoExistingDirectory(staging, destination, plan);
            else
                Directory.Move(staging, destination);

            written.Add(Path.Combine(destination, XmlFileName));
            written.Add(Path.Combine(destination, ManifestFileName));
            written.AddRange(plan.Assets.Select(asset => ResolvePackagePath(destination, asset.OutputPath)));
            return new(true, plan, written);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static V2FrameXmlExportPlan InvalidPlan(UiDocument document, List<V2FrameXmlDiagnostic> diagnostics)
    {
        var placeholder = document.CompositionRoots.FirstOrDefault() ?? new CompositionRoot
        {
            Id = new SemanticId(Guid.Empty.ToString("D")),
            RuntimeName = string.Empty,
            ExternalHostName = string.Empty,
            DesignWidth = 1,
            DesignHeight = 1,
            Sizing = RootSizing.FillHost(),
        };
        return new(document, placeholder, string.Empty, string.Empty,
            new Dictionary<SemanticId, string>(), [], [], [], diagnostics);
    }

    private static IReadOnlyDictionary<SemanticId, string> AllocateRuntimeNames(
        UiDocument document,
        CompositionRoot root,
        List<V2FrameXmlDiagnostic> diagnostics)
    {
        var result = new Dictionary<SemanticId, string>();
        var used = new Dictionary<string, SemanticId>(StringComparer.Ordinal);
        ValidateExplicitName(root.RuntimeName, root.Id, "compositionRoots[0].runtimeName", true, diagnostics);
        if (IsRuntimeName(root.RuntimeName))
            used[root.RuntimeName] = root.Id;

        foreach (var node in document.Nodes)
        {
            var name = node.RuntimeName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = GeneratedPrefix + Compact(document.DocumentId) + "_" + Compact(node.Id);
            }
            else
            {
                ValidateExplicitName(name, node.Id, "runtimeName", true, diagnostics);
            }

            if (used.TryGetValue(name, out var first))
            {
                diagnostics.Add(Error("FFV2X-NAME-003",
                    $"Runtime name '{name}' collides with identity '{first}'. Explicit identities are never renamed.",
                    node.Id, "runtimeName"));
            }
            else
            {
                used[name] = node.Id;
            }
            result[node.Id] = name;
        }

        foreach (var external in document.ExternalReferences)
        {
            if (used.ContainsKey(external.GlobalName))
                diagnostics.Add(Error("FFV2X-NAME-004",
                    $"Generated runtime identity '{external.GlobalName}' collides with a declared external global."));
        }
        return result;
    }

    private static void ValidateExplicitName(string name, SemanticId id, string path, bool reservePrefix,
        List<V2FrameXmlDiagnostic> diagnostics)
    {
        if (!IsRuntimeName(name))
            diagnostics.Add(Error("FFV2X-NAME-001",
                $"Explicit runtime name '{name}' is not a valid WoW global identifier.", id, path));
        if (reservePrefix && name.StartsWith(GeneratedPrefix, StringComparison.Ordinal))
            diagnostics.Add(Error("FFV2X-NAME-002",
                $"Explicit runtime names may not use the reserved '{GeneratedPrefix}' generated-name namespace.", id, path));
    }

    private static void ValidateExportProperties(UiDocument document, List<V2FrameXmlDiagnostic> diagnostics)
    {
        foreach (var node in document.Nodes)
        {
            var frame = node.AuthoredProperties.Frame;
            var region = node.AuthoredProperties.Region;
            if (frame is not null && (frame.Width is null) != (frame.Height is null))
                diagnostics.Add(Error("FFV2X-PROP-001",
                    "Static FrameXML requires frame width and height to be authored together.", node.Id,
                    "authoredProperties.frame"));
            if (region is not null && (region.Width is null) != (region.Height is null))
                diagnostics.Add(Error("FFV2X-PROP-002",
                    "Static FrameXML requires region width and height to be authored together.", node.Id,
                    "authoredProperties.region"));
            if (region?.Sublevel is < -8 or > 7)
                diagnostics.Add(Error("FFV2X-PROP-006",
                    "Build-12340 region texture sublevels must be between -8 and 7.", node.Id,
                    "authoredProperties.region.sublevel"));
            if (node.AuthoredProperties.Button?.Enabled == false)
                diagnostics.Add(Error("FFV2X-PROP-003",
                    "A disabled Button requires runtime script and cannot be represented by this visual-only exporter.",
                    node.Id, "authoredProperties.button.enabled"));
            if (node.Kind == UiNodeKind.StatusBar && node.AuthoredProperties.StatusBar is { } bar &&
                ((bar.Minimum is null) != (bar.Maximum is null)))
                diagnostics.Add(Error("FFV2X-PROP-004",
                    "StatusBar minimum and maximum must be authored together.", node.Id,
                    "authoredProperties.statusBar"));
            if (node.Kind == UiNodeKind.StatusBar && node.AuthoredProperties.StatusBar is { Value: not null } valueBar &&
                (valueBar.Minimum is null || valueBar.Maximum is null))
                diagnostics.Add(Error("FFV2X-PROP-005",
                    "A StatusBar default value requires an authored minimum and maximum.", node.Id,
                    "authoredProperties.statusBar.value"));
        }
    }

    private static IReadOnlyList<V2ExportedAsset> BuildAssets(
        UiDocument document,
        string? projectFilePath,
        IReadOnlyDictionary<SemanticId, string> names,
        List<V2FrameXmlDiagnostic> diagnostics,
        out IReadOnlyDictionary<SemanticId, string> texturePaths,
        out IReadOnlyList<V2ExternalDependency> assetDependencies)
    {
        var resolved = new Dictionary<SemanticId, string>();
        var projectAssets = new List<V2ExportedAsset>();
        var external = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var projectDirectory = string.IsNullOrWhiteSpace(projectFilePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(projectFilePath));

        foreach (var node in document.Nodes)
        {
            var reference = node.Kind switch
            {
                UiNodeKind.Texture => node.AuthoredProperties.Texture?.TextureReference,
                UiNodeKind.StatusBar => node.AuthoredProperties.StatusBar?.TextureReference,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(reference))
                continue;

            var consumer = names.GetValueOrDefault(node.Id, node.Id.Value);
            if (IsClientPath(reference))
            {
                var logical = NormalizeClientPath(reference);
                if (!IsSafeClientPath(logical))
                {
                    diagnostics.Add(Error("FFV2X-ASSET-008",
                        $"Client artwork path '{reference}' is not a safe Interface-relative path.",
                        node.Id, TexturePropertyPath(node)));
                    continue;
                }
                resolved[node.Id] = logical;
                if (!external.TryGetValue(logical, out var consumers))
                    external[logical] = consumers = [];
                consumers.Add(consumer);
                continue;
            }

            if (projectDirectory is null)
            {
                diagnostics.Add(Error("FFV2X-ASSET-001",
                    $"Project artwork '{reference}' cannot be resolved until the v2 project has a file path.",
                    node.Id, TexturePropertyPath(node)));
                continue;
            }
            if (Path.IsPathRooted(reference) || Regex.IsMatch(reference, @"^[A-Za-z]:[\\/]"))
            {
                diagnostics.Add(Error("FFV2X-ASSET-002",
                    $"Project artwork '{reference}' must be a portable project-relative path.",
                    node.Id, TexturePropertyPath(node)));
                continue;
            }

            string physical;
            try
            {
                physical = Path.GetFullPath(Path.Combine(projectDirectory,
                    reference.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                diagnostics.Add(Error("FFV2X-ASSET-003", $"Artwork path '{reference}' is invalid: {ex.Message}",
                    node.Id, TexturePropertyPath(node)));
                continue;
            }
            var relative = Path.GetRelativePath(projectDirectory, physical);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                diagnostics.Add(Error("FFV2X-ASSET-004",
                    $"Project artwork '{reference}' escapes the project directory.", node.Id,
                    TexturePropertyPath(node)));
                continue;
            }
            var extension = Path.GetExtension(physical).ToLowerInvariant();
            if (extension is not (".tga" or ".blp"))
            {
                diagnostics.Add(Error("FFV2X-ASSET-005",
                    $"Project artwork '{reference}' uses unsupported format '{extension}'. Build 12340 export currently accepts TGA and BLP without lossy conversion.",
                    node.Id, TexturePropertyPath(node)));
                continue;
            }
            if (!File.Exists(physical))
            {
                diagnostics.Add(Error("FFV2X-ASSET-006",
                    $"Project artwork '{reference}' was not found.", node.Id, TexturePropertyPath(node)));
                continue;
            }

            string hash;
            try
            {
                var bytes = File.ReadAllBytes(physical);
                if (!HasSupportedArtworkHeader(bytes, extension))
                {
                    diagnostics.Add(Error("FFV2X-ASSET-009",
                        $"Project artwork '{reference}' does not contain a supported {extension[1..].ToUpperInvariant()} header.",
                        node.Id, TexturePropertyPath(node)));
                    continue;
                }
                hash = Hash(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("FFV2X-ASSET-007",
                    $"Project artwork '{reference}' could not be read: {ex.Message}", node.Id,
                    TexturePropertyPath(node)));
                continue;
            }
            var stem = SafeFileStem(Path.GetFileNameWithoutExtension(reference));
            var fileName = $"{stem}-{hash[..12].ToLowerInvariant()}{extension}";
            var output = "Artwork/" + fileName;
            var logicalPath = @"Interface\FrameForge\Artwork\" + fileName;
            resolved[node.Id] = logicalPath;
            projectAssets.Add(new(reference.Replace('\\', '/'), physical, output, logicalPath,
                extension[1..], hash, [consumer]));
        }

        var assets = projectAssets
            .GroupBy(item => (item.Sha256, item.OutputPath))
            .Select(group => group.First() with
            {
                Consumers = group.SelectMany(item => item.Consumers).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(),
            })
            .OrderBy(item => item.OutputPath, StringComparer.Ordinal)
            .ToArray();
        texturePaths = resolved;
        assetDependencies = external.Select(item => new V2ExternalDependency(
            "blizzardAsset", item.Key, null,
            item.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray();
        return assets;
    }

    private static IReadOnlyList<V2ExternalDependency> BuildExternalDependencies(
        UiDocument document,
        CompositionRoot root,
        IReadOnlyDictionary<SemanticId, string> names,
        IReadOnlyList<V2ExternalDependency> assetDependencies,
        List<V2FrameXmlDiagnostic> diagnostics)
    {
        var consumers = document.Nodes.SelectMany(node => node.Anchors
                .Where(anchor => anchor.Target.Kind == AnchorTargetKind.ExternalGlobal)
                .Select(anchor => (anchor.Target.GlobalName!, Consumer: names[node.Id])))
            .GroupBy(item => item.Item1, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Consumer).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var templateDependencies = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in document.Nodes.Where(node => node.Kind == UiNodeKind.FontString))
        {
            var font = node.AuthoredProperties.FontString?.FontReference;
            if (string.IsNullOrWhiteSpace(font))
                continue;
            if (!IsRuntimeName(font))
            {
                diagnostics.Add(Error("FFV2X-FONT-001",
                    $"Font reference '{font}' is not a valid Blizzard font-object identity.", node.Id,
                    "authoredProperties.fontString.fontReference"));
                continue;
            }
            if (!templateDependencies.TryGetValue(font, out var fontConsumers))
                templateDependencies[font] = fontConsumers = [];
            fontConsumers.Add(names[node.Id]);
        }

        var references = document.ExternalReferences.Select(reference => new V2ExternalDependency(
                string.Equals(reference.GlobalName, root.ExternalHostName, StringComparison.Ordinal)
                    ? "moduleHost"
                    : "externalGlobal",
                reference.GlobalName,
                reference.ExpectedSource,
                string.Equals(reference.GlobalName, root.ExternalHostName, StringComparison.Ordinal)
                    ? [root.RuntimeName]
                    : consumers.GetValueOrDefault(reference.GlobalName, [])))
            .Concat(assetDependencies)
            .Concat(templateDependencies.Select(item => new V2ExternalDependency(
                "blizzardTemplate", item.Key, "Blizzard FrameXML font objects",
                item.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();
        return references;
    }

    private static string WriteXml(
        UiDocument document,
        CompositionRoot root,
        IReadOnlyDictionary<SemanticId, string> names,
        IReadOnlyDictionary<SemanticId, string> texturePaths)
    {
        var byId = document.Nodes.ToDictionary(item => item.Id);
        var output = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(false),
        };
        using var writer = XmlWriter.Create(output, settings);
        writer.WriteStartElement("Ui", "http://www.blizzard.com/wow/ui/");
        writer.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
        writer.WriteAttributeString("xsi", "schemaLocation", null,
            "http://www.blizzard.com/wow/ui/ ..\\FrameXML\\UI.xsd");
        writer.WriteStartElement("Frame");
        writer.WriteAttributeString("name", root.RuntimeName);
        writer.WriteAttributeString("parent", root.ExternalHostName);
        if (root.Sizing.Kind == RootSizingKind.FillHost)
        {
            writer.WriteAttributeString("setAllPoints", "true");
        }
        else
        {
            WriteSize(writer, root.Sizing.Width!.Value, root.Sizing.Height!.Value);
            writer.WriteStartElement("Anchors");
            WriteAnchor(writer, root.Sizing.Anchor!.Point, root.ExternalHostName,
                root.Sizing.Anchor.RelativePoint, root.Sizing.Anchor.OffsetX, root.Sizing.Anchor.OffsetY, true);
            writer.WriteEndElement();
        }
        WriteOwnedContents(writer, root.Children, root, byId, names, texturePaths);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Flush();
        return output.ToString() + "\n";
    }

    private static void WriteOwnedContents(
        XmlWriter writer,
        IReadOnlyList<SemanticId> children,
        CompositionRoot root,
        IReadOnlyDictionary<SemanticId, UiNode> byId,
        IReadOnlyDictionary<SemanticId, string> names,
        IReadOnlyDictionary<SemanticId, string> texturePaths)
    {
        var nodes = children.Select(id => byId[id]).ToArray();
        var regions = nodes.Where(node => node.IsRegion).ToArray();
        if (regions.Length > 0)
        {
            writer.WriteStartElement("Layers");
            foreach (var group in regions.GroupBy(node => (
                         Layer: node.AuthoredProperties.Region?.DrawLayer ?? RegionDrawLayer.Artwork,
                         Sublevel: node.AuthoredProperties.Region?.Sublevel ?? 0)))
            {
                writer.WriteStartElement("Layer");
                writer.WriteAttributeString("level", Upper(group.Key.Layer));
                if (group.Key.Sublevel != 0)
                    writer.WriteAttributeString("textureSubLevel", group.Key.Sublevel.ToString(CultureInfo.InvariantCulture));
                foreach (var region in group)
                    WriteRegion(writer, region, root, names, texturePaths);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        var frames = nodes.Where(node => !node.IsRegion).ToArray();
        if (frames.Length > 0)
        {
            writer.WriteStartElement("Frames");
            foreach (var frame in frames)
                WriteFrame(writer, frame, root, byId, names, texturePaths);
            writer.WriteEndElement();
        }
    }

    private static void WriteFrame(
        XmlWriter writer,
        UiNode node,
        CompositionRoot root,
        IReadOnlyDictionary<SemanticId, UiNode> byId,
        IReadOnlyDictionary<SemanticId, string> names,
        IReadOnlyDictionary<SemanticId, string> texturePaths)
    {
        writer.WriteStartElement(node.Kind switch
        {
            UiNodeKind.Frame => "Frame",
            UiNodeKind.Button => "Button",
            UiNodeKind.StatusBar => "StatusBar",
            _ => throw new InvalidOperationException($"'{node.Kind}' is not a frame type."),
        });
        writer.WriteAttributeString("name", names[node.Id]);
        var properties = node.AuthoredProperties.Frame;
        if (properties?.Strata is { } strata)
            writer.WriteAttributeString("frameStrata", Upper(strata));
        if (properties?.Level is { } level)
            writer.WriteAttributeString("frameLevel", level.ToString(CultureInfo.InvariantCulture));
        if (properties?.Visible == false)
            writer.WriteAttributeString("hidden", "true");
        if (node.Kind == UiNodeKind.StatusBar && node.AuthoredProperties.StatusBar is { } bar)
        {
            if (bar.Minimum is { } minimum)
                writer.WriteAttributeString("minValue", Number(minimum));
            if (bar.Maximum is { } maximum)
                writer.WriteAttributeString("maxValue", Number(maximum));
            if (bar.Value is { } value)
                writer.WriteAttributeString("defaultValue", Number(value));
        }
        WriteGeometry(writer, node, root, names, properties?.Width, properties?.Height);
        if (node.Kind == UiNodeKind.StatusBar && texturePaths.TryGetValue(node.Id, out var texture))
        {
            writer.WriteStartElement("BarTexture");
            writer.WriteAttributeString("file", texture);
            writer.WriteEndElement();
        }
        WriteOwnedContents(writer, node.Children, root, byId, names, texturePaths);
        writer.WriteEndElement();
    }

    private static void WriteRegion(
        XmlWriter writer,
        UiNode node,
        CompositionRoot root,
        IReadOnlyDictionary<SemanticId, string> names,
        IReadOnlyDictionary<SemanticId, string> texturePaths)
    {
        writer.WriteStartElement(node.Kind == UiNodeKind.Texture ? "Texture" : "FontString");
        writer.WriteAttributeString("name", names[node.Id]);
        if (node.Kind == UiNodeKind.Texture && texturePaths.TryGetValue(node.Id, out var texture))
            writer.WriteAttributeString("file", texture);
        if (node.Kind == UiNodeKind.FontString && node.AuthoredProperties.FontString is { } font)
        {
            if (!string.IsNullOrWhiteSpace(font.FontReference))
                writer.WriteAttributeString("inherits", font.FontReference);
            if (font.Text is not null)
                writer.WriteAttributeString("text", font.Text);
        }
        var region = node.AuthoredProperties.Region;
        WriteGeometry(writer, node, root, names, region?.Width, region?.Height);
        if (region?.Tint is { } tint)
        {
            writer.WriteStartElement("Color");
            writer.WriteAttributeString("r", Number(tint.Red));
            writer.WriteAttributeString("g", Number(tint.Green));
            writer.WriteAttributeString("b", Number(tint.Blue));
            writer.WriteAttributeString("a", Number(tint.Alpha));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteGeometry(XmlWriter writer, UiNode node, CompositionRoot root,
        IReadOnlyDictionary<SemanticId, string> names, double? width, double? height)
    {
        if (width is { } x && height is { } y)
            WriteSize(writer, x, y);
        if (node.Anchors.Count == 0)
            return;
        writer.WriteStartElement("Anchors");
        foreach (var anchor in node.Anchors)
        {
            var (relative, include) = anchor.Target.Kind switch
            {
                AnchorTargetKind.Parent => (string.Empty, false),
                AnchorTargetKind.CompositionRoot => (root.RuntimeName, true),
                AnchorTargetKind.LocalNode => (names[anchor.Target.NodeId!.Value], true),
                AnchorTargetKind.ExternalGlobal => (anchor.Target.GlobalName!, true),
                _ => throw new InvalidOperationException("Unresolved anchors cannot enter XML writing."),
            };
            WriteAnchor(writer, anchor.Point, relative, anchor.RelativePoint, anchor.OffsetX,
                anchor.OffsetY, include);
        }
        writer.WriteEndElement();
    }

    private static void WriteSize(XmlWriter writer, double width, double height)
    {
        writer.WriteStartElement("Size");
        writer.WriteStartElement("AbsDimension");
        writer.WriteAttributeString("x", Number(width));
        writer.WriteAttributeString("y", Number(height));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteAnchor(XmlWriter writer, Models.AnchorPoint point, string relativeTo,
        Models.AnchorPoint relativePoint, double offsetX, double offsetY, bool includeRelativeTo)
    {
        writer.WriteStartElement("Anchor");
        writer.WriteAttributeString("point", point.ToString());
        if (includeRelativeTo)
            writer.WriteAttributeString("relativeTo", relativeTo);
        writer.WriteAttributeString("relativePoint", relativePoint.ToString());
        writer.WriteStartElement("Offset");
        writer.WriteStartElement("AbsDimension");
        writer.WriteAttributeString("x", Number(offsetX));
        writer.WriteAttributeString("y", Number(offsetY));
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string WriteManifest(
        UiDocument document,
        CompositionRoot root,
        IReadOnlyList<V2ExportedControl> controls,
        IReadOnlyList<V2ExternalDependency> dependencies,
        IReadOnlyList<V2ExportedAsset> assets,
        IReadOnlyList<V2FrameXmlDiagnostic> diagnostics,
        string xml)
    {
        var payload = new
        {
            schema = ManifestSchema,
            version = ManifestVersion,
            status = "valid",
            target = new { product = document.Target.Product, build = document.Target.Build },
            documentId = document.DocumentId.Value,
            compositionRoot = new
            {
                internalId = root.Id.Value,
                runtimeName = root.RuntimeName,
                moduleHost = root.ExternalHostName,
                designWidth = root.DesignWidth,
                designHeight = root.DesignHeight,
                sizing = new
                {
                    kind = Camel(root.Sizing.Kind),
                    width = root.Sizing.Width,
                    height = root.Sizing.Height,
                    anchor = root.Sizing.Anchor is null ? null : new
                    {
                        point = root.Sizing.Anchor.Point.ToString(),
                        relativePoint = root.Sizing.Anchor.RelativePoint.ToString(),
                        offsetX = root.Sizing.Anchor.OffsetX,
                        offsetY = root.Sizing.Anchor.OffsetY,
                    },
                },
            },
            controls = controls.Select(control => new
            {
                internalId = control.Id.Value,
                runtimeName = control.RuntimeName,
                kind = Camel(control.Kind),
                ownerInternalId = control.OwnerId.Value,
                ownerRuntimeName = control.OwnerRuntimeName,
                luaExpression = control.LuaExpression,
            }),
            externalReferences = dependencies.Select(dependency => new
            {
                dependency.Kind,
                dependency.Identity,
                dependency.ExpectedSource,
                dependency.Consumers,
            }),
            artwork = assets.Select(asset => new
            {
                source = asset.ProjectReference,
                output = asset.OutputPath,
                logicalClientPath = asset.LogicalClientPath,
                asset.Format,
                sha256 = asset.Sha256,
                asset.Consumers,
            }),
            outputs = new[]
            {
                new { path = XmlFileName, sha256 = Hash(Encoding.UTF8.GetBytes(xml)) },
            }.Concat(assets.Select(asset => new { path = asset.OutputPath, sha256 = asset.Sha256 })),
            diagnostics = diagnostics.Select(item => new
            {
                severity = Camel(item.Severity),
                item.Code,
                item.Message,
                nodeId = item.NodeId?.Value,
                item.PropertyPath,
            }),
        };
        return JsonSerializer.Serialize(payload, JsonOptions()) + "\n";
    }

    private static void ValidateGeneratedStructure(
        UiDocument document,
        CompositionRoot root,
        IReadOnlyDictionary<SemanticId, string> names,
        XDocument xml,
        List<V2FrameXmlDiagnostic> diagnostics)
    {
        XNamespace ui = "http://www.blizzard.com/wow/ui/";
        var rootElements = xml.Root?.Elements(ui + "Frame").ToArray() ?? [];
        if (rootElements.Length != 1 || (string?)rootElements[0].Attribute("name") != root.RuntimeName ||
            (string?)rootElements[0].Attribute("parent") != root.ExternalHostName)
        {
            diagnostics.Add(Error("FFV2X-XML-002",
                "Generated FrameXML must contain exactly one composition root parented to the declared module host."));
            return;
        }

        var rootElement = rootElements[0];
        var emitted = rootElement.Descendants()
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => (string)element.Attribute("name")!, StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            var runtimeName = names[node.Id];
            if (!emitted.TryGetValue(runtimeName, out var element))
            {
                diagnostics.Add(Error("FFV2X-XML-003",
                    $"Generated FrameXML omitted runtime identity '{runtimeName}'.", node.Id));
                continue;
            }
            var expectedTag = node.Kind switch
            {
                UiNodeKind.Frame => "Frame",
                UiNodeKind.Texture => "Texture",
                UiNodeKind.FontString => "FontString",
                UiNodeKind.Button => "Button",
                UiNodeKind.StatusBar => "StatusBar",
                _ => string.Empty,
            };
            if (element.Name.LocalName != expectedTag)
                diagnostics.Add(Error("FFV2X-XML-004",
                    $"Runtime identity '{runtimeName}' was emitted as {element.Name.LocalName}, expected {expectedTag}.",
                    node.Id));

            var ownerName = node.Owner.Kind == OwnerKind.CompositionRoot
                ? root.RuntimeName
                : names[node.Owner.Id];
            var structuralOwner = element.Ancestors()
                .FirstOrDefault(ancestor => (string?)ancestor.Attribute("name") is not null);
            if ((string?)structuralOwner?.Attribute("name") != ownerName)
                diagnostics.Add(Error("FFV2X-XML-005",
                    $"Runtime identity '{runtimeName}' is not structurally nested under owner '{ownerName}'.",
                    node.Id, "owner"));
            if (node.IsRegion && element.Parent?.Name.LocalName != "Layer")
                diagnostics.Add(Error("FFV2X-XML-006",
                    $"Region '{runtimeName}' is not contained by a native Layer.", node.Id));
            if (!node.IsRegion && element.Parent?.Name.LocalName != "Frames")
                diagnostics.Add(Error("FFV2X-XML-007",
                    $"Frame '{runtimeName}' is not contained by a native Frames collection.", node.Id));
        }

        var localNames = names.Values.Append(root.RuntimeName).ToHashSet(StringComparer.Ordinal);
        var externalNames = document.ExternalReferences.Select(item => item.GlobalName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var anchor in rootElement.Descendants(ui + "Anchor"))
        {
            var relativeTo = (string?)anchor.Attribute("relativeTo");
            if (relativeTo is not null && !localNames.Contains(relativeTo) && !externalNames.Contains(relativeTo))
                diagnostics.Add(Error("FFV2X-XML-008",
                    $"Generated anchor target '{relativeTo}' is neither an exported identity nor a declared external global."));
        }
    }

    private static void PublishIntoExistingDirectory(string staging, string destination,
        V2FrameXmlExportPlan plan)
    {
        Directory.CreateDirectory(destination);
        var existingManifest = Path.Combine(destination, ManifestFileName);
        var previouslyManaged = ReadManagedOutputs(existingManifest);
        foreach (var fixedOutput in new[] { XmlFileName, ManifestFileName })
        {
            if (File.Exists(Path.Combine(destination, fixedOutput)) && previouslyManaged.Count == 0)
                throw new InvalidDataException(
                    $"Export would overwrite '{fixedOutput}', which is not owned by a previous v2 export.");
        }
        foreach (var asset in plan.Assets)
        {
            var target = ResolvePackagePath(destination, asset.OutputPath);
            if (File.Exists(target) && !previouslyManaged.Contains(asset.OutputPath))
                throw new InvalidDataException(
                    $"Export would overwrite '{asset.OutputPath}', which is not owned by a previous v2 export.");
        }
        foreach (var relative in plan.Assets.Select(asset => asset.OutputPath)
                     .Append(XmlFileName).Append(ManifestFileName))
        {
            var source = ResolvePackagePath(staging, relative);
            var target = ResolvePackagePath(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
        foreach (var stale in previouslyManaged.Except(plan.Assets.Select(asset => asset.OutputPath), StringComparer.Ordinal)
                     .Where(path => path.StartsWith("Artwork/", StringComparison.Ordinal)))
        {
            var path = ResolvePackagePath(destination, stale);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static HashSet<string> ReadManagedOutputs(string manifestPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(manifestPath))
            return result;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (json.RootElement.TryGetProperty("schema", out var schema) &&
                schema.GetString() == ManifestSchema &&
                json.RootElement.TryGetProperty("outputs", out var outputs))
            {
                foreach (var item in outputs.EnumerateArray())
                    if (item.TryGetProperty("path", out var path) && path.GetString() is { } value)
                        result.Add(value);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return result;
    }

    internal static string ResolvePackagePath(string root, string relative)
    {
        var portable = relative.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            Path.IsPathRooted(portable) || Regex.IsMatch(relative, @"^[A-Za-z]:[\\/]"))
            throw new InvalidDataException($"Export path '{relative}' is not portable.");
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var result = Path.GetFullPath(Path.Combine(rootPath, portable));
        var fromRoot = Path.GetRelativePath(rootPath, result);
        if (fromRoot == ".." || fromRoot.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new InvalidDataException($"Export path '{relative}' escapes the destination.");
        return result;
    }

    private static V2FrameXmlDiagnostic Error(string code, string message, SemanticId? id = null,
        string? path = null) => new(DiagnosticSeverity.Error, code, message, id, path);
    private static string TexturePropertyPath(UiNode node) => node.Kind == UiNodeKind.StatusBar
        ? "authoredProperties.statusBar.textureReference"
        : "authoredProperties.texture.textureReference";
    private static bool IsRuntimeName(string value) => RuntimeNameRegex().IsMatch(value);
    private static string Compact(SemanticId id) => id.Value.Replace("-", string.Empty, StringComparison.Ordinal);
    private static bool IsClientPath(string value) => NormalizeClientPath(value)
        .StartsWith("Interface\\", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeClientPath(string value) => value.Trim().Replace('/', '\\');
    private static bool IsSafeClientPath(string value)
    {
        if (!value.StartsWith("Interface\\", StringComparison.OrdinalIgnoreCase) || value.Contains(':'))
            return false;
        var segments = value.Split('\\');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }
    private static bool HasSupportedArtworkHeader(byte[] bytes, string extension)
    {
        if (extension == ".blp")
            return bytes.Length >= 4 &&
                   (bytes.AsSpan(0, 4).SequenceEqual("BLP1"u8) || bytes.AsSpan(0, 4).SequenceEqual("BLP2"u8));
        if (extension != ".tga" || bytes.Length < 18)
            return false;
        var imageType = bytes[2];
        var width = bytes[12] | bytes[13] << 8;
        var height = bytes[14] | bytes[15] << 8;
        var depth = bytes[16];
        return imageType is 2 or 3 or 10 or 11 && width > 0 && height > 0 && depth is 8 or 16 or 24 or 32;
    }
    private static string SafeFileStem(string value)
    {
        var safe = FileStemRegex().Replace(value.Trim(), "_").Trim('_').ToLowerInvariant();
        return safe.Length == 0 ? "asset" : safe;
    }
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private static string Upper<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
    private static string Camel<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex RuntimeNameRegex();
    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex FileStemRegex();
}
