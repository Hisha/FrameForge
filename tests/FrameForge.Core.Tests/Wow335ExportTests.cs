using System.Buffers.Binary;
using System.Xml.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrameForge.Core.Export;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using SkiaSharp;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class Wow335ExportTests
{
    [Fact]
    public void DungeonFinderFoundationOwnsExportRootWithoutFlatteningCustomRelationships()
    {
        var project = new Project
        {
            Name = "Dungeon Finder UI",
            Frames =
            [
                new() { Name = "LFDParentFrame", Width = 355, Height = 440, Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER },
                new() { Name = "StockArtwork", Parent = "LFDParentFrame", Kind = FrameKind.TEXTURE, Width = 64, Height = 64, Visual = new FrameVisual { Texture = new TextureVisual(@"Interface\LFGFrame\UI-LFG-FRAME") } },
                new() { Name = "CustomPanel", Parent = "LFDParentFrame", Width = 300, Height = 80, Point = AnchorPoint.TOP, RelativePoint = AnchorPoint.TOP, OffsetY = -20 },
                new() { Name = "Header", Parent = "CustomPanel", Kind = FrameKind.FONTSTRING, Width = 270, Height = 18, Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER, OffsetX = 7, OffsetY = -3, Visual = new FrameVisual { Text = new TextVisual("Custom") } },
            ],
            Editor = new EditorMetadata
            {
                Groups =
                [
                    new EditorGroup
                    {
                        Name = "Blizzard Dungeon Finder Frame",
                        Concept = "stock-framework",
                        StockIdentity = "wow-3.3.5a-12340:Interface/FrameXML/LFDFrame.xml:LFDParentFrame",
                        Locked = true,
                        Members = ["LFDParentFrame", "StockArtwork"],
                    },
                ],
                DesignOrder = ["CustomPanel", "Header"],
                DesignObjects =
                [
                    new() { FrameName = "LFDParentFrame", DisplayName = "Blizzard Dungeon Finder Frame" },
                    new() { FrameName = "CustomPanel" },
                    new() { FrameName = "Header" },
                ],
            },
        };

        var model = Wow335ExportBuilder.Build(project, null);
        var xml = XDocument.Parse(Wow335Exporter.WriteXml(model));
        XNamespace ns = "http://www.blizzard.com/wow/ui/";
        var root = Assert.Single(xml.Root!.Elements(ns + "Frame"));
        var rootAnchor = Assert.Single(root.Element(ns + "Anchors")!.Elements(ns + "Anchor"));
        var panel = Assert.Single(model.Objects, item => item.SourceName == "CustomPanel");
        var header = Assert.Single(model.Objects, item => item.SourceName == "Header");

        Assert.Equal("LFDParentFrame", root.Attribute("parent")!.Value);
        Assert.Equal("LFDParentFrame", rootAnchor.Attribute("relativeTo")!.Value);
        Assert.Equal(AnchorPoint.TOPLEFT, model.Root.Anchor.Point);
        Assert.Equal(0, model.Root.Anchor.OffsetX);
        Assert.Equal(0, model.Root.Anchor.OffsetY);
        Assert.Equal(355, model.Root.Width);
        Assert.Equal(440, model.Root.Height);
        Assert.Equal(model.RootRuntimeName, panel.ExportFrame.Parent);
        Assert.Equal(AnchorPoint.TOP, panel.ExportFrame.Point);
        Assert.Equal(AnchorPoint.TOP, panel.ExportFrame.RelativePoint);
        Assert.Equal(0, panel.ExportFrame.OffsetX);
        Assert.Equal(-20, panel.ExportFrame.OffsetY);
        Assert.Equal(panel.RuntimeName, header.ExportFrame.Parent);
        Assert.Null(header.ExportFrame.RelativeTo);
        Assert.Equal(7, header.ExportFrame.OffsetX);
        Assert.Equal(-3, header.ExportFrame.OffsetY);
        Assert.DoesNotContain("StockArtwork", xml.ToString());
        Assert.DoesNotContain(@"Interface\LFGFrame\UI-LFG-FRAME", xml.ToString());

        using var manifest = JsonDocument.Parse(Wow335Exporter.WriteManifest(model));
        Assert.Equal("LFDParentFrame", manifest.RootElement.GetProperty("rootParentRuntimeId").GetString());
        Assert.Equal("LFDParentFrame", manifest.RootElement.GetProperty("stockFoundation").GetProperty("runtimeParent").GetString());
    }

    [Fact]
    public void RootIsIndependentAndScreenAuthoredObjectsAreRebasedToIt()
    {
        using var fixture = ExportFixture.Create();
        var model = Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath);
        var xml = XDocument.Parse(Wow335Exporter.WriteXml(model));
        XNamespace ns = "http://www.blizzard.com/wow/ui/";
        var root = Assert.Single(xml.Root!.Elements(ns + "Frame"));

        Assert.Equal(model.RootRuntimeName, root.Attribute("name")!.Value);
        Assert.Equal("UIParent", root.Attribute("parent")!.Value);
        Assert.Null(root.Attribute("setAllPoints"));
        Assert.DoesNotContain("LFDParentFrame", xml.ToString());
        Assert.All(model.Objects, item => Assert.Equal(model.RootRuntimeName, item.ExportFrame.Parent));
        Assert.All(root.Descendants(ns + "Anchor").Skip(1), anchor =>
            Assert.Equal(model.RootRuntimeName, anchor.Attribute("relativeTo")!.Value));
    }

    [Fact]
    public void RebasingPreservesGeometryAndMovingRootMovesEveryTopLevelObject()
    {
        using var fixture = ExportFixture.Create();
        var model = Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath);
        var sourceLayout = LayoutResolver.Resolve(fixture.Project);
        var rootFrame = new FrameDef
        {
            Name = model.RootRuntimeName,
            Width = model.Root.Width,
            Height = model.Root.Height,
            Point = model.Root.Anchor.Point,
            RelativePoint = model.Root.Anchor.RelativePoint,
            OffsetX = model.Root.Anchor.OffsetX,
            OffsetY = model.Root.Anchor.OffsetY,
        };
        var rootRect = LayoutResolver.ResolveFrameRect(rootFrame,
            LayoutResolver.ScreenRect(fixture.Project.Screen), null);
        const double dx = 73;
        const double dy = -41;

        foreach (var item in model.Objects)
        {
            var exported = LayoutResolver.ResolveFrameRect(item.ExportFrame,
                target => target == model.RootRuntimeName ? rootRect : null, null, out var notes);
            Assert.Empty(notes);
            var source = sourceLayout.Rects[item.SourceName];
            Assert.Equal(source, exported);

            var moved = LayoutResolver.ResolveFrameRect(item.ExportFrame,
                target => target == model.RootRuntimeName ? rootRect.Translate(dx, dy) : null, null, out notes);
            Assert.Empty(notes);
            Assert.Equal(exported.Translate(dx, dy), moved);
        }
    }

    [Fact]
    public void NestedFramesOwnRegionsAndReferencesUseActualNativeOwners()
    {
        var project = new Project
        {
            Name = "Nested",
            Frames =
            [
                new() { Name = "Container", Kind = FrameKind.FRAME, Width = 200, Height = 100, Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER },
                new() { Name = "Bar", Parent = "Container", Kind = FrameKind.STATUSBAR, Width = 100, Height = 10, Visual = new FrameVisual { StatusBar = new StatusBarVisual(0, 100, 0, null, null) } },
                new() { Name = "Texture", Parent = "Container", RelativeTo = "Bar", Kind = FrameKind.TEXTURE, Width = 16, Height = 16 },
                new() { Name = "Label", Parent = "Container", Kind = FrameKind.FONTSTRING, Width = 80, Height = 20, Visual = new FrameVisual { Text = new TextVisual("Label") } },
            ],
            Editor = new EditorMetadata
            {
                DesignOrder = ["Container", "Bar", "Texture", "Label"],
                DesignObjects =
                [
                    new() { FrameName = "Container" },
                    new() { FrameName = "Bar" },
                    new() { FrameName = "Texture" },
                    new() { FrameName = "Label" },
                ],
            },
        };

        var model = Wow335ExportBuilder.Build(project, null);
        var container = Assert.Single(model.Objects, item => item.SourceName == "Container");
        var bar = Assert.Single(model.Objects, item => item.SourceName == "Bar");
        var texture = Assert.Single(model.Objects, item => item.SourceName == "Texture");
        var label = Assert.Single(model.Objects, item => item.SourceName == "Label");
        Assert.Equal(container.RuntimeName, bar.ExportFrame.Parent);
        Assert.Equal(container.RuntimeName, texture.ExportFrame.Parent);
        Assert.Equal(bar.RuntimeName, texture.ExportFrame.RelativeTo);
        Assert.Equal(container.RuntimeName, label.ExportFrame.Parent);
        Assert.EndsWith("__FFLayer", texture.OwnerRuntimeName, StringComparison.Ordinal);
        Assert.EndsWith("__FFLayer", label.OwnerRuntimeName, StringComparison.Ordinal);
        Assert.Equal(bar.RuntimeName, bar.OwnerRuntimeName);
        Assert.True(bar.FrameLevel > container.FrameLevel);
    }

    [Fact]
    public void StateVisibilityTargetsRegionOwnersAndBindingsTargetRegions()
    {
        using var fixture = ExportFixture.Create();
        var model = Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath);
        using var manifest = JsonDocument.Parse(Wow335Exporter.WriteManifest(model));
        var state = manifest.RootElement.GetProperty("states")[0];
        Assert.Contains("Bad_Name__FFLayer", state.GetProperty("show").EnumerateArray().Select(item => item.GetString()));
        var binding = manifest.RootElement.GetProperty("bindings").EnumerateArray()
            .Single(item => item.GetProperty("sourceObject").GetString() == "Text1");
        Assert.Equal("Bad_Name", binding.GetProperty("runtimeId").GetString());
        Assert.Equal("Bad_Name__FFLayer", binding.GetProperty("visibilityRuntimeId").GetString());
    }

    [Fact]
    public void SaveReopenKeepsGeneratedNativeLayoutIdentical()
    {
        using var fixture = ExportFixture.Create();
        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(fixture.Project));
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal(
            Wow335Exporter.WriteXml(Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath)),
            Wow335Exporter.WriteXml(Wow335ExportBuilder.Build(reopened.Project!, fixture.ProjectPath)));
    }

    [Fact]
    public void ExportIsDeterministicPortableAndWellFormed()
    {
        using var fixture = ExportFixture.Create();
        var first = Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1);
        var second = Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output2);
        Assert.True(first.Success, string.Join("\n", first.Diagnostics));
        foreach (var file in first.WrittenFiles.Select(Path.GetFileName))
            Assert.Equal(File.ReadAllText(Path.Combine(fixture.Output1, file!)), File.ReadAllText(Path.Combine(fixture.Output2, file!)));
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Output1, "assets", "panel.png")),
            File.ReadAllBytes(Path.Combine(fixture.Output2, "assets", "panel.png")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Output1, "textures", "panel.tga")),
            File.ReadAllBytes(Path.Combine(fixture.Output2, "textures", "panel.tga")));
        _ = XDocument.Load(Path.Combine(fixture.Output1, Wow335Exporter.XmlFileName));
        var all = string.Join("\n", first.WrittenFiles.Select(File.ReadAllText));
        Assert.DoesNotContain(fixture.Root, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppData", all, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output1, "*.blp", SearchOption.AllDirectories));
        Assert.Equal(new[] { Path.Combine(fixture.Output1, "assets", "panel.png") },
            Directory.EnumerateFiles(fixture.Output1, "*.png", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(fixture.Output1, "*.ttf", SearchOption.AllDirectories));
    }

    [Fact]
    public void PreferredBlp2ExportIsDeterministicMipmappedDecodedAndUsageSized()
    {
        using var fixture = ExportFixture.Create();
        var pixels = Enumerable.Range(0, 16 * 8).Select(index =>
            new SKColor((byte)(index * 17), (byte)(255 - index * 7), (byte)(index * 29),
                (byte)(index % 5 == 0 ? 0 : index % 3 == 0 ? 112 : 255))).ToArray();
        WritePng(Path.Combine(fixture.Root, "shared-rect.png"), 16, 8, pixels, withAlpha: true);
        var project = AddTextures(fixture.Project, ("SharedFull", "shared-rect.png"), ("SharedCrop", "shared-rect.png"));
        project = project with
        {
            Frames = project.Frames.Select(frame => frame.Name switch
            {
                "SharedFull" => frame with
                {
                    Width = 3,
                    Height = 1,
                    Visual = new FrameVisual { Texture = new TextureVisual(null, TexCoords.Full) },
                },
                "SharedCrop" => frame with
                {
                    Width = 2,
                    Height = 2,
                    Visual = new FrameVisual { Texture = new TextureVisual(null, new(.25, .75, 0, 1)) },
                },
                _ => frame,
            }).ToArray(),
        };

        var first = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output1, Wow335ExportOptions.Preferred);
        var second = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output2, Wow335ExportOptions.Preferred);
        Assert.True(first.Success, string.Join("\n", first.Diagnostics));
        Assert.True(second.Success, string.Join("\n", second.Diagnostics));
        var asset = Assert.Single(first.Model!.Assets, item => item.ProjectReferences.Contains("shared-rect.png"));
        Assert.Equal((16, 8, 8, 4, 8, 4, 4),
            (asset.SourceWidth, asset.SourceHeight, asset.PreparedWidth, asset.PreparedHeight,
                asset.TextureWidth, asset.TextureHeight, asset.MipCount));
        var firstPath = Path.Combine(fixture.Output1, "textures", "shared_rect.blp");
        var secondPath = Path.Combine(fixture.Output2, "textures", "shared_rect.blp");
        Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
        using (var stream = File.OpenRead(firstPath))
            Assert.Equal(4, Blp2Dxt5Converter.Verify(stream, new(16, 8, 8, 4, 8, 4, 4)).MipCount);

        var bytes = File.ReadAllBytes(firstPath);
        Assert.Equal("BLP2", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(new byte[] { 2, 8, 7, 1 }, bytes[8..12]);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output1, Wow335Exporter.AssetsFileName)));
        var entry = manifest.RootElement.GetProperty("assets").EnumerateArray()
            .Single(item => item.GetProperty("projectReferences").EnumerateArray()
                .Any(reference => reference.GetString() == "shared-rect.png"));
        Assert.Equal("textures/shared_rect.blp", entry.GetProperty("generatedFile").GetString());
        Assert.Equal("Interface/FrameForge/Fixture/shared_rect.blp", entry.GetProperty("requiredMpqDestination").GetString());
        Assert.Equal("blp2-dxt5-alpha8-mipmapped", entry.GetProperty("generatedFormat").GetString());
        Assert.True(entry.GetProperty("resized").GetBoolean());
        Assert.Equal(4, entry.GetProperty("mipCount").GetInt32());
        Assert.InRange(entry.GetProperty("decodedPremultipliedColorMeanAbsoluteError").GetDouble(), 0, 64);
        Assert.InRange(entry.GetProperty("decodedAlphaMeanAbsoluteError").GetDouble(), 0, 32);
        Assert.Contains("hidden=\"true\"", File.ReadAllText(Path.Combine(fixture.Output1, Wow335Exporter.XmlFileName)));
    }

    [Fact]
    public void Blp2PreserveSourceOptionKeepsResolutionAndConservativePolicyNeverUpscales()
    {
        using var fixture = ExportFixture.Create();
        WritePng(Path.Combine(fixture.Root, "large.png"), 16, 8,
            Enumerable.Repeat(new SKColor(50, 100, 150, 200), 16 * 8).ToArray(), withAlpha: true);
        var project = AddTextures(fixture.Project, ("Large", "large.png"));
        project = project with
        {
            Frames = project.Frames.Select(frame => frame.Name == "Large"
                ? frame with { Width = 2, Height = 1 }
                : frame).ToArray(),
        };
        var preserved = Wow335ExportBuilder.Build(project, fixture.ProjectPath,
            Wow335ExportOptions.Preferred with { ResolutionPolicy = WowTextureResolutionPolicy.PreserveSource });
        var conservative = Wow335ExportBuilder.Build(project, fixture.ProjectPath, Wow335ExportOptions.Preferred);
        var preservedAsset = Assert.Single(preserved.Assets, item => item.ProjectReferences.Contains("large.png"));
        var conservativeAsset = Assert.Single(conservative.Assets, item => item.ProjectReferences.Contains("large.png"));
        Assert.Equal((16, 8), (preservedAsset.PreparedWidth, preservedAsset.PreparedHeight));
        Assert.Equal((4, 2), (conservativeAsset.PreparedWidth, conservativeAsset.PreparedHeight));
        Assert.True(conservativeAsset.PreparedWidth <= conservativeAsset.SourceWidth);
        Assert.True(conservativeAsset.PreparedHeight <= conservativeAsset.SourceHeight);
    }

    [Fact]
    public void NamesStatesAssetsFontsGeometryAndDrawOrderAreContractData()
    {
        using var fixture = ExportFixture.Create();
        var model = Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath);
        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == ExportSeverity.Error);
        Assert.Contains(model.Diagnostics, d => d.Code == "NAME_SANITIZED");
        Assert.Contains(model.Diagnostics, d => d.Code == "NAME_COLLISION");
        Assert.Equal(new[] { "Bad_Name", "Bad_Name_2", "Progress" }, model.Objects.Select(o => o.RuntimeName));
        Assert.Equal(new[] { "tracking", "located" }, model.Objects[0].StateIds);
        Assert.Empty(model.Objects[1].StateIds);
        Assert.Equal(@"Interface\FrameForge\Fixture\panel", model.Assets.Single().LogicalTexture);
        Assert.Equal(["panel.png"], model.Assets.Single().ProjectReferences);
        Assert.Equal("assets/panel.png", model.Assets.Single().PackageSource);
        Assert.Equal("GameFontHighlight", model.Objects[0].FontStyle);
        Assert.Equal(17, model.Objects[0].FontSize);
        Assert.Equal(0, model.Objects[0].Frame.OffsetX);
        Assert.True(model.Objects[0].DrawOrder < model.Objects[1].DrawOrder);
    }

    [Fact]
    public void StatusBarUsesLogicalTextureAndDoesNotInitializePreviewValue()
    {
        using var fixture = ExportFixture.Create();
        var model = Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath);
        var xml = Wow335Exporter.WriteXml(model);
        var manifest = Wow335Exporter.WriteManifest(model);
        Assert.Contains(@"Interface\TargetingFrame\UI-StatusBar", xml);
        Assert.Contains("<BarColor r=\"0.2\" g=\"0.8\" b=\"0.2\" a=\"1\"", xml);
        Assert.DoesNotContain("defaultValue", xml);
        Assert.Contains("\"editorPreviewDefault\": 50", manifest);
        Assert.Contains("\"runtimeValueRequired\": true", manifest);
        Assert.Contains("\"runtimeBinding\": \"hunt.progress\"", manifest);
        Assert.Contains("\"valueType\": \"number\"", manifest);
        Assert.Contains("\"operation\": \"SetValue\"", manifest);
    }

    [Fact]
    public void RuntimeBindingsRoundTripAndDoNotChangeGeneratedFrameXml()
    {
        using var fixture = ExportFixture.Create();
        var unbound = fixture.Project with
        {
            Editor = fixture.Project.Editor with
            {
                DesignObjects = fixture.Project.Editor.DesignObjects.Select(item => item with
                {
                    RuntimeValueRequired = false,
                    RuntimeBinding = null,
                }).ToArray(),
            },
        };
        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(fixture.Project));
        Assert.True(reopened.Ok, reopened.ErrorText);
        var bar = reopened.Project!.Editor.DesignObjectFor("Bar")!;
        Assert.True(bar.RuntimeValueRequired);
        Assert.Equal("hunt.progress", bar.RuntimeBinding);
        Assert.Equal(Wow335Exporter.WriteXml(Wow335ExportBuilder.Build(unbound, fixture.ProjectPath)),
            Wow335Exporter.WriteXml(Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath)));
        var oldProject = ProjectCodec.Parse(ProjectCodec.Serialize(unbound));
        Assert.True(oldProject.Ok, oldProject.ErrorText);
        Assert.All(oldProject.Project!.Editor.DesignObjects, item =>
        {
            Assert.False(item.RuntimeValueRequired);
            Assert.Null(item.RuntimeBinding);
        });
    }

    [Fact]
    public void StaticObjectsDoNotReceiveInventedBindingKeys()
    {
        using var fixture = ExportFixture.Create();
        using var manifest = JsonDocument.Parse(Wow335Exporter.WriteManifest(Wow335ExportBuilder.Build(fixture.Project, fixture.ProjectPath)));
        var image = manifest.RootElement.GetProperty("bindings").EnumerateArray()
            .Single(item => item.GetProperty("sourceObject").GetString() == "Image");
        Assert.False(image.GetProperty("runtimeValueRequired").GetBoolean());
        Assert.False(image.TryGetProperty("runtimeBinding", out _));
        Assert.False(image.TryGetProperty("valueType", out _));
        Assert.False(image.TryGetProperty("operation", out _));
    }

    [Fact]
    public void InvalidMissingUnsupportedAndConflictingBindingsBlockExport()
    {
        using var fixture = ExportFixture.Create();
        var objects = fixture.Project.Editor.DesignObjects.ToArray();
        objects[0] = objects[0] with { RuntimeValueRequired = true, RuntimeBinding = "DesignObject17" };
        objects[1] = objects[1] with { RuntimeValueRequired = true, RuntimeBinding = "hunt.image" };
        objects[2] = objects[2] with { RuntimeValueRequired = true, RuntimeBinding = "hunt.shared" };
        objects[0] = objects[0] with { RuntimeBinding = "hunt.shared" };
        var invalid = fixture.Project with { Editor = fixture.Project.Editor with { DesignObjects = objects } };
        var model = Wow335ExportBuilder.Build(invalid, fixture.ProjectPath);
        Assert.Contains(model.Diagnostics, d => d.Code == "BINDING_TYPE_UNSUPPORTED");
        Assert.Contains(model.Diagnostics, d => d.Code == "BINDING_TYPE_CONFLICT");

        objects[0] = objects[0] with { RuntimeBinding = "DesignObject17" };
        objects[1] = objects[1] with { RuntimeValueRequired = false, RuntimeBinding = "hunt.image" };
        objects[2] = objects[2] with { RuntimeValueRequired = true, RuntimeBinding = null };
        model = Wow335ExportBuilder.Build(fixture.Project with { Editor = fixture.Project.Editor with { DesignObjects = objects } }, fixture.ProjectPath);
        Assert.Contains(model.Diagnostics, d => d.Code == "BINDING_KEY_INVALID");
        Assert.Contains(model.Diagnostics, d => d.Code == "BINDING_KEY_EDITOR_ID");
        Assert.Contains(model.Diagnostics, d => d.Code == "BINDING_WITHOUT_RUNTIME_VALUE");
        Assert.Contains(model.Diagnostics, d => d.Code == "BINDING_REQUIRED");
    }

    [Fact]
    public void CompatibleFontStringsMayShareOneSemanticBinding()
    {
        using var fixture = ExportFixture.Create();
        var extra = fixture.Project.Frames[0] with { Name = "Text2" };
        var project = fixture.Project with
        {
            Frames = [.. fixture.Project.Frames, extra],
            Editor = fixture.Project.Editor with
            {
                DesignObjects =
                [
                    .. fixture.Project.Editor.DesignObjects.Select(item => item.FrameName == "Text1"
                        ? item with { RuntimeValueRequired = true, RuntimeBinding = "hunt.huntmaster" }
                        : item),
                    new DesignObjectMetadata { FrameName = "Text2", RuntimeValueRequired = true, RuntimeBinding = "hunt.huntmaster" },
                ],
            },
        };
        var model = Wow335ExportBuilder.Build(project, fixture.ProjectPath);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Code == "BINDING_TYPE_CONFLICT");
        Assert.Equal(2, model.Objects.Count(item => item.Metadata.RuntimeBinding == "hunt.huntmaster"));
    }

    [Fact]
    public void AssetManifestUsesPackagedRelativeSourcesAndStockArtworkIsNeverCopied()
    {
        using var fixture = ExportFixture.Create();
        var result = Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1);
        Assert.True(result.Success, result.Summary);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output1, Wow335Exporter.AssetsFileName)));
        Assert.Equal(2, manifest.RootElement.GetProperty("version").GetInt32());
        var asset = Assert.Single(manifest.RootElement.GetProperty("assets").EnumerateArray());
        Assert.Equal("assets/panel.png", asset.GetProperty("source").GetString());
        Assert.False(Path.IsPathRooted(asset.GetProperty("source").GetString()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Root, "panel.png")),
            File.ReadAllBytes(Path.Combine(fixture.Output1, "assets", "panel.png")));
        Assert.True(File.Exists(Path.Combine(fixture.Output1, "textures", "panel.tga")));
        Assert.DoesNotContain(manifest.RootElement.GetRawText(), "UI-StatusBar", StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output1, "*.blp", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(fixture.Output1, "*.tga", SearchOption.AllDirectories));
        Assert.Contains(@"Interface\TargetingFrame\UI-StatusBar",
            File.ReadAllText(Path.Combine(fixture.Output1, Wow335Exporter.XmlFileName)));
    }

    [Fact]
    public void PngConversionPreservesAlphaRgbDimensionsAndTopLeftOrientation()
    {
        using var fixture = ExportFixture.Create();
        var pixels = new[]
        {
            new SKColor(10, 20, 30, 40), new SKColor(50, 60, 70, 80),
            new SKColor(90, 100, 110, 120), new SKColor(130, 140, 150, 160),
        };
        WritePng(Path.Combine(fixture.Root, "alpha.png"), 2, 2, pixels, withAlpha: true);
        WritePng(Path.Combine(fixture.Root, "opaque.png"), 2, 4,
            Enumerable.Range(0, 8).Select(index => new SKColor((byte)(index + 1), 2, 3, 255)).ToArray(),
            withAlpha: false);
        var project = AddTextures(fixture.Project, ("Alpha", "alpha.png"), ("Opaque", "opaque.png"));
        var expectedXml = Wow335Exporter.WriteXml(Wow335ExportBuilder.Build(project, fixture.ProjectPath));

        var result = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output1);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(expectedXml, File.ReadAllText(Path.Combine(fixture.Output1, Wow335Exporter.XmlFileName)));

        var alpha = DecodeTga(Path.Combine(fixture.Output1, "textures", "alpha.tga"));
        Assert.Equal((2, 2), (alpha.Width, alpha.Height));
        Assert.Equal(32, alpha.BitsPerPixel);
        Assert.Equal(0x28, alpha.Descriptor);
        Assert.Equal(new byte[]
        {
            30, 20, 10, 40, 70, 60, 50, 80,
            110, 100, 90, 120, 150, 140, 130, 160,
        }, alpha.Bgra);

        var opaque = DecodeTga(Path.Combine(fixture.Output1, "textures", "opaque.tga"));
        Assert.Equal((2, 4), (opaque.Width, opaque.Height));
        Assert.All(opaque.Bgra.Where((_, index) => index % 4 == 3), value => Assert.Equal(255, value));
        Assert.Equal(2, File.ReadAllBytes(Path.Combine(fixture.Root, "opaque.png"))[25]);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output1, Wow335Exporter.AssetsFileName)));
        var entry = manifest.RootElement.GetProperty("assets").EnumerateArray()
            .Single(item => item.GetProperty("projectReferences").EnumerateArray().Any(reference => reference.GetString() == "alpha.png"));
        Assert.Equal("textures/alpha.tga", entry.GetProperty("generatedFile").GetString());
        Assert.Equal("Interface/FrameForge/Fixture/alpha.tga", entry.GetProperty("requiredMpqDestination").GetString());
        Assert.Equal("tga-type-2-bgra32-top-left", entry.GetProperty("generatedFormat").GetString());
        Assert.Equal(2, entry.GetProperty("width").GetInt32());
        Assert.Equal(2, entry.GetProperty("height").GetInt32());
        Assert.Equal(2, entry.GetProperty("sourceWidth").GetInt32());
        Assert.Equal(2, entry.GetProperty("sourceHeight").GetInt32());
        Assert.False(entry.GetProperty("paddingApplied").GetBoolean());
        Assert.Matches("^[0-9A-F]{64}$", entry.GetProperty("generatedContentSha256").GetString()!);
    }

    [Fact]
    public void NonPowerOfTwoPngIsPaddedAndSharedCropsRetainTheirSourcePixels()
    {
        using var fixture = ExportFixture.Create();
        var pixels = new[]
        {
            new SKColor(1, 11, 21, 31), new SKColor(2, 12, 22, 32), new SKColor(3, 13, 23, 33),
            new SKColor(4, 14, 24, 34), new SKColor(5, 15, 25, 35), new SKColor(6, 16, 26, 36),
            new SKColor(7, 17, 27, 37), new SKColor(8, 18, 28, 38), new SKColor(9, 19, 29, 39),
        };
        WritePng(Path.Combine(fixture.Root, "non-pot.png"), 3, 3, pixels, withAlpha: true);
        WritePng(Path.Combine(fixture.Root, "opaque-non-pot.png"), 3, 2,
            [SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.White, SKColors.Black, SKColors.Yellow],
            withAlpha: false);
        var project = AddTextures(fixture.Project,
            ("Full", "non-pot.png"), ("Crop", "non-pot.png"), ("OpaquePadded", "opaque-non-pot.png"));
        project = project with
        {
            Frames = project.Frames.Select(frame => frame.Name switch
            {
                "Full" => frame with
                {
                    Width = 137,
                    Height = 49,
                    Visual = new FrameVisual { Texture = new TextureVisual(null, TexCoords.Full) },
                },
                "Crop" => frame with
                {
                    Width = 17,
                    Height = 101,
                    Visual = new FrameVisual { Texture = new TextureVisual(null, new(.25, .75, .25, .75)) },
                },
                _ => frame,
            }).ToArray(),
        };

        var result = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output1);
        Assert.True(result.Success, result.Summary);
        var asset = Assert.Single(result.Model!.Assets, item => item.ProjectReferences.Contains("non-pot.png"));
        Assert.Equal((3, 3, 4, 4),
            (asset.SourceWidth, asset.SourceHeight, asset.TextureWidth, asset.TextureHeight));

        var tga = DecodeTga(Path.Combine(fixture.Output1, "textures", "non_pot.tga"));
        Assert.Equal((4, 4), (tga.Width, tga.Height));
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            var offset = (y * 4 + x) * 4;
            if (x < 3 && y < 3)
            {
                var source = pixels[y * 3 + x];
                Assert.Equal(new byte[] { source.Blue, source.Green, source.Red, source.Alpha },
                    tga.Bgra[offset..(offset + 4)]);
            }
            else
            {
                Assert.Equal(new byte[4], tga.Bgra[offset..(offset + 4)]);
            }
        }

        var full = Assert.Single(result.Model.Objects, item => item.SourceName == "Full");
        var crop = Assert.Single(result.Model.Objects, item => item.SourceName == "Crop");
        Assert.Equal((137d, 49d), (full.ExportFrame.Width, full.ExportFrame.Height));
        Assert.Equal((17d, 101d), (crop.ExportFrame.Width, crop.ExportFrame.Height));
        Assert.Equal(new TexCoords(0, .75, 0, .75), full.ExportFrame.Visual!.Texture!.TexCoords);
        Assert.Equal(new TexCoords(.1875, .5625, .1875, .5625), crop.ExportFrame.Visual!.Texture!.TexCoords);
        AssertTextureSamplesMatch(pixels, 3, 3, tga.Bgra, 4, 4,
            TexCoords.Full, full.ExportFrame.Visual.Texture.TexCoords);
        AssertTextureSamplesMatch(pixels, 3, 3, tga.Bgra, 4, 4,
            new(.25, .75, .25, .75), crop.ExportFrame.Visual.Texture.TexCoords);

        var xml = XDocument.Load(Path.Combine(fixture.Output1, Wow335Exporter.XmlFileName));
        XNamespace ns = "http://www.blizzard.com/wow/ui/";
        Assert.Equal("0.75", xml.Descendants(ns + "Texture").Single(element => element.Attribute("name")?.Value == full.RuntimeName)
            .Element(ns + "TexCoords")!.Attribute("right")!.Value);
        Assert.Equal("0.5625", xml.Descendants(ns + "Texture").Single(element => element.Attribute("name")?.Value == crop.RuntimeName)
            .Element(ns + "TexCoords")!.Attribute("right")!.Value);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output1, Wow335Exporter.AssetsFileName)));
        var entry = manifest.RootElement.GetProperty("assets").EnumerateArray()
            .Single(item => item.GetProperty("projectReferences").EnumerateArray().Any(reference => reference.GetString() == "non-pot.png"));
        Assert.Equal(3, entry.GetProperty("sourceWidth").GetInt32());
        Assert.Equal(3, entry.GetProperty("sourceHeight").GetInt32());
        Assert.Equal(4, entry.GetProperty("width").GetInt32());
        Assert.Equal(4, entry.GetProperty("height").GetInt32());
        Assert.True(entry.GetProperty("paddingApplied").GetBoolean());

        var opaque = DecodeTga(Path.Combine(fixture.Output1, "textures", "opaque_non_pot.tga"));
        Assert.Equal((4, 2), (opaque.Width, opaque.Height));
        for (var y = 0; y < 2; y++)
        {
            Assert.All(Enumerable.Range(0, 3), x => Assert.Equal(255, opaque.Bgra[(y * 4 + x) * 4 + 3]));
            Assert.Equal(new byte[4], opaque.Bgra[((y * 4 + 3) * 4)..((y * 4 + 4) * 4)]);
        }

        var repeated = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output2);
        Assert.True(repeated.Success, repeated.Summary);
        foreach (var relative in new[]
                 {
                     Wow335Exporter.XmlFileName,
                     Wow335Exporter.ManifestFileName,
                     Wow335Exporter.AssetsFileName,
                     "textures/non_pot.tga",
                     "textures/opaque_non_pot.tga",
                 })
            Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Output1, relative)),
                File.ReadAllBytes(Path.Combine(fixture.Output2, relative)));
    }

    [Fact]
    public void CorruptPngFailsClearlyWithoutReplacingSuccessfulExport()
    {
        using var fixture = ExportFixture.Create();
        Assert.True(Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1).Success);
        var previousManifest = File.ReadAllBytes(Path.Combine(fixture.Output1, Wow335Exporter.AssetsFileName));

        File.WriteAllBytes(Path.Combine(fixture.Root, "corrupt.png"), [1, 2, 3]);
        var corrupt = Wow335Exporter.Export(AddTextures(fixture.Project, ("Corrupt", "corrupt.png")),
            fixture.ProjectPath, fixture.Output1);
        Assert.False(corrupt.Success);
        Assert.Contains(corrupt.Diagnostics, diagnostic => diagnostic.Code == "ASSET_TEXTURE_INVALID"
            && diagnostic.Message.Contains("corrupt.png", StringComparison.Ordinal));
        Assert.Equal(previousManifest, File.ReadAllBytes(Path.Combine(fixture.Output1, Wow335Exporter.AssetsFileName)));
    }

    [Fact]
    public void ProjectWithoutCustomTexturesExportsWithoutCreatingAssetDirectories()
    {
        using var fixture = ExportFixture.Create();
        var project = fixture.Project with
        {
            Frames = fixture.Project.Frames.Where(frame => frame.Name != "Image").ToArray(),
            Editor = fixture.Project.Editor with
            {
                DesignObjects = fixture.Project.Editor.DesignObjects.Where(item => item.FrameName != "Image").ToArray(),
                DesignOrder = fixture.Project.Editor.DesignOrder.Where(name => name != "Image").ToArray(),
            },
        };

        var result = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output1);
        Assert.True(result.Success, result.Summary);
        Assert.False(Directory.Exists(Path.Combine(fixture.Output1, "assets")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Output1, "textures")));
    }

    [Fact]
    public void TrailingSeparatorExportRootAcceptsAssetsInFinalAndStagingRoots()
    {
        using var fixture = ExportFixture.Create();
        var destinationFromFolderPicker = fixture.Output1 + Path.DirectorySeparatorChar;

        var finalAsset = Wow335Exporter.ResolvePackagePath(destinationFromFolderPicker, "assets/hunt_circle_icon.png");
        var staging = Path.Combine(destinationFromFolderPicker, ".frameforge-staging-test");
        var stagedAsset = Wow335Exporter.ResolvePackagePath(staging, "assets/hunt_circle_icon.png");

        Assert.Equal(Path.Combine(fixture.Output1, "assets", "hunt_circle_icon.png"), finalAsset);
        Assert.Equal(Path.Combine(staging, "assets", "hunt_circle_icon.png"), stagedAsset);
        var result = Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, destinationFromFolderPicker);
        Assert.True(result.Success, result.Summary);
        Assert.True(File.Exists(Path.Combine(fixture.Output1, "assets", "panel.png")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Output1, ".frameforge-staging-*"));
    }

    [Theory]
    [InlineData("../hunt_circle_icon.png")]
    [InlineData("assets/../../hunt_circle_icon.png")]
    [InlineData("..\\hunt_circle_icon.png")]
    [InlineData("C:\\outside\\hunt_circle_icon.png")]
    public void PackagePathContainmentRejectsTraversalAndRootedPaths(string relative)
    {
        using var fixture = ExportFixture.Create();
        Assert.Throws<InvalidDataException>(() => Wow335Exporter.ResolvePackagePath(fixture.Output1, relative));
    }

    [Fact]
    public void DuplicateReferencesDeduplicateAndDifferentContentFilenameCollisionsAreStable()
    {
        using var fixture = ExportFixture.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "a"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "b"));
        WritePng(Path.Combine(fixture.Root, "a", "shared.png"), 2, 2,
            Enumerable.Repeat(SKColors.Red, 4).ToArray(), withAlpha: true);
        WritePng(Path.Combine(fixture.Root, "b", "shared.png"), 2, 2,
            Enumerable.Repeat(SKColors.Blue, 4).ToArray(), withAlpha: true);
        var project = AddTextures(fixture.Project,
            ("A1", "a/shared.png"), ("A2", "a/shared.png"), ("B", "b/shared.png"));

        var first = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output1);
        var second = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output2);
        Assert.True(first.Success, first.Summary);
        Assert.True(second.Success, second.Summary);
        var shared = first.Model!.Assets.Where(asset => asset.ProjectReferences.Contains("a/shared.png", StringComparer.Ordinal)).Single();
        Assert.Equal(2, shared.Consumers.Count);
        var collisionAssets = first.Model.Assets.Where(asset => asset.ProjectReferences.Any(reference => reference.EndsWith("shared.png", StringComparison.Ordinal))).ToArray();
        Assert.Equal(2, collisionAssets.Length);
        Assert.Equal(2, collisionAssets.Select(asset => asset.PackageSource).Distinct(StringComparer.Ordinal).Count());
        Assert.All(collisionAssets, asset => Assert.Contains("-", Path.GetFileNameWithoutExtension(asset.PackageSource)));
        Assert.Equal(
            Directory.EnumerateFiles(fixture.Output1, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(fixture.Output1, path)).Order().ToArray(),
            Directory.EnumerateFiles(fixture.Output2, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(fixture.Output2, path)).Order().ToArray());
        foreach (var relative in Directory.EnumerateFiles(fixture.Output1, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(fixture.Output1, path)))
            Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Output1, relative)), File.ReadAllBytes(Path.Combine(fixture.Output2, relative)));
    }

    [Fact]
    public void IdenticalContentAtDifferentReferencesUsesOnePackagedSourceFile()
    {
        using var fixture = ExportFixture.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "copies"));
        File.Copy(Path.Combine(fixture.Root, "panel.png"), Path.Combine(fixture.Root, "copies", "same.png"));
        var project = AddTextures(fixture.Project, ("Copy", "copies/same.png"));
        var result = Wow335Exporter.Export(project, fixture.ProjectPath, fixture.Output1);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.Model!.Assets.Count);
        Assert.Single(result.Model.Assets.Select(asset => asset.PackageSource).Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Output1, "assets")));
    }

    [Fact]
    public void MissingAndEscapingAssetsBlockExportBeforeDestinationIsCreated()
    {
        using var fixture = ExportFixture.Create();
        var missing = AddTextures(fixture.Project, ("Missing", "missing.png"));
        var missingResult = Wow335Exporter.Export(missing, fixture.ProjectPath, fixture.Output1);
        Assert.False(missingResult.Success);
        Assert.Contains(missingResult.Diagnostics, diagnostic => diagnostic.Code == "ASSET_UNRESOLVED"
            && diagnostic.Message.Contains("missing.png", StringComparison.Ordinal));
        Assert.False(Directory.Exists(fixture.Output1));

        var escaping = AddTextures(fixture.Project, ("Escape", "../escape.png"));
        var escapeResult = Wow335Exporter.Export(escaping, fixture.ProjectPath, fixture.Output2);
        Assert.False(escapeResult.Success);
        Assert.Contains(escapeResult.Diagnostics, diagnostic => diagnostic.Code == "ASSET_ESCAPE");
        Assert.False(Directory.Exists(fixture.Output2));
    }

    [Fact]
    public void ReexportRemovesOnlyPreviouslyManifestedStaleAssets()
    {
        using var fixture = ExportFixture.Create();
        Assert.True(Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1).Success);
        var userFile = Path.Combine(fixture.Output1, "assets", "user-note.txt");
        File.WriteAllText(userFile, "keep");
        var withoutImage = fixture.Project with
        {
            Frames = fixture.Project.Frames.Where(frame => frame.Name != "Image").ToArray(),
            Editor = fixture.Project.Editor with
            {
                DesignObjects = fixture.Project.Editor.DesignObjects.Where(item => item.FrameName != "Image").ToArray(),
                DesignOrder = fixture.Project.Editor.DesignOrder.Where(name => name != "Image").ToArray(),
            },
        };
        Assert.True(Wow335Exporter.Export(withoutImage, fixture.ProjectPath, fixture.Output1).Success);
        Assert.False(File.Exists(Path.Combine(fixture.Output1, "assets", "panel.png")));
        Assert.False(File.Exists(Path.Combine(fixture.Output1, "textures", "panel.tga")));
        Assert.Equal("keep", File.ReadAllText(userFile));
    }

    [Fact]
    public void ExportWillNotOverwriteAnUnownedConflictingAsset()
    {
        using var fixture = ExportFixture.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Output1, "assets"));
        var conflict = Path.Combine(fixture.Output1, "assets", "panel.png");
        File.WriteAllBytes(conflict, [99]);
        var exception = Assert.Throws<InvalidDataException>(() =>
            Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1 + Path.DirectorySeparatorChar));
        Assert.Contains("not owned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([99], File.ReadAllBytes(conflict));
        Assert.False(File.Exists(Path.Combine(fixture.Output1, Wow335Exporter.ManifestFileName)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Output1, ".frameforge-staging-*"));
    }

    [Fact]
    public void ExportWillNotOverwriteAnUnownedConflictingGeneratedTexture()
    {
        using var fixture = ExportFixture.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Output1, "textures"));
        var conflict = Path.Combine(fixture.Output1, "textures", "panel.tga");
        File.WriteAllBytes(conflict, [99]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1));

        Assert.Contains("not owned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([99], File.ReadAllBytes(conflict));
        Assert.False(File.Exists(Path.Combine(fixture.Output1, Wow335Exporter.ManifestFileName)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Output1, ".frameforge-staging-*"));
    }

    [Fact]
    public void UnsafeProjectsFailBeforeWriting()
    {
        using var fixture = ExportFixture.Create();
        var bad = fixture.Project with
        {
            Frames = [.. fixture.Project.Frames, new FrameDef { Name = "Unsupported", Kind = FrameKind.OTHER, Width = -1 }],
            Editor = fixture.Project.Editor with
            {
                DesignObjects = [.. fixture.Project.Editor.DesignObjects,
                    new DesignObjectMetadata { FrameName = "Unsupported", DisplayName = "Unsupported", DesignAsset = @"C:\\Users\\person\\bad.png", StateIds = ["missing"] }]
            }
        };
        var destination = Path.Combine(fixture.Root, "blocked");
        var result = Wow335Exporter.Export(bad, fixture.ProjectPath, destination);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "OBJECT_UNSUPPORTED");
        Assert.Contains(result.Diagnostics, d => d.Code == "GEOMETRY_INVALID");
        Assert.Contains(result.Diagnostics, d => d.Code == "ASSET_ABSOLUTE");
        Assert.Contains(result.Diagnostics, d => d.Code == "STATE_REFERENCE_INVALID");
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void ExportNeverChangesSourceProject()
    {
        using var fixture = ExportFixture.Create();
        var before = File.ReadAllBytes(fixture.ProjectPath);
        Assert.True(Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1).Success);
        Assert.Equal(before, File.ReadAllBytes(fixture.ProjectPath));
    }

    [Fact]
    public void RealNativeHuntsProjectExportsAllFiveStatesAndEveryCustomDesignObjectWhenAvailable()
    {
        var path = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_PROJECT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return; // The sibling checkout is an explicit acceptance fixture, not a repository dependency.

        var sourceBefore = File.ReadAllBytes(path);
        var parsed = ProjectCodec.Parse(File.ReadAllText(path));
        Assert.True(parsed.Ok, parsed.ErrorText);
        var project = ApplyNativeHuntsBindings(parsed.Project!);
        var model = Wow335ExportBuilder.Build(project, path);
        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == ExportSeverity.Error);
        Assert.Equal(new[] { "Idle", "Tracking", "Located", "Fight", "Turnin" }, model.States.Select(s => s.Name));
        var stock = project.Editor.Groups.Where(g => g.Concept == "stock-framework").SelectMany(g => g.Members).ToHashSet(StringComparer.Ordinal);
        var expected = project.Editor.DesignObjects.Count(item => project.Contains(item.FrameName) && !stock.Contains(item.FrameName));
        Assert.Equal(expected, model.Objects.Count);
        Assert.Equal(49, model.Objects.Count);
        Assert.Equal("LFDParentFrame", model.Root.Anchor.RelativeTo);
        Assert.Equal(355, model.Root.Width, 8);
        Assert.Equal(440, model.Root.Height, 8);
        Assert.All(model.Objects, item => Assert.Equal(model.RootRuntimeName, item.ExportFrame.Parent));
        Assert.All(model.Objects.SelectMany(item => item.ExportFrame.AllAnchors), anchor =>
            Assert.True(anchor.RelativeTo is null || anchor.RelativeTo != "UIParent"));
        var progress = Assert.Single(model.Objects, o => o.RuntimeName == "Tracker_Hunt_Progress");
        Assert.Equal(FrameKind.STATUSBAR, progress.Kind);
        Assert.Equal(@"Interface\TargetingFrame\UI-StatusBar", progress.TextureReference);
        Assert.Equal(new ColorRgba(0.2, 0.8, 0.2, 1), progress.Frame.Visual!.StatusBar!.BarColor);
        Assert.Contains(model.Objects, o => o.RuntimeName == "Final_Fight_Icon" && o.StateIds.SequenceEqual(["fight"]));
        Assert.Contains(model.Objects, o => o.RuntimeName == "Complete_Text" && o.StateIds.SequenceEqual(["turnin"]));
        Assert.Contains(model.Objects, o => o.RuntimeName == "Located_Text" && o.StateIds.SequenceEqual(["located"]));
        Assert.Contains(model.Objects, o => o.RuntimeName == "Tracking_Text" && o.StateIds.SequenceEqual(["tracking"]));
        Assert.Equal(10, model.Objects.Count(o => o.Metadata.RuntimeValueRequired));
        Assert.Equal("hunt.progress", progress.Metadata.RuntimeBinding);

        var destination = Path.Combine(Path.GetTempPath(), "frameforge-native-hunts-export", Guid.NewGuid().ToString("N"));
        try
        {
            var result = Wow335Exporter.Export(project, path, destination);
            Assert.True(result.Success, result.Summary);
            Assert.Equal(sourceBefore, File.ReadAllBytes(path));
            var xml = File.ReadAllText(Path.Combine(destination, Wow335Exporter.XmlFileName));
            Assert.Contains($"<Frame name=\"{model.RootRuntimeName}\" parent=\"LFDParentFrame\" hidden=\"true\">", xml);
            Assert.DoesNotContain("<Frame name=\"LFDParentFrame\"", xml);
            Assert.Single(Regex.Matches(xml, "relativeTo=\"LFDParentFrame\"").Cast<Match>());
            Assert.Contains("Round-trip self-check: PASS (49/49 runtime identities)", File.ReadAllText(Path.Combine(destination, Wow335Exporter.ReportFileName)));
            Assert.Equal(11, result.Model!.Assets.Count);
            Assert.Equal(11, Directory.EnumerateFiles(Path.Combine(destination, "assets"), "*", SearchOption.AllDirectories).Count());
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, Wow335Exporter.AssetsFileName)));
            var generatedCount = 0;
            foreach (var entry in assets.RootElement.GetProperty("assets").EnumerateArray())
            {
                var source = entry.GetProperty("source").GetString()!;
                Assert.StartsWith("assets/", source, StringComparison.Ordinal);
                Assert.True(File.Exists(Path.Combine(destination, source.Replace('/', Path.DirectorySeparatorChar))));
                if (entry.TryGetProperty("generatedFile", out var generated))
                {
                    generatedCount++;
                    Assert.True(File.Exists(Path.Combine(destination, generated.GetString()!.Replace('/', Path.DirectorySeparatorChar))));
                }
            }
            Assert.Equal(generatedCount, Directory.EnumerateFiles(Path.Combine(destination, "textures"), "*.tga", SearchOption.AllDirectories).Count());
            var relocated = Path.Combine(Path.GetTempPath(), "frameforge-native-hunts-relocated-" + Guid.NewGuid().ToString("N"));
            Directory.Move(destination, relocated);
            destination = relocated;
            foreach (var entry in assets.RootElement.GetProperty("assets").EnumerateArray())
            {
                Assert.True(File.Exists(Path.Combine(destination, entry.GetProperty("source").GetString()!.Replace('/', Path.DirectorySeparatorChar))));
                if (entry.TryGetProperty("generatedFile", out var generated))
                    Assert.True(File.Exists(Path.Combine(destination, generated.GetString()!.Replace('/', Path.DirectorySeparatorChar))));
            }
            foreach (var forbiddenPattern in new[] { "*.blp", "*.mpq", "*.ttf", "*.otf" })
                Assert.Empty(Directory.EnumerateFiles(destination, forbiddenPattern, SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
        }
    }

    private static Project AddTextures(Project project, params (string Name, string Asset)[] textures)
    {
        return project with
        {
            Frames = [.. project.Frames, .. textures.Select(texture => new FrameDef
            {
                Name = texture.Name,
                Kind = FrameKind.TEXTURE,
                Width = 16,
                Height = 16,
            })],
            Editor = project.Editor with
            {
                DesignObjects = [.. project.Editor.DesignObjects, .. textures.Select(texture => new DesignObjectMetadata
                {
                    FrameName = texture.Name,
                    DisplayName = texture.Name,
                    DesignAsset = texture.Asset,
                })],
                DesignOrder = [.. project.Editor.DesignOrder, .. textures.Select(texture => texture.Name)],
            },
        };
    }

    private static Project ApplyNativeHuntsBindings(Project project)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Standard_Hunts_Value"] = "hunt.standardCount",
            ["Elite_Hunts_Value"] = "hunt.eliteCount",
            ["Hunt_Seal_Value"] = "hunt.seals",
            ["Tracker_Target_Name"] = "hunt.targetName",
            ["Tracker_Huntmaster"] = "hunt.huntmaster",
            ["Tracker_Location"] = "hunt.location",
            ["Tracker_Hunt_Ground_Value"] = "hunt.huntingGround",
            ["Tracker_Hunt_Progress"] = "hunt.progress",
            ["Complete2B_Huntmaster_Value"] = "hunt.huntmaster",
            ["Complete3B_Location_Value"] = "hunt.location",
        };
        return project with
        {
            Editor = project.Editor with
            {
                DesignObjects = project.Editor.DesignObjects.Select(item => keys.TryGetValue(item.DisplayName ?? string.Empty, out var key)
                    ? item with { RuntimeValueRequired = true, RuntimeBinding = key }
                    : item).ToArray(),
            },
        };
    }

    private sealed class ExportFixture : IDisposable
    {
        public required string Root { get; init; }
        public required string ProjectPath { get; init; }
        public required string Output1 { get; init; }
        public required string Output2 { get; init; }
        public required Project Project { get; init; }

        public static ExportFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "frameforge-export-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            WritePng(Path.Combine(root, "panel.png"), 2, 2,
                [SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Transparent], withAlpha: true);
            var frames = new FrameDef[]
            {
                new() { Name = "Text1", Kind = FrameKind.FONTSTRING, Width = 100, Height = 20, Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER, Visual = new FrameVisual { Text = new TextVisual(null) } },
                new() { Name = "Image", Kind = FrameKind.TEXTURE, Width = 64, Height = 64, Point = AnchorPoint.TOPLEFT, RelativePoint = AnchorPoint.TOPLEFT, OffsetX = 12, OffsetY = -8 },
                new() { Name = "Bar", Kind = FrameKind.STATUSBAR, Width = 217, Height = 13, Visual = new FrameVisual { StatusBar = new StatusBarVisual(0, 100, 50, "Interface/TargetingFrame/UI-StatusBar", new(0.2, 0.8, 0.2, 1)) } },
            };
            var project = new Project
            {
                Name = "Fixture", Frames = frames,
                Editor = new EditorMetadata
                {
                    DesignStates = [new() { Id = "tracking", Name = "Tracking" }, new() { Id = "located", Name = "Located" }],
                    DesignOrder = ["Text1", "Image", "Bar"],
                    DesignObjects =
                    [
                        new() { FrameName = "Text1", DisplayName = "Bad Name", StateIds = ["tracking", "located"], TextStyle = new() { BaseStyle = "GameFontHighlight", Size = 17 } },
                        new() { FrameName = "Image", DisplayName = "Bad-Name", DesignAsset = "panel.png" },
                        new() { FrameName = "Bar", DisplayName = "Progress", RuntimeValueRequired = true, RuntimeBinding = "hunt.progress" },
                    ],
                }
            };
            var projectPath = Path.Combine(root, "fixture.fforge.json");
            File.WriteAllText(projectPath, "source sentinel");
            return new() { Root = root, ProjectPath = projectPath, Output1 = Path.Combine(root, "one"), Output2 = Path.Combine(root, "two"), Project = project };
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static void WritePng(string path, int width, int height, IReadOnlyList<SKColor> pixels, bool withAlpha)
    {
        Assert.Equal(width * height, pixels.Count);
        var info = new SKImageInfo(width, height,
            withAlpha ? SKColorType.Rgba8888 : SKColorType.Rgb888x,
            withAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, pixels[y * width + x]);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
    }

    private static (int Width, int Height, int BitsPerPixel, int Descriptor, byte[] Bgra) DecodeTga(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 18);
        Assert.Equal(0, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(2, bytes[2]);
        var width = BitConverter.ToUInt16(bytes, 12);
        var height = BitConverter.ToUInt16(bytes, 14);
        var pixels = bytes[18..];
        Assert.Equal(width * height * 4, pixels.Length);
        return (width, height, bytes[16], bytes[17], pixels);
    }

    private static void AssertTextureSamplesMatch(
        IReadOnlyList<SKColor> source,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyList<byte> paddedBgra,
        int paddedWidth,
        int paddedHeight,
        TexCoords sourceCoordinates,
        TexCoords paddedCoordinates)
    {
        const int samples = 37;
        for (var y = 0; y < samples; y++)
        for (var x = 0; x < samples; x++)
        {
            // Pixel-centre samples avoid making an unsupported claim about the WoW client's
            // precise edge filtering while still verifying the complete coordinate composition.
            var tx = (x + .5) / samples;
            var ty = (y + .5) / samples;
            var sourceU = sourceCoordinates.Left + (sourceCoordinates.Right - sourceCoordinates.Left) * tx;
            var sourceV = sourceCoordinates.Top + (sourceCoordinates.Bottom - sourceCoordinates.Top) * ty;
            var paddedU = paddedCoordinates.Left + (paddedCoordinates.Right - paddedCoordinates.Left) * tx;
            var paddedV = paddedCoordinates.Top + (paddedCoordinates.Bottom - paddedCoordinates.Top) * ty;
            var sourceX = Math.Clamp((int)(sourceU * sourceWidth), 0, sourceWidth - 1);
            var sourceY = Math.Clamp((int)(sourceV * sourceHeight), 0, sourceHeight - 1);
            var paddedX = Math.Clamp((int)(paddedU * paddedWidth), 0, paddedWidth - 1);
            var paddedY = Math.Clamp((int)(paddedV * paddedHeight), 0, paddedHeight - 1);
            var expected = source[sourceY * sourceWidth + sourceX];
            var offset = (paddedY * paddedWidth + paddedX) * 4;
            Assert.Equal(new byte[] { expected.Blue, expected.Green, expected.Red, expected.Alpha },
                new[] { paddedBgra[offset], paddedBgra[offset + 1], paddedBgra[offset + 2], paddedBgra[offset + 3] });
        }
    }
}
