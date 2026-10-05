using FrameForge.Core.Import;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class EditorMetadataTests
{
    [Fact]
    public void Import_retains_reliable_element_line_and_column()
    {
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "Fixtures", "NativeHuntsFrame.xml"));
        var project = FrameXmlImporter.ImportFile(fixture).Project!;
        var record = project.Find("NativeHuntsFrameContentPanelRecord")!;
        Assert.Equal(71, record.SourceLocation?.Line);
        Assert.True(record.SourceLocation?.Column > 0);
        Assert.Equal("NativeHuntsFrame.xml", project.Source?.FileName);
    }

    [Fact]
    public void Groups_and_locks_round_trip_without_changing_the_format_version()
    {
        var project = new Project
        {
            Frames = [new FrameDef { Name = "Chrome" }, new FrameDef { Name = "Content" }],
            Editor = new EditorMetadata
            {
                LockedElements = ["Content"],
                Groups = [new EditorGroup { Name = "Blizzard Chrome", Members = ["Chrome"], Locked = true }],
            },
        };
        var json = ProjectCodec.Serialize(project);
        var reopened = ProjectCodec.Parse(json);
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal(Project.FormatVersion, 1);
        Assert.True(reopened.Project!.Editor.IsLocked("Chrome"));
        Assert.True(reopened.Project.Editor.IsLocked("Content"));
        Assert.Equal("Blizzard Chrome", Assert.Single(reopened.Project.Editor.Groups).Name);
        Assert.Contains("\"editor\"", json);
    }

    [Fact]
    public void Older_v1_project_without_editor_metadata_loads_with_empty_defaults()
    {
        const string json = """
        {"format":"frameforge-project","version":1,"name":"Old","screen":{"width":1024,"height":768},
         "frames":[{"name":"A","parent":null,"width":10,"height":10,"point":"TOPLEFT",
         "relativeTo":null,"relativePoint":"TOPLEFT","offsetX":0,"offsetY":0,"visible":true}]}
        """;
        var result = ProjectCodec.Parse(json);
        Assert.True(result.Ok, result.ErrorText);
        Assert.Empty(result.Project!.Editor.Groups);
        Assert.Empty(result.Project.Editor.LockedElements);
        Assert.Equal("design", result.Project.Editor.Workspace);
        Assert.Empty(result.Project.Editor.DesignStates);
    }

    [Fact]
    public void Design_identity_states_membership_and_stock_presentation_round_trip_additively()
    {
        var project = new Project
        {
            Frames = [new FrameDef { Name = "LFDParentFrame" }, new FrameDef { Name = "DesignObject" }],
            Editor = new EditorMetadata
            {
                Workspace = "inspect",
                ActiveDesignStateId = "standard",
                DesignStates = [new DesignState { Id = "standard", Name = "Standard Hunt" }],
                DesignObjects =
                [
                    new DesignObjectMetadata
                    {
                        FrameName = "DesignObject", DisplayName = "Hunt Record",
                        TextOverride = "NATIVE HUNTS", DesignAsset = "assets/hunt_divider.png",
                        TextStyle = new DesignTextStyleMetadata
                        {
                            BaseStyle = "GameFontNormalLarge", Size = 14,
                            Color = new ColorRgba(1, 0.82, 0, 0.9), Outline = "NORMAL",
                            Shadow = false, JustifyH = "CENTER",
                        },
                        StateIds = ["standard"],
                    },
                ],
                DesignOrder = ["DesignObject"],
                Groups =
                [
                    new EditorGroup
                    {
                        Name = "Blizzard Dungeon Finder Frame", Members = ["LFDParentFrame"], Locked = true,
                        Expanded = true, Concept = "stock-framework", StockIdentity = "wow-3.3.5a-12340:LFDParentFrame",
                    },
                ],
            },
        };
        var json = ProjectCodec.Serialize(project);
        var reopened = ProjectCodec.Parse(json);
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal(project, reopened.Project);
        Assert.Equal("Hunt Record", reopened.Project!.Editor.DisplayNameFor(reopened.Project.Find("DesignObject")!));
        Assert.Equal("NATIVE HUNTS", reopened.Project.Editor.DesignObjectFor("DesignObject")!.TextOverride);
        Assert.Equal("assets/hunt_divider.png", reopened.Project.Editor.DesignObjectFor("DesignObject")!.DesignAsset);
        Assert.Equal(project.Editor.DesignObjectFor("DesignObject")!.TextStyle,
            reopened.Project.Editor.DesignObjectFor("DesignObject")!.TextStyle);
        Assert.Equal(Project.FormatVersion, 1);
    }

    [Fact]
    public void Older_design_projects_without_explicit_order_use_deterministic_design_object_order()
    {
        var project = new Project
        {
            Frames =
            [
                new FrameDef { Name = "Stock" },
                new FrameDef { Name = "CustomB" },
                new FrameDef { Name = "CustomA" },
            ],
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup { Name = "Blizzard", Members = ["Stock"], Locked = true, Concept = "stock-framework" }],
                DesignObjects =
                [
                    new DesignObjectMetadata { FrameName = "CustomA" },
                    new DesignObjectMetadata { FrameName = "CustomB" },
                ],
            },
        };

        Assert.Equal(["CustomA", "CustomB"], project.Editor.EffectiveDesignOrder(project));
        Assert.Equal(["Stock", "CustomA", "CustomB"], LayoutResolver.Resolve(project).PaintOrder);
    }

    [Fact]
    public void Friendly_anonymous_texture_name_uses_asset_but_preserves_internal_identity()
    {
        var frame = new FrameDef
        {
            Name = "Texture#18", Anonymous = true, Kind = FrameKind.TEXTURE,
            Visual = new FrameVisual { Texture = new TextureVisual(@"Interface\NativeHunts\hunt_panel_record.tga") },
        };
        Assert.Equal("Hunt Panel Record", new EditorMetadata().DisplayNameFor(frame));
        Assert.Equal("Texture#18", frame.Name);
    }

    [Fact]
    public void Import_supports_blizzard_nested_absolute_size_syntax()
    {
        const string xml = """
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="Stock"><Size><AbsDimension x="355" y="440"/></Size></Frame>
            </Ui>
            """;
        var result = FrameXmlImporter.Import(xml, "stock.xml");
        Assert.True(result.Ok);
        Assert.Equal(355, result.Project!.Find("Stock")!.Width);
        Assert.Equal(440, result.Project.Find("Stock")!.Height);
    }
}
