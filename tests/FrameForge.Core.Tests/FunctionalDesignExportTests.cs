using FrameForge.Core.Export;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
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
    public void CompositionPreservesAuthoritativeSourceAndAddsMappedDesign()
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
    public void ChangedSourceAndIncompleteValueMappingAreBlocked()
    {
        var design = Design();
        var changed = FunctionalDesignExporter.Prepare(design, null, Source.Replace("runtime", "changed"));
        Assert.False(changed.Success);
        Assert.Contains(changed.Diagnostics, item => item.Code == "FUNCTIONAL_SOURCE_HASH_MISMATCH");

        var incomplete = FunctionalDesignExporter.Prepare(design with
        {
            FunctionalExport = design.FunctionalExport! with { Values = [] },
        }, null, Source);
        Assert.False(incomplete.Success);
        Assert.Contains(incomplete.Diagnostics, item => item.Code == "FUNCTIONAL_VALUE_UNMAPPED");
    }

    [Fact]
    public void FunctionalAssociationRoundTripsAsPersistentProjectMetadata()
    {
        var project = Design();
        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(project));
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal(project.FunctionalExport, reopened.Project!.FunctionalExport);
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
        ]) with
        {
            FunctionalExport = profile,
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup { Name = "Stock", Concept = "stock-framework", StockIdentity = "LFDParentFrame", Members = ["Stock"] }],
                DesignObjects = [new DesignObjectMetadata { FrameName = "Value", RuntimeValueRequired = true, RuntimeBinding = "fixture.value" }],
                DesignOrder = ["Value"],
            },
        };
    }
}
