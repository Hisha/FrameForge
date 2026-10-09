using System.Text.Json;
using System.Xml.Linq;
using FrameForge.Core.Export;
using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class V2FrameXmlExportTests
{
    private static readonly XNamespace Ui = "http://www.blizzard.com/wow/ui/";

    [Fact]
    public void CheckedInGoldenProjectAndXmlMatchExporter()
    {
        var document = GoldenDocument();
        var assembly = typeof(V2FrameXmlExportTests).Assembly;
        using var projectStream = assembly.GetManifestResourceStream(
            "FrameForge.Core.Tests.examples.frameforge-v2-golden.fforge.json");
        using var xmlStream = assembly.GetManifestResourceStream(
            "FrameForge.Core.Tests.examples.frameforge-v2-golden.Design.xml");
        Assert.NotNull(projectStream);
        Assert.NotNull(xmlStream);
        using var projectReader = new StreamReader(projectStream);
        using var xmlReader = new StreamReader(xmlStream);

        Assert.Equal(UiDocumentCodec.Serialize(document), projectReader.ReadToEnd());
        Assert.Equal(V2FrameXmlExporter.Build(document, null).Xml, xmlReader.ReadToEnd());
    }

    [Fact]
    public void GoldenProjectUsesNativeOwnershipLayersAndTypedAnchors()
    {
        var document = GoldenDocument();
        var plan = V2FrameXmlExporter.Build(document, null);

        Assert.True(plan.IsValid, Diagnostics(plan));
        var xml = XDocument.Parse(plan.Xml);
        var root = Assert.Single(xml.Root!.Elements(Ui + "Frame"));
        Assert.Equal("GoldenDesignRoot", (string?)root.Attribute("name"));
        Assert.Equal("GoldenModuleHost", (string?)root.Attribute("parent"));
        Assert.Equal("true", (string?)root.Attribute("setAllPoints"));

        var layers = Assert.Single(root.Elements(Ui + "Layers"));
        Assert.Equal(2, layers.Elements(Ui + "Layer").Count());
        Assert.Contains(layers.Elements(Ui + "Layer"), layer =>
            (string?)layer.Attribute("level") == "BACKGROUND" &&
            layer.Element(Ui + "Texture") is not null);
        Assert.Contains(layers.Elements(Ui + "Layer"), layer =>
            (string?)layer.Attribute("level") == "OVERLAY" &&
            (string?)layer.Attribute("textureSubLevel") == "2" &&
            layer.Element(Ui + "FontString") is not null);

        var frames = Assert.Single(root.Elements(Ui + "Frames"));
        Assert.NotNull(frames.Element(Ui + "Button"));
        Assert.NotNull(frames.Element(Ui + "StatusBar"));
        Assert.DoesNotContain(root.Descendants(Ui + "Texture"), element =>
            element.Parent?.Name.LocalName == "Frames");
        Assert.DoesNotContain(root.Descendants(Ui + "FontString"), element =>
            element.Parent?.Name.LocalName == "Frames");
    }

    [Fact]
    public void ParentRootLocalExternalAndMultipleAnchorsRemainDistinct()
    {
        var document = GoldenDocument();
        var nodes = document.Nodes.ToArray();
        var button = nodes.Single(node => node.Kind == UiNodeKind.Button);
        nodes[2] = button with
        {
            Anchors =
            [
                Anchor(AnchorPoint.TOP, AnchorTarget.Local(nodes[1].Id), AnchorPoint.BOTTOM, 0, -10),
                Anchor(AnchorPoint.RIGHT, AnchorTarget.External("ExplicitExternal"), AnchorPoint.RIGHT, -4, 0),
            ],
        };
        document = document with
        {
            Nodes = nodes,
            ExternalReferences =
            [
                .. document.ExternalReferences,
                new ExternalReference { GlobalName = "ExplicitExternal", ExpectedSource = "module" },
            ],
        };

        var plan = V2FrameXmlExporter.Build(document, null);
        Assert.True(plan.IsValid, Diagnostics(plan));
        var xml = XDocument.Parse(plan.Xml);
        var backgroundAnchors = xml.Descendants(Ui + "Texture").Single()
            .Element(Ui + "Anchors")!.Elements(Ui + "Anchor").ToArray();
        Assert.Equal(2, backgroundAnchors.Length);
        Assert.All(backgroundAnchors, anchor =>
            Assert.Equal("GoldenDesignRoot", (string?)anchor.Attribute("relativeTo")));

        var buttonAnchors = xml.Descendants(Ui + "Button").Single()
            .Element(Ui + "Anchors")!.Elements(Ui + "Anchor").ToArray();
        Assert.Equal("GoldenHeader", (string?)buttonAnchors[0].Attribute("relativeTo"));
        Assert.Equal("ExplicitExternal", (string?)buttonAnchors[1].Attribute("relativeTo"));

        var statusAnchor = xml.Descendants(Ui + "StatusBar").Single()
            .Element(Ui + "Anchors")!.Element(Ui + "Anchor")!;
        Assert.Null(statusAnchor.Attribute("relativeTo"));
        Assert.Contains(plan.ExternalDependencies, dependency =>
            dependency.Kind == "externalGlobal" && dependency.Identity == "ExplicitExternal");
    }

    [Fact]
    public void ExplicitUiParentIsAllowedOnlyAsDeclaredExternal()
    {
        var document = GoldenDocument();
        var nodes = document.Nodes.ToArray();
        nodes[2] = nodes[2] with { Anchors = [Anchor(AnchorPoint.CENTER, AnchorTarget.External("UIParent"))] };
        var undeclared = V2FrameXmlExporter.Build(document with { Nodes = nodes }, null);
        Assert.False(undeclared.IsValid);

        var declared = V2FrameXmlExporter.Build(document with
        {
            Nodes = nodes,
            ExternalReferences =
            [
                .. document.ExternalReferences,
                new ExternalReference { GlobalName = "UIParent", ExpectedSource = "Blizzard FrameXML" },
            ],
        }, null);
        Assert.True(declared.IsValid, Diagnostics(declared));
        Assert.Contains("relativeTo=\"UIParent\"", declared.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportContainsNoAccidentalUiParentAndDoesNotMutateDocument()
    {
        var document = GoldenDocument();
        var before = UiDocumentCodec.Serialize(document);

        var plan = V2FrameXmlExporter.Build(document, null);

        Assert.True(plan.IsValid, Diagnostics(plan));
        Assert.DoesNotContain("UIParent", plan.Xml, StringComparison.Ordinal);
        Assert.Equal(before, UiDocumentCodec.Serialize(document));
    }

    [Fact]
    public void GeneratedRuntimeNamesAreStableReservedAndLabelIndependent()
    {
        var document = GoldenDocument();
        var nodes = document.Nodes.ToArray();
        nodes[0] = nodes[0] with { RuntimeName = null, DisplayLabel = "First label" };
        document = document with { Nodes = nodes };

        var first = V2FrameXmlExporter.Build(document, null);
        nodes[0] = nodes[0] with { DisplayLabel = "Renamed visible label" };
        var second = V2FrameXmlExporter.Build(document with { Nodes = nodes }, null);

        Assert.True(first.IsValid, Diagnostics(first));
        Assert.True(second.IsValid, Diagnostics(second));
        var generated = first.RuntimeNames[nodes[0].Id];
        Assert.StartsWith("FF2_", generated, StringComparison.Ordinal);
        Assert.Equal(generated, second.RuntimeNames[nodes[0].Id]);
        Assert.Equal(first.Xml, second.Xml);
    }

    [Fact]
    public void InvalidExplicitAndCollidingRuntimeNamesFailClosed()
    {
        var document = GoldenDocument();
        var nodes = document.Nodes.ToArray();
        nodes[0] = nodes[0] with { RuntimeName = "bad name" };
        nodes[1] = nodes[1] with { RuntimeName = "GoldenButton" };
        var plan = V2FrameXmlExporter.Build(document with { Nodes = nodes }, null);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Diagnostics, item => item.Code == "FFV2X-NAME-001");
        Assert.Contains(plan.Diagnostics, item => item.Code is "FFV2X-NAME-003" or "FFV2-ID-004");
        Assert.Empty(plan.Xml);
        Assert.Empty(plan.Manifest);
    }

    [Fact]
    public void FixedSizeRootWritesSizeAndHostRelativeAnchor()
    {
        var document = GoldenDocument();
        var root = document.CompositionRoots.Single();
        document = document with
        {
            CompositionRoots =
            [
                root with
                {
                    Sizing = RootSizing.Explicit(800, 600,
                        new RootAnchor
                        {
                            Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER,
                            OffsetX = 5, OffsetY = -7,
                        }),
                },
            ],
        };
        var plan = V2FrameXmlExporter.Build(document, null);

        Assert.True(plan.IsValid, Diagnostics(plan));
        var rootXml = Assert.Single(XDocument.Parse(plan.Xml).Root!.Elements(Ui + "Frame"));
        Assert.Null(rootXml.Attribute("setAllPoints"));
        Assert.Equal("800", (string?)rootXml.Element(Ui + "Size")!.Element(Ui + "AbsDimension")!.Attribute("x"));
        Assert.Equal("GoldenModuleHost", (string?)rootXml.Element(Ui + "Anchors")!
            .Element(Ui + "Anchor")!.Attribute("relativeTo"));
    }

    [Fact]
    public void FontAndStatusBarAppearanceExportAsNativeFrameXml()
    {
        var document = GoldenDocument();
        var nodes = document.Nodes.ToArray();
        var fontIndex = Array.FindIndex(nodes, node => node.Kind == UiNodeKind.FontString);
        var statusIndex = Array.FindIndex(nodes, node => node.Kind == UiNodeKind.StatusBar);
        nodes[fontIndex] = nodes[fontIndex] with
        {
            AuthoredProperties = nodes[fontIndex].AuthoredProperties with
            {
                Region = nodes[fontIndex].AuthoredProperties.Region! with { Tint = new UiColor(1, 1, 1, 1) },
                FontString = nodes[fontIndex].AuthoredProperties.FontString! with
                {
                    FontSize = 14, JustifyH = "CENTER", JustifyV = "MIDDLE",
                },
            },
        };
        nodes[statusIndex] = nodes[statusIndex] with
        {
            AuthoredProperties = nodes[statusIndex].AuthoredProperties with
            {
                StatusBar = nodes[statusIndex].AuthoredProperties.StatusBar! with
                {
                    FillColor = new UiColor(0, 1, 0, 1),
                    BackgroundColor = new UiColor(0.1, 0.1, 0.1, 1),
                },
            },
        };

        var plan = V2FrameXmlExporter.Build(document with { Nodes = nodes }, null);

        Assert.True(plan.IsValid, Diagnostics(plan));
        var xml = XDocument.Parse(plan.Xml);
        var font = xml.Descendants(Ui + "FontString").Single();
        Assert.Equal("CENTER", (string?)font.Attribute("justifyH"));
        Assert.Equal("MIDDLE", (string?)font.Attribute("justifyV"));
        Assert.Equal("14", (string?)font.Element(Ui + "FontHeight")?.Element(Ui + "AbsValue")?.Attribute("val"));
        var status = xml.Descendants(Ui + "StatusBar").Single();
        Assert.Equal("0", (string?)status.Element(Ui + "BarColor")?.Attribute("r"));
        var background = status.Descendants(Ui + "Texture").Single(item => (string?)item.Attribute("name") == "$parentBackground");
        Assert.Equal("true", (string?)background.Attribute("setAllPoints"));
        Assert.Equal("0.1", (string?)background.Element(Ui + "Color")?.Attribute("r"));
    }

    [Fact]
    public void ProjectArtworkIsHashedCopiedAndListedInManifest()
    {
        var directory = NewTempDirectory();
        try
        {
            var projectPath = Path.Combine(directory, "golden.fforge2.json");
            File.WriteAllText(projectPath, UiDocumentCodec.Serialize(GoldenDocument()));
            File.WriteAllBytes(Path.Combine(directory, "panel.tga"), [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 32, 0, 0, 0, 0, 255]);
            var document = WithBackgroundReference(GoldenDocument(), "panel.tga");
            var output = Path.Combine(directory, "export");
            var secondOutput = Path.Combine(directory, "export-repeat");

            var first = V2FrameXmlExporter.Export(document, projectPath, output);
            var second = V2FrameXmlExporter.Export(document, projectPath, secondOutput);

            Assert.True(first.Success, first.Summary + " " + Diagnostics(first.Plan));
            Assert.True(second.Success, second.Summary + " " + Diagnostics(second.Plan));
            Assert.Equal(first.Plan.Xml, second.Plan.Xml);
            Assert.Equal(first.Plan.Manifest, second.Plan.Manifest);
            var asset = Assert.Single(first.Plan.Assets);
            Assert.True(File.Exists(Path.Combine(output, asset.OutputPath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(File.ReadAllBytes(Path.Combine(output, asset.OutputPath.Replace('/', Path.DirectorySeparatorChar))),
                File.ReadAllBytes(Path.Combine(secondOutput, asset.OutputPath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Contains(asset.LogicalClientPath, first.Plan.Xml, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, first.Plan.Manifest, StringComparison.Ordinal);
            using var manifest = JsonDocument.Parse(first.Plan.Manifest);
            Assert.Equal(V2FrameXmlExporter.ManifestSchema,
                manifest.RootElement.GetProperty("schema").GetString());
            Assert.Equal("valid", manifest.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, manifest.RootElement.GetProperty("outputs").GetArrayLength());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MissingAndUnsupportedArtworkBlockBeforeDestinationCreation()
    {
        var directory = NewTempDirectory();
        try
        {
            var projectPath = Path.Combine(directory, "golden.fforge2.json");
            File.WriteAllText(projectPath, "{}");
            var destination = Path.Combine(directory, "blocked");

            var missing = V2FrameXmlExporter.Export(
                WithBackgroundReference(GoldenDocument(), "missing.tga"), projectPath, destination);
            var unsupported = V2FrameXmlExporter.Build(
                WithBackgroundReference(GoldenDocument(), "picture.png"), projectPath);
            File.WriteAllBytes(Path.Combine(directory, "malformed.tga"), [1, 2, 3]);
            var malformed = V2FrameXmlExporter.Build(
                WithBackgroundReference(GoldenDocument(), "malformed.tga"), projectPath);
            var unsafeClientPath = V2FrameXmlExporter.Build(
                WithBackgroundReference(GoldenDocument(), @"Interface\..\private.tga"), projectPath);

            Assert.False(missing.Success);
            Assert.Contains(missing.Plan.Diagnostics, item => item.Code == "FFV2X-ASSET-006");
            Assert.False(Directory.Exists(destination));
            Assert.Contains(unsupported.Diagnostics, item => item.Code == "FFV2X-ASSET-005");
            Assert.Contains(malformed.Diagnostics, item => item.Code == "FFV2X-ASSET-009");
            Assert.Contains(unsafeClientPath.Diagnostics, item => item.Code == "FFV2X-ASSET-008");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ManifestIdentityOwnershipDependenciesAndHashesAreConsistent()
    {
        var document = GoldenDocument();
        var plan = V2FrameXmlExporter.Build(document, null);
        using var manifest = JsonDocument.Parse(plan.Manifest);
        var json = manifest.RootElement;

        Assert.Equal(12340, json.GetProperty("target").GetProperty("build").GetInt32());
        Assert.Equal(document.Nodes.Count, json.GetProperty("controls").GetArrayLength());
        Assert.Contains(json.GetProperty("externalReferences").EnumerateArray(), item =>
            item.GetProperty("kind").GetString() == "moduleHost" &&
            item.GetProperty("identity").GetString() == "GoldenModuleHost");
        Assert.Contains(json.GetProperty("externalReferences").EnumerateArray(), item =>
            item.GetProperty("kind").GetString() == "blizzardAsset");
        Assert.Contains(json.GetProperty("externalReferences").EnumerateArray(), item =>
            item.GetProperty("kind").GetString() == "blizzardTemplate" &&
            item.GetProperty("identity").GetString() == "GameFontNormalLarge");
        foreach (var control in json.GetProperty("controls").EnumerateArray())
        {
            var id = new SemanticId(control.GetProperty("internalId").GetString()!);
            Assert.Equal(plan.RuntimeNames[id], control.GetProperty("runtimeName").GetString());
        }
        Assert.Equal(64, json.GetProperty("outputs")[0].GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public void FortyNineControlsRemainOwnedByOneRoot()
    {
        var source = GoldenDocument();
        var root = source.CompositionRoots.Single();
        var nodes = Enumerable.Range(1, 49).Select(index => new UiNode
        {
            Id = Id(100 + index),
            Kind = UiNodeKind.Frame,
            RuntimeName = index % 2 == 0 ? $"Control{index}" : null,
            DisplayLabel = $"Visible label {index}",
            Owner = OwnerReference.Root(root.Id),
            Anchors = [Anchor(AnchorPoint.CENTER, AnchorTarget.Parent(), offsetX: index)],
            AuthoredProperties = new AuthoredProperties
            {
                Frame = new FrameProperties { Width = 20, Height = 20 },
            },
        }).ToArray();
        var document = source with
        {
            CompositionRoots = [root with { Children = nodes.Select(node => node.Id).ToArray() }],
            Nodes = nodes,
        };

        var plan = V2FrameXmlExporter.Build(document, null);
        Assert.True(plan.IsValid, Diagnostics(plan));
        Assert.Equal(49, plan.Controls.Count);
        Assert.All(plan.Controls, control => Assert.Equal(root.Id, control.OwnerId));
        var xml = XDocument.Parse(plan.Xml);
        var rootXml = Assert.Single(xml.Root!.Elements(Ui + "Frame"));
        Assert.Equal(49, rootXml.Element(Ui + "Frames")!.Elements().Count());
        Assert.All(rootXml.Descendants(Ui + "Anchor"), anchor => Assert.Null(anchor.Attribute("relativeTo")));
    }

    [Fact]
    public void NestedDescendantIsEmittedInsideItsSemanticOwner()
    {
        var document = GoldenDocument();
        var root = document.CompositionRoots.Single();
        var nodes = document.Nodes.ToArray();
        var button = nodes[2];
        var status = nodes[3];
        nodes[2] = button with { Children = [status.Id] };
        nodes[3] = status with { Owner = OwnerReference.Node(button.Id) };
        document = document with
        {
            CompositionRoots = [root with { Children = root.Children.Where(id => id != status.Id).ToArray() }],
            Nodes = nodes,
        };

        var plan = V2FrameXmlExporter.Build(document, null);
        Assert.True(plan.IsValid, Diagnostics(plan));
        var buttonXml = XDocument.Parse(plan.Xml).Descendants(Ui + "Button").Single();
        Assert.Equal("GoldenProgress", (string?)buttonXml.Element(Ui + "Frames")!
            .Element(Ui + "StatusBar")!.Attribute("name"));
    }

    [Fact]
    public void UnsupportedRuntimePropertiesFailWithActionablePaths()
    {
        var document = GoldenDocument();
        var nodes = document.Nodes.ToArray();
        nodes[2] = nodes[2] with
        {
            AuthoredProperties = nodes[2].AuthoredProperties with
            {
                Button = new ButtonProperties { Enabled = false },
            },
        };
        nodes[1] = nodes[1] with
        {
            AuthoredProperties = nodes[1].AuthoredProperties with
            {
                Region = nodes[1].AuthoredProperties.Region! with { Sublevel = 8 },
            },
        };
        var plan = V2FrameXmlExporter.Build(document with { Nodes = nodes }, null);

        var diagnostic = Assert.Single(plan.Diagnostics, item => item.Code == "FFV2X-PROP-003");
        Assert.Equal(nodes[2].Id, diagnostic.NodeId);
        Assert.Equal("authoredProperties.button.enabled", diagnostic.PropertyPath);
        Assert.Contains(plan.Diagnostics, item => item.Code == "FFV2X-PROP-006" && item.NodeId == nodes[1].Id);
        Assert.False(plan.IsValid);
    }

    internal static UiDocument GoldenDocument()
    {
        var rootId = Id(1);
        var backgroundId = Id(2);
        var headerId = Id(3);
        var buttonId = Id(4);
        var statusId = Id(5);
        return new UiDocument
        {
            Version = UiDocument.SchemaVersion,
            DocumentId = Id(99),
            Target = WowTargetProfile.Wow335a12340,
            CompositionRoots =
            [
                new CompositionRoot
                {
                    Id = rootId,
                    RuntimeName = "GoldenDesignRoot",
                    ExternalHostName = "GoldenModuleHost",
                    DesignWidth = 800,
                    DesignHeight = 600,
                    Sizing = RootSizing.FillHost(),
                    Children = [backgroundId, headerId, buttonId, statusId],
                },
            ],
            Nodes =
            [
                new UiNode
                {
                    Id = backgroundId, Kind = UiNodeKind.Texture, RuntimeName = "GoldenBackground",
                    DisplayLabel = "Background", Owner = OwnerReference.Root(rootId),
                    Anchors =
                    [
                        Anchor(AnchorPoint.TOPLEFT, AnchorTarget.Root(), AnchorPoint.TOPLEFT),
                        Anchor(AnchorPoint.BOTTOMRIGHT, AnchorTarget.Root(), AnchorPoint.BOTTOMRIGHT),
                    ],
                    AuthoredProperties = new AuthoredProperties
                    {
                        Region = new RegionProperties
                        {
                            DrawLayer = RegionDrawLayer.Background,
                            Tint = new UiColor(0.08, 0.1, 0.14, 1),
                        },
                        Texture = new TextureProperties
                        {
                            TextureReference = @"Interface\Buttons\WHITE8X8",
                        },
                    },
                },
                new UiNode
                {
                    Id = headerId, Kind = UiNodeKind.FontString, RuntimeName = "GoldenHeader",
                    DisplayLabel = "Header", Owner = OwnerReference.Root(rootId),
                    Anchors = [Anchor(AnchorPoint.TOP, AnchorTarget.Parent(), AnchorPoint.TOP, 0, -24)],
                    AuthoredProperties = new AuthoredProperties
                    {
                        Region = new RegionProperties
                        {
                            Width = 300, Height = 24, DrawLayer = RegionDrawLayer.Overlay, Sublevel = 2,
                        },
                        FontString = new FontStringProperties
                        {
                            FontReference = "GameFontNormalLarge", Text = "FrameForge Export",
                        },
                    },
                },
                new UiNode
                {
                    Id = buttonId, Kind = UiNodeKind.Button, RuntimeName = "GoldenButton",
                    DisplayLabel = "Action", Owner = OwnerReference.Root(rootId),
                    Anchors = [Anchor(AnchorPoint.TOP, AnchorTarget.Local(headerId), AnchorPoint.BOTTOM, 0, -16)],
                    AuthoredProperties = new AuthoredProperties
                    {
                        Frame = new FrameProperties
                        {
                            Width = 160, Height = 32, Strata = FrameStrata.Medium, Level = 2, Visible = true,
                        },
                        Button = new ButtonProperties { Enabled = true },
                    },
                },
                new UiNode
                {
                    Id = statusId, Kind = UiNodeKind.StatusBar, RuntimeName = "GoldenProgress",
                    DisplayLabel = "Progress", Owner = OwnerReference.Root(rootId),
                    Anchors = [Anchor(AnchorPoint.TOP, AnchorTarget.Parent(), AnchorPoint.TOP, 0, -110)],
                    AuthoredProperties = new AuthoredProperties
                    {
                        Frame = new FrameProperties { Width = 240, Height = 18, Level = 1 },
                        StatusBar = new StatusBarProperties
                        {
                            Minimum = 0, Maximum = 100, Value = 35,
                            TextureReference = @"Interface\TargetingFrame\UI-StatusBar",
                        },
                    },
                },
            ],
            ExternalReferences =
            [
                new ExternalReference
                {
                    GlobalName = "GoldenModuleHost", ExpectedSource = "AzerothCore module",
                    Description = "Module-owned visual host",
                },
            ],
            Diagnostics = [],
        };
    }

    private static UiDocument WithBackgroundReference(UiDocument document, string reference)
    {
        var nodes = document.Nodes.ToArray();
        nodes[0] = nodes[0] with
        {
            AuthoredProperties = nodes[0].AuthoredProperties with
            {
                Texture = new TextureProperties { TextureReference = reference },
            },
        };
        return document with { Nodes = nodes };
    }

    private static UiAnchor Anchor(AnchorPoint point, AnchorTarget target,
        AnchorPoint relativePoint = AnchorPoint.CENTER, double offsetX = 0, double offsetY = 0) => new()
        {
            Point = point,
            Target = target,
            RelativePoint = relativePoint,
            OffsetX = offsetX,
            OffsetY = offsetY,
        };

    private static SemanticId Id(int suffix) =>
        new($"10000000-0000-0000-0000-{suffix:000000000000}");

    private static string Diagnostics(V2FrameXmlExportPlan plan) =>
        string.Join(Environment.NewLine, plan.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "frameforge-v2-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
