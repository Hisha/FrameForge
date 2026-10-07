using System.Xml.Linq;
using System.Text.Json;
using FrameForge.Core.Export;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class Wow335ExportTests
{
    [Fact]
    public void ExportIsDeterministicPortableAndWellFormed()
    {
        using var fixture = ExportFixture.Create();
        var first = Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output1);
        var second = Wow335Exporter.Export(fixture.Project, fixture.ProjectPath, fixture.Output2);
        Assert.True(first.Success, string.Join("\n", first.Diagnostics));
        foreach (var file in first.WrittenFiles.Select(Path.GetFileName))
            Assert.Equal(File.ReadAllText(Path.Combine(fixture.Output1, file!)), File.ReadAllText(Path.Combine(fixture.Output2, file!)));
        _ = XDocument.Load(Path.Combine(fixture.Output1, Wow335Exporter.XmlFileName));
        var all = string.Join("\n", first.WrittenFiles.Select(File.ReadAllText));
        Assert.DoesNotContain(fixture.Root, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppData", all, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output1, "*.blp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(fixture.Output1, "*.png", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(fixture.Output1, "*.ttf", SearchOption.AllDirectories));
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
        Assert.Equal("panel.png", model.Assets.Single().SourceProjectAsset);
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
            Assert.Contains("Round-trip self-check: PASS (49/49 runtime identities)", File.ReadAllText(Path.Combine(destination, Wow335Exporter.ReportFileName)));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
        }
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
            File.WriteAllBytes(Path.Combine(root, "panel.png"), [1, 2, 3]);
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
}
