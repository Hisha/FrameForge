using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Globalization;
using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;

namespace FrameForge.Core.Export;

public sealed record FunctionalDesignExportResult(
    bool Success,
    string? Xml,
    WowExportModel? DesignModel,
    int DesignObjectCount,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

/// <summary>
/// Composes an authored DESIGN project into a copy of authoritative functional FrameXML. The
/// source document remains the owner of scripts, templates, identities, and parent relationships;
/// the generated composition is an additional child of an explicitly associated source frame.
/// </summary>
public static class FunctionalDesignExporter
{
    public static FunctionalDesignExportResult Prepare(Project design, string? projectPath, string sourceXml)
    {
        var diagnostics = new List<ExportDiagnostic>();
        if (design.FunctionalExport is not { } profile)
            return Failure("FUNCTIONAL_SOURCE_REQUIRED", "Associate this DESIGN project with functional FrameXML before exporting.");

        var actualHash = FrameXmlImporter.ComputeSha256(sourceXml);
        if (string.IsNullOrWhiteSpace(profile.Source.Sha256)
            || !string.Equals(actualHash, profile.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            return Failure("FUNCTIONAL_SOURCE_HASH_MISMATCH",
                "The associated FrameXML changed since it was selected. Associate the current source again before exporting.");

        var imported = FrameXmlImporter.Import(sourceXml, profile.Source.FileName ?? "functional.xml");
        if (!imported.Ok || imported.Project is null)
            return Failure("FUNCTIONAL_SOURCE_INVALID", "The associated FrameXML could not be imported: " + imported.SummaryText);
        var source = imported.Project;
        var sourceNames = source.Frames.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (!sourceNames.Contains(profile.HostFrameName))
            diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_HOST_MISSING",
                $"Functional host '{profile.HostFrameName}' does not exist in the associated FrameXML."));

        var model = Wow335ExportBuilder.Build(design, projectPath);
        diagnostics.AddRange(model.Diagnostics);
        var authoredNames = model.Objects.Select(item => item.SourceName).ToHashSet(StringComparer.Ordinal);

        var generatedIdentities = model.Objects.SelectMany(item =>
                item.Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING
                    ? new[] { item.RuntimeName, item.WrapperName }
                    : new[] { item.RuntimeName })
            .Prepend(model.RootRuntimeName).ToArray();
        foreach (var duplicate in generatedIdentities.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_GENERATED_IDENTITY_COLLISION",
                $"Generated FrameXML identity '{duplicate.Key}' would be declared more than once."));
        foreach (var collision in generatedIdentities.Where(sourceNames.Contains).Distinct(StringComparer.Ordinal))
            diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_IDENTITY_COLLISION",
                $"Generated FrameXML identity '{collision}' already exists in the functional source."));

        var stateMap = profile.States.ToDictionary(item => item.StateId, StringComparer.Ordinal);
        foreach (var binding in stateMap.Values)
        {
            if (!sourceNames.Contains(binding.SourceFrameName))
                diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_STATE_SOURCE_MISSING",
                    $"State '{binding.StateId}' refers to missing source frame '{binding.SourceFrameName}'."));
            if (binding.TextEquals is null && binding.Visible is null)
                diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_STATE_PROBE_EMPTY",
                    $"State '{binding.StateId}' needs a text or visibility condition."));
        }

        var valueMap = profile.Values.ToDictionary(item => item.DesignFrameName, StringComparer.Ordinal);
        foreach (var item in model.Objects.Where(item => item.Metadata.RuntimeValueRequired))
        {
            if (!valueMap.TryGetValue(item.SourceName, out var binding))
                continue;
            var sourceFrame = source.Find(binding.SourceFrameName);
            if (sourceFrame is null)
                diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_VALUE_SOURCE_MISSING",
                    $"Runtime DESIGN object '{item.SourceName}' refers to missing source frame '{binding.SourceFrameName}'.", item.SourceName));
            else if (item.Kind == FrameKind.STATUSBAR && sourceFrame.Kind != FrameKind.STATUSBAR
                     || item.Kind == FrameKind.FONTSTRING && sourceFrame.Kind != FrameKind.FONTSTRING)
                diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_VALUE_TYPE_MISMATCH",
                    $"'{item.SourceName}' ({item.Kind.TagName()}) cannot mirror '{binding.SourceFrameName}' ({sourceFrame.Kind.TagName()}).", item.SourceName));
        }

        var layout = LayoutResolver.Resolve(design);
        var foundation = FindFoundation(design, layout);
        if (foundation is null)
            diagnostics.Add(new(ExportSeverity.Error, "FUNCTIONAL_FOUNDATION_UNRESOLVED",
                "The DESIGN project needs one resolved stock-framework foundation so its existing geometry can be rebased into the functional host."));

        if (diagnostics.Any(item => item.Severity == ExportSeverity.Error))
            return new(false, null, model, 0, diagnostics);

        var rebased = Rebase(model, layout, foundation!.Value, authoredNames);
        var generatedRoot = ExtractGeneratedRoot(Wow335Exporter.WriteXml(rebased));
        generatedRoot.SetAttributeValue("parent", profile.HostFrameName);
        generatedRoot.SetAttributeValue("setAllPoints", "true");
        PreserveUnmappedDefaults(generatedRoot, rebased, profile);
        if (HasRuntimeBridge(rebased, profile))
            AddRuntimeBridge(generatedRoot, rebased, profile);

        var insertion = "\n\t" + generatedRoot.ToString(SaveOptions.DisableFormatting).Replace("\n", "\n\t") + "\n";
        var close = sourceXml.LastIndexOf("</Ui>", StringComparison.Ordinal);
        if (close < 0)
            return Failure("FUNCTIONAL_SOURCE_INVALID", "The associated FrameXML has no closing Ui element.");
        var output = sourceXml.Insert(close, insertion);

        var verify = FrameXmlImporter.Import(output, profile.Source.FileName ?? "functional.xml");
        if (!verify.Ok || verify.Project is null)
            return Failure("FUNCTIONAL_VERIFY_FAILED", "The composed XML failed re-import: " + verify.SummaryText);
        var verifiedNames = verify.Project.Frames.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var missing = rebased.Objects.Select(item => item.RuntimeName).Where(name => !verifiedNames.Contains(name)).ToArray();
        if (missing.Length > 0)
            return Failure("FUNCTIONAL_VERIFY_FAILED",
                $"The composed XML lost {missing.Length} design identity/identities: {string.Join(", ", missing.Take(5))}.");

        return new(true, output, rebased, rebased.Objects.Count, diagnostics);

        FunctionalDesignExportResult Failure(string code, string message)
        {
            diagnostics.Add(new(ExportSeverity.Error, code, message));
            return new(false, null, null, 0, diagnostics);
        }
    }

    private static FrameRect? FindFoundation(Project design, LayoutResult layout)
    {
        var stock = design.Editor.Groups.FirstOrDefault(group => group.Concept == "stock-framework");
        if (stock is null)
            return null;
        foreach (var name in stock.Members)
            if (layout.Rects.TryGetValue(name, out var rect) && rect.Width > 0 && rect.Height > 0)
                return rect;
        return null;
    }

    private static WowExportModel Rebase(WowExportModel model, LayoutResult layout, FrameRect foundation,
        IReadOnlySet<string> authoredNames)
    {
        var objects = model.Objects.Select(item =>
        {
            if (item.Frame.Parent is { } parent && authoredNames.Contains(parent)
                || item.Frame.RelativeTo is { } relative && authoredNames.Contains(relative))
                return item;
            if (!layout.Rects.TryGetValue(item.SourceName, out var rect))
                return item;
            var frame = item.Frame with
            {
                Parent = null,
                Point = AnchorPoint.CENTER,
                RelativeTo = null,
                RelativePoint = AnchorPoint.CENTER,
                OffsetX = rect.CenterX - foundation.CenterX,
                OffsetY = rect.CenterY - foundation.CenterY,
                ExtraAnchors = [],
                SetAllPoints = false,
            };
            return item with { Frame = frame, ParentRuntimeName = null, RelativeRuntimeName = null };
        }).ToArray();
        return model with { Objects = objects };
    }

    private static XElement ExtractGeneratedRoot(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return new XElement(document.Root!.Elements().Single());
    }

    private static bool HasRuntimeBridge(WowExportModel model, FunctionalExportProfile profile)
    {
        if (profile.States.Count > 0)
            return true;
        var mapped = profile.Values.Select(item => item.DesignFrameName).ToHashSet(StringComparer.Ordinal);
        return model.Objects.Any(item => item.Metadata.RuntimeValueRequired && mapped.Contains(item.SourceName));
    }

    private static void PreserveUnmappedDefaults(XElement root, WowExportModel model, FunctionalExportProfile profile)
    {
        var configured = profile.Values.Select(item => item.DesignFrameName).ToHashSet(StringComparer.Ordinal);
        var mapped = model.Objects.Where(item => item.Metadata.RuntimeValueRequired && configured.Contains(item.SourceName))
            .Select(item => item.SourceName).ToHashSet(StringComparer.Ordinal);
        foreach (var item in model.Objects.Where(item => item.Kind == FrameKind.STATUSBAR
                     && !mapped.Contains(item.SourceName)
                     && item.Frame.Visual?.StatusBar?.DefaultValue is not null))
        {
            var element = root.DescendantsAndSelf()
                .Single(node => string.Equals((string?)node.Attribute("name"), item.RuntimeName, StringComparison.Ordinal));
            element.SetAttributeValue("defaultValue",
                item.Frame.Visual!.StatusBar!.DefaultValue!.Value.ToString("0.################", CultureInfo.InvariantCulture));
        }
    }

    private static void AddRuntimeBridge(XElement root, WowExportModel model, FunctionalExportProfile profile)
    {
        XNamespace ns = root.Name.Namespace;
        var scripts = new XElement(ns + "Scripts");
        scripts.Add(new XElement(ns + "OnLoad", "self.__ffElapsed = 0;"));
        scripts.Add(new XElement(ns + "OnUpdate", BuildBridge(model, profile)));
        root.Add(scripts);
    }

    private static string BuildBridge(WowExportModel model, FunctionalExportProfile profile)
    {
        var b = new StringBuilder();
        b.Append("self.__ffElapsed = (self.__ffElapsed or 0) + elapsed; if self.__ffElapsed < 0.1 then return; end self.__ffElapsed = 0; ");
        b.Append("local ffState = nil; ");
        foreach (var binding in profile.States)
        {
            var source = Wow335ExportBuilder.SafeIdentifier(binding.SourceFrameName);
            var checks = new List<string> { source };
            if (binding.Visible is { } visible)
                checks.Add(visible ? source + ":IsShown()" : "not " + source + ":IsShown()");
            if (binding.TextEquals is { } text)
                checks.Add($"string.upper(tostring({source}:GetText() or '')) == {LuaString(text.ToUpperInvariant())}");
            b.Append($"if {string.Join(" and ", checks)} then ffState = {LuaString(binding.StateId)}; end ");
        }
        foreach (var item in model.Objects.Where(item => item.StateIds.Count > 0))
        {
            var target = item.Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING ? item.WrapperName : item.RuntimeName;
            var matches = string.Join(" or ", item.StateIds.Select(id => $"ffState == {LuaString(id)}"));
            b.Append($"if {target} then if {matches} then {target}:Show(); else {target}:Hide(); end end ");
        }
        var values = profile.Values.ToDictionary(item => item.DesignFrameName, StringComparer.Ordinal);
        foreach (var item in model.Objects.Where(item => item.Metadata.RuntimeValueRequired))
        {
            if (!values.TryGetValue(item.SourceName, out var binding)) continue;
            var source = Wow335ExportBuilder.SafeIdentifier(binding.SourceFrameName);
            var target = item.RuntimeName;
            var getter = item.Kind == FrameKind.STATUSBAR ? "GetValue" : "GetText";
            var setter = item.Kind == FrameKind.STATUSBAR ? "SetValue" : "SetText";
            b.Append($"if {source} and {target} then {target}:{setter}({source}:{getter}()); end ");
        }
        return b.ToString();
    }

    private static string LuaString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

