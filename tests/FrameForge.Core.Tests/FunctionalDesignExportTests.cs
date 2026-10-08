using FrameForge.Core.Export;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class FunctionalDesignExportTests
{
    private const string Source = """
        <Ui xmlns="http://www.blizzard.com/wow/ui/">
          <Script file="Runtime.lua"/>
          <Frame name="FunctionalHost" parent="LFDParentFrame" setAllPoints="true">
            <Layers><Layer><FontString name="SourceValue" text="runtime"/></Layer></Layers>
            <Scripts><OnShow>Runtime_OnShow(self);</OnShow></Scripts>
          </Frame>
        </Ui>
        """;

    [Fact]
    public void CompositionWithOptionalMappingPreservesAuthoritativeSourceAndAddsMappedDesign()
    {
        var project = Design();
        var close = Source.LastIndexOf("</Ui>", StringComparison.Ordinal);
        var result = FunctionalDesignExporter.Prepare(project, null, Source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.StartsWith(Source[..close], result.Xml!, StringComparison.Ordinal);
        Assert.EndsWith(Source[close..], result.Xml!, StringComparison.Ordinal);
        Assert.Contains("<Script file=\"Runtime.lua\"/>", result.Xml, StringComparison.Ordinal);
        Assert.Contains("Runtime_OnShow(self);", result.Xml, StringComparison.Ordinal);
        Assert.Contains("parent=\"FunctionalHost\"", result.Xml, StringComparison.Ordinal);
        Assert.Contains("name=\"Value\"", result.Xml, StringComparison.Ordinal);
        Assert.Contains("SourceValue:GetText()", result.Xml, StringComparison.Ordinal);
        Assert.Contains("Value:SetText", result.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroRuntimeMappingsExportAuthoredDefaultsWithoutAGeneratedBridge()
    {
        var design = Design() with
        {
            FunctionalExport = Design().FunctionalExport! with { Values = [], States = [] },
        };

        var result = FunctionalDesignExporter.Prepare(design, null, Source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "FUNCTIONAL_VALUE_UNMAPPED");
        Assert.DoesNotContain("<OnUpdate>", result.Xml, StringComparison.Ordinal);
        Assert.Contains("name=\"Value\"", result.Xml, StringComparison.Ordinal);
        Assert.Contains("text=\"preview\"", result.Xml, StringComparison.Ordinal);
        Assert.Contains("name=\"Progress\"", result.Xml, StringComparison.Ordinal);
        Assert.Contains("defaultValue=\"42\"", result.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedSourceIsBlockedButIncompleteValueMappingIsAllowed()
    {
        var design = Design();
        var changed = FunctionalDesignExporter.Prepare(design, null, Source.Replace("runtime", "changed"));
        Assert.False(changed.Success);
        Assert.Contains(changed.Diagnostics, item => item.Code == "FUNCTIONAL_SOURCE_HASH_MISMATCH");

        var incomplete = FunctionalDesignExporter.Prepare(design with
        {
            FunctionalExport = design.FunctionalExport! with { Values = [] },
        }, null, Source);
        Assert.True(incomplete.Success, string.Join("\n", incomplete.Diagnostics));
        Assert.DoesNotContain(incomplete.Diagnostics, item => item.Code == "FUNCTIONAL_VALUE_UNMAPPED");
    }

    [Fact]
    public void FunctionalAssociationRoundTripsAsPersistentProjectMetadata()
    {
        var project = Design();
        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(project));
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal(project.FunctionalExport!.Source, reopened.Project!.FunctionalExport!.Source);
        Assert.Equal(project.FunctionalExport.HostFrameName, reopened.Project.FunctionalExport.HostFrameName);
        Assert.Equal(project.FunctionalExport.States, reopened.Project.FunctionalExport.States);
        Assert.Equal(project.FunctionalExport.Values, reopened.Project.FunctionalExport.Values);
    }

    [Fact]
    public void AssociationDoesNotInferModuleSpecificStateOrValueMappings()
    {
        var design = Design();
        design = design with
        {
            FunctionalExport = null,
            Editor = design.Editor with
            {
                DesignStates = [new DesignState { Id = "open", Name = "Open" }],
                DesignObjects = design.Editor.DesignObjects.Select(item => item.FrameName == "Value"
                    ? item with { StateIds = ["open"] }
                    : item).ToArray(),
            },
        };

        var result = FunctionalExportAssociator.Associate(design, Source, "Functional.xml", null);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Profile!.States);
        Assert.Empty(result.Profile.Values);
        Assert.Contains(result.Diagnostics, item => item.Code == "FUNCTIONAL_STATES_MODULE_MANAGED"
            && item.Severity == ExportSeverity.Warning);
    }

    [Fact]
    public void NamesAndParentRelationshipsAreDeterministicAndLuaAccessible()
    {
        var design = Design();
        var first = FunctionalDesignExporter.Prepare(design, null, Source);
        var second = FunctionalDesignExporter.Prepare(design, null, Source);

        Assert.True(first.Success, string.Join("\n", first.Diagnostics));
        Assert.Equal(first.Xml, second.Xml);
        var xml = XDocument.Parse(first.Xml!);
        var progress = xml.Descendants().Single(element => (string?)element.Attribute("name") == "Progress");
        Assert.Equal("Value__FFLayer", (string?)progress.Attribute("parent"));
        Assert.Contains(xml.Descendants(), element => (string?)element.Attribute("name") == "Value");
    }

    [Fact]
    public void GeneratedWrapperAndSourceIdentityCollisionsAreBlocked()
    {
        var design = Design();
        var collisionFrame = new FrameDef
        {
            Name = "Collision", Kind = FrameKind.FRAME, Width = 10, Height = 10,
            Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER,
        };
        var generatedCollision = design with
        {
            Frames = [.. design.Frames, collisionFrame],
            Editor = design.Editor with
            {
                DesignObjects = [.. design.Editor.DesignObjects,
                    new DesignObjectMetadata { FrameName = "Collision", DisplayName = "FrameForge_Functional_Fixture" }],
                DesignOrder = [.. design.Editor.DesignOrder, "Collision"],
            },
        };
        var duplicate = FunctionalDesignExporter.Prepare(generatedCollision, null, Source);
        Assert.False(duplicate.Success);
        Assert.Contains(duplicate.Diagnostics, item => item.Code == "FUNCTIONAL_GENERATED_IDENTITY_COLLISION");

        var sourceCollision = Source.Replace("name=\"SourceValue\"", "name=\"Value__FFLayer\"");
        var associated = design with
        {
            FunctionalExport = design.FunctionalExport! with
            {
                Source = design.FunctionalExport.Source with { Sha256 = FrameXmlImporter.ComputeSha256(sourceCollision) },
                Values = [],
            },
        };
        var existing = FunctionalDesignExporter.Prepare(associated, null, sourceCollision);
        Assert.False(existing.Success);
        Assert.Contains(existing.Diagnostics, item => item.Code == "FUNCTIONAL_IDENTITY_COLLISION");
    }

    [Fact]
    public void FunctionalPackageManifestContainsInventoryDerivedFromExportedXml()
    {
        var project = Design() with
        {
            FunctionalExport = Design().FunctionalExport! with { Values = [] },
        };
        var destination = Path.Combine(Path.GetTempPath(), $"frameforge-functional-{Guid.NewGuid():N}");
        try
        {
            var result = FunctionalDesignPackageExporter.Export(project, null, Source, destination);
            Assert.True(result.Success, string.Join("\n", result.Preparation.Diagnostics));
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, Wow335Exporter.ManifestFileName)));
            var controls = manifest.RootElement.GetProperty("controlInventory").EnumerateArray().ToArray();
            var value = Assert.Single(controls, entry => entry.GetProperty("authoredName").GetString() == "Value");
            Assert.Equal("Value", value.GetProperty("exportedName").GetString());
            Assert.Equal("FontString", value.GetProperty("type").GetString());
            Assert.Equal("_G[\"Value\"]", value.GetProperty("luaAccess").GetString());

            var exported = XDocument.Load(Path.Combine(destination, "Functional.xml"));
            var element = exported.Descendants().Single(node => (string?)node.Attribute("name") == value.GetProperty("exportedName").GetString());
            var actualParentPath = string.Join("/", element.Ancestors().Reverse()
                .Select(node => (string?)node.Attribute("name")).Where(name => !string.IsNullOrWhiteSpace(name)));
            Assert.Equal(actualParentPath, value.GetProperty("parentPath").GetString());
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
        }
    }

    [Fact]
    public void RealNativeHuntsDesignExportsFortyNineObjectsWithoutMappingsWhenAvailable()
    {
        var projectPath = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_PROJECT");
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return;
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "Fixtures", "NativeHuntsFrame.xml"));
        var projectBytes = File.ReadAllBytes(projectPath);
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var parsed = ProjectCodec.Parse(File.ReadAllText(projectPath));
        Assert.True(parsed.Ok, parsed.ErrorText);
        var sourceXml = File.ReadAllText(sourcePath);
        var project = parsed.Project! with
        {
            FunctionalExport = new FunctionalExportProfile
            {
                Source = new ProjectSource
                {
                    Type = SourceTypes.WowFrameXml,
                    FileName = "NativeHuntsFrame.xml",
                    ReferencePath = "NativeHuntsFrame.xml",
                    ReadOnly = true,
                    Sha256 = FrameXmlImporter.ComputeSha256(sourceXml),
                },
                HostFrameName = "NativeHuntsFrame",
            },
        };

        var result = FunctionalDesignExporter.Prepare(project, projectPath, sourceXml);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(49, result.DesignObjectCount);
        Assert.Contains(result.DesignModel!.Objects, item => item.RuntimeName == "Hunt_Seal_Value");
        Assert.Contains(result.DesignModel.Objects, item => item.RuntimeName == "Standard_Hunts_Value");
        Assert.Contains(result.DesignModel.Objects, item => item.RuntimeName == "Elite_Hunts_Value");
        var xml = XDocument.Parse(result.Xml!);
        var generatedRoot = xml.Descendants().Single(element =>
            (string?)element.Attribute("name") == result.DesignModel.RootRuntimeName);
        Assert.DoesNotContain(generatedRoot.Elements(), element => element.Name.LocalName == "Scripts");
        var destination = Path.Combine(Path.GetTempPath(), $"frameforge-native-functional-{Guid.NewGuid():N}");
        try
        {
            var package = FunctionalDesignPackageExporter.Export(project, projectPath, sourceXml, destination);
            Assert.True(package.Success, string.Join("\n", package.Preparation.Diagnostics));
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, Wow335Exporter.ManifestFileName)));
            Assert.Equal(49, manifest.RootElement.GetProperty("controlInventory").GetArrayLength());
            Assert.Contains(manifest.RootElement.GetProperty("controlInventory").EnumerateArray(), item =>
                item.GetProperty("exportedName").GetString() == "Hunt_Seal_Value"
                && item.GetProperty("luaAccess").GetString() == "_G[\"Hunt_Seal_Value\"]");
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
        }
        Assert.Equal(projectBytes, File.ReadAllBytes(projectPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    private static Project Design()
    {
        var profile = new FunctionalExportProfile
        {
            Source = new ProjectSource
            {
                Type = SourceTypes.WowFrameXml,
                FileName = "Functional.xml",
                ReferencePath = "Functional.xml",
                ReadOnly = true,
                Sha256 = FrameXmlImporter.ComputeSha256(Source),
            },
            HostFrameName = "FunctionalHost",
            Values = [new FunctionalValueBinding { DesignFrameName = "Value", SourceFrameName = "SourceValue" }],
        };
        return ProjectFactory.Create("Functional Fixture",
        [
            new FrameDef { Name = "Stock", Width = 356, Height = 500, Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER },
            new FrameDef
            {
                Name = "Value", Kind = FrameKind.FONTSTRING, Width = 100, Height = 20,
                Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER,
                Visual = new FrameVisual { Text = new TextVisual("preview") },
            },
            new FrameDef
            {
                Name = "Progress", Kind = FrameKind.STATUSBAR, Parent = "Value", Width = 100, Height = 12,
                Point = AnchorPoint.BOTTOM, RelativePoint = AnchorPoint.TOP,
                Visual = new FrameVisual
                {
                    StatusBar = new StatusBarVisual(0, 100, 42, @"Interface\TargetingFrame\UI-StatusBar"),
                },
            },
        ]) with
        {
            FunctionalExport = profile,
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup { Name = "Stock", Concept = "stock-framework", StockIdentity = "LFDParentFrame", Members = ["Stock"] }],
                DesignObjects =
                [
                    new DesignObjectMetadata { FrameName = "Value", RuntimeValueRequired = true, RuntimeBinding = "fixture.value" },
                    new DesignObjectMetadata { FrameName = "Progress" },
                ],
                DesignOrder = ["Value", "Progress"],
            },
        };
    }
}