public sealed record FunctionalDesignPackageResult(
    bool Success,
    FunctionalDesignExportResult Preparation,
    IReadOnlyList<string> Files);

/// <summary>
/// Writes a verified functional composition and its project-owned artwork. The authoritative XML
/// file name is retained so an addon's existing load order and Script declarations stay valid.
/// Existing files are accepted only when they belong to a prior FrameForge export.
/// </summary>
public static class FunctionalDesignPackageExporter
{
    public static FunctionalDesignPackageResult Export(
        Project project, string? projectPath, string sourceXml, string destination)
    {
        var prepared = FunctionalDesignExporter.Prepare(project, projectPath, sourceXml);
        if (!prepared.Success || prepared.Xml is null || prepared.DesignModel is null)
            return new(false, prepared, []);

        var model = prepared.DesignModel;
        var sourceName = Path.GetFileName(project.FunctionalExport!.Source.FileName);
        if (string.IsNullOrWhiteSpace(sourceName)
            || !sourceName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The associated functional source has no portable .xml file name.");

        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Directory.CreateDirectory(destinationRoot);
        var staging = Path.Combine(destinationRoot, $".frameforge-functional-staging-{Guid.NewGuid():N}");
        var finalFiles = new[]
        {
            Path.Combine(destinationRoot, sourceName),
            Path.Combine(destinationRoot, Wow335Exporter.ManifestFileName),
            Path.Combine(destinationRoot, Wow335Exporter.AssetsFileName),
            Path.Combine(destinationRoot, Wow335Exporter.ReportFileName),
        };
        var published = new List<string>();
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var asset in model.Assets.GroupBy(item => item.PackageSource, StringComparer.Ordinal).Select(group => group.First()))
            {
                var target = Wow335Exporter.ResolvePackagePath(staging, asset.PackageSource);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(asset.SourcePhysicalPath, target, true);
                using var copied = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                var copiedHash = Convert.ToHexString(SHA256.HashData(copied));
                if (!string.Equals(copiedHash, asset.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Project artwork '{asset.ProjectReferences[0]}' changed during export.");
            }

            var stagedXml = Path.Combine(staging, sourceName);
            var stagedManifest = Path.Combine(staging, Wow335Exporter.ManifestFileName);
            var stagedAssets = Path.Combine(staging, Wow335Exporter.AssetsFileName);
            var stagedReport = Path.Combine(staging, Wow335Exporter.ReportFileName);
            File.WriteAllText(stagedXml, prepared.Xml, new UTF8Encoding(false));
            File.WriteAllText(stagedManifest, FunctionalManifest(model, sourceName, project.FunctionalExport, prepared.Xml), new UTF8Encoding(false));
            File.WriteAllText(stagedAssets, Wow335Exporter.WriteAssets(model), new UTF8Encoding(false));
            File.WriteAllText(stagedReport, FunctionalReport(model, sourceName, prepared.DesignObjectCount), new UTF8Encoding(false));

            var verify = FrameXmlImporter.ImportFile(stagedXml);
            if (!verify.Ok || verify.Project is null)
                throw new InvalidDataException("The staged functional FrameXML failed its independent importer check.");
            var names = verify.Project.Frames.Select(frame => frame.Name).ToHashSet(StringComparer.Ordinal);
            var missing = model.Objects.Select(item => item.RuntimeName).Where(name => !names.Contains(name)).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"The staged FrameXML lost {missing.Length} generated runtime identity/identities.");

            var stagedFiles = new[] { stagedXml, stagedManifest, stagedAssets, stagedReport };
            GuardDestination(destinationRoot, staging);
            foreach (var asset in model.Assets.GroupBy(item => item.PackageSource, StringComparer.Ordinal).Select(group => group.First()))
            {
                var target = Wow335Exporter.ResolvePackagePath(destinationRoot, asset.PackageSource);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                published.Add(target);
                File.Copy(Wow335Exporter.ResolvePackagePath(staging, asset.PackageSource), target, true);
            }
            for (var index = 0; index < stagedFiles.Length; index++)
            {
                published.Add(finalFiles[index]);
                File.Copy(stagedFiles[index], finalFiles[index], true);
            }
            return new(true, prepared, finalFiles);
        }
        catch
        {
            foreach (var path in published)
                if (File.Exists(path))
                    File.Delete(path);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static void GuardDestination(string destinationRoot, string staging)
    {
        var existing = Directory.EnumerateFileSystemEntries(destinationRoot)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
            .Take(1).ToArray();
        if (existing.Length > 0)
            throw new InvalidDataException(
                $"Functional export requires an empty directory and found '{Path.GetFileName(existing[0])}'.");
    }

    private static string FunctionalManifest(WowExportModel model, string sourceName, FunctionalExportProfile profile, string exportedXml)
    {
        var root = JsonNode.Parse(Wow335Exporter.WriteManifest(model))!.AsObject();
        root["layout"] = sourceName;
        root["exportMode"] = "functional-source-composition";
        root["functionalHost"] = profile.HostFrameName;
        root["functionalSourceSha256"] = profile.Source.Sha256;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        root["stateProbes"] = JsonSerializer.SerializeToNode(profile.States, options);
        root["valueSources"] = JsonSerializer.SerializeToNode(profile.Values, options);
        root["controlInventory"] = BuildControlInventory(model, exportedXml);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray BuildControlInventory(WowExportModel model, string exportedXml)
    {
        var document = XDocument.Parse(exportedXml, LoadOptions.PreserveWhitespace);
        var inventory = new JsonArray();
        foreach (var item in model.Objects.OrderBy(item => item.DrawOrder).ThenBy(item => item.RuntimeName, StringComparer.Ordinal))
        {
            // Authoritative XML commonly repeats parent-relative source names such as
            // "$parentBackground". Search only for already collision-checked generated names.
            var element = document.Descendants().Single(node =>
                string.Equals((string?)node.Attribute("name"), item.RuntimeName, StringComparison.Ordinal));
            var parentPath = string.Join("/", element.Ancestors().Reverse()
                .Select(ancestor => (string?)ancestor.Attribute("name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)));
            inventory.Add(new JsonObject
            {
                ["authoredName"] = item.Metadata.DisplayName ?? item.SourceName,
                ["sourceObject"] = item.SourceName,
                ["exportedName"] = (string)element.Attribute("name")!,
                ["type"] = element.Name.LocalName,
                ["parentPath"] = parentPath,
                ["luaAccess"] = $"_G[{JsonSerializer.Serialize(item.RuntimeName)}]",
            });
        }
        return inventory;
    }

    private static string FunctionalReport(WowExportModel model, string sourceName, int objectCount) =>
        Wow335Exporter.WriteReport(model)
        + $"\nExport mode: functional source composition\nAuthoritative FrameXML: {sourceName}\n"
        + $"Composed design objects: {objectCount}\nSource scripts, templates, hierarchy, and identities: preserved\n"
        + "Control inventory: frameforge-manifest.json > controlInventory\n"
        + "Packaging note: keep the addon's existing Lua files and TOC/XML load-order entries; FrameForge does not copy or generate application Lua.\n";
}
