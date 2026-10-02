using FrameForge.Core.Examples;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// The <c>*.fforge.json</c> format contract: strict where it matters, tolerant of unknown
/// keys, and stable on the way out.
/// </summary>
public class SerializationTests
{
    private const string Minimal = """
        {
          "format": "frameforge-project",
          "version": 1,
          "name": "Minimal",
          "screen": { "width": 1024, "height": 768 },
          "frames": [
            {
              "name": "Root",
              "parent": null,
              "width": 100,
              "height": 50,
              "point": "TOPLEFT",
              "relativeTo": null,
              "relativePoint": "TOPLEFT",
              "offsetX": 0,
              "offsetY": 0,
              "visible": true
            }
          ]
        }
        """;

    private static Project Parsed(string json)
    {
        var result = ProjectCodec.Parse(json);
        Assert.True(result.Ok, result.ErrorText);
        return result.Project!;
    }

    private static string ErrorsOf(string json)
    {
        var result = ProjectCodec.Parse(json);
        Assert.False(result.Ok, "expected the document to be rejected");
        return result.ErrorText;
    }

    [Fact]
    public void AcceptsAMinimalDocument()
    {
        var project = Parsed(Minimal);

        Assert.Equal("Minimal", project.Name);
        Assert.Single(project.Frames);
        Assert.Equal("Root", project.Frames[0].Name);
    }

    [Fact]
    public void RejectsNonJsonWithAUsefulMessage()
    {
        Assert.Contains("Not valid JSON", ErrorsOf("{ nope"));
    }

    [Fact]
    public void RejectsADocumentThatIsNotAFrameForgeProject()
    {
        Assert.Contains(Project.FormatId, ErrorsOf("""{ "hello": "world" }"""));
    }

    [Fact]
    public void RejectsAFutureSchemaVersionRatherThanGuessing()
    {
        var errors = ErrorsOf(
            """{ "format": "frameforge-project", "version": 99, "name": "x", "frames": [] }""");

        Assert.Contains("newer than this build", errors);
    }

    [Fact]
    public void RejectsAnInvalidAnchorPoint()
    {
        var errors = ErrorsOf(Minimal.Replace("\"point\": \"TOPLEFT\"", "\"point\": \"MIDDLE\""));

        Assert.Contains("TOPLEFT", errors);
    }

    [Fact]
    public void RejectsDuplicateFrameNames()
    {
        var frame = """{ "name": "Root", "width": 10, "height": 10 }""";
        var errors = ErrorsOf(
            $$"""{ "format": "frameforge-project", "version": 1, "name": "d", "frames": [{{frame}}, {{frame}}] }""");

        Assert.Contains("duplicated", errors);
    }

    [Fact]
    public void RejectsADanglingParentReference()
    {
        var errors = ErrorsOf(Minimal.Replace("\"parent\": null", "\"parent\": \"Nope\""));

        Assert.Contains("does not exist", errors);
    }

    [Fact]
    public void RejectsADanglingAnchorReference()
    {
        var errors = ErrorsOf(Minimal.Replace("\"relativeTo\": null", "\"relativeTo\": \"Nope\""));

        Assert.Contains("does not exist", errors);
    }

    [Fact]
    public void RejectsANonFiniteNumber()
    {
        Assert.NotEmpty(ErrorsOf(Minimal.Replace("\"offsetX\": 0", "\"offsetX\": \"ten\"")));
    }

    [Fact]
    public void RejectsAScreenWithNoPositiveSize()
    {
        var errors = ErrorsOf(Minimal.Replace("\"width\": 1024", "\"width\": 0"));

        Assert.Contains("greater than zero", errors);
    }

    [Fact]
    public void RequiresWidthAndHeightRatherThanInventingThem()
    {
        var errors = ErrorsOf(
            """{ "format": "frameforge-project", "version": 1, "name": "NoSize", "frames": [ { "name": "Only" } ] }""");

        Assert.Contains("width must be a finite number", errors);
        Assert.Contains("height must be a finite number", errors);
    }

    [Fact]
    public void FillsInDocumentedDefaultsForOmittedOptionalFields()
    {
        var project = Parsed(
            """{ "format": "frameforge-project", "version": 1, "name": "Sparse", "frames": [ { "name": "Only", "width": 40, "height": 20 } ] }""");

        var frame = Assert.Single(project.Frames);
        Assert.Equal(40, frame.Width);
        Assert.Equal(20, frame.Height);
        Assert.Equal(AnchorPoint.TOPLEFT, frame.Point);
        Assert.Equal(AnchorPoint.TOPLEFT, frame.RelativePoint);
        Assert.Equal(0, frame.OffsetX);
        Assert.Equal(0, frame.OffsetY);
        Assert.True(frame.Visible);
        Assert.Null(frame.Parent);
        Assert.Null(frame.RelativeTo);
        Assert.Equal(new Screen(1024, 768), project.Screen);
    }

    [Fact]
    public void KeepsUnknownExtraKeysFromBreakingOlderBuilds()
    {
        var project = Parsed(
            """
            {
              "format": "frameforge-project",
              "version": 1,
              "name": "Future",
              "futureThing": { "a": 1 },
              "frames": [ { "name": "Only", "width": 10, "height": 10, "someFutureField": 42 } ]
            }
            """);

        Assert.Single(project.Frames);
    }

    [Fact]
    public void RoundTripsAProjectWithoutLoss()
    {
        var project = TestProject.Project(
            TestProject.Frame("A", width: 10, height: 20, offsetX: -3, offsetY: 4.5, visible: false),
            TestProject.Frame("B", parent: "A", point: AnchorPoint.BOTTOM, relativePoint: AnchorPoint.TOP,
                relativeTo: "A", offsetX: 7, offsetY: -8),
            TestProject.Frame("C", parent: "A", width: 0.5, height: 0.25,
                sizeReference: SizeReference.PARENT, stratum: Stratum.DIALOG, level: 3));

        var parsed = Parsed(ProjectCodec.Serialize(project));

        Assert.Equal(project, parsed);
    }

    [Fact]
    public void EmitsTheCurrentSchemaVersionAndATrailingNewline()
    {
        var text = ProjectCodec.Serialize(ProjectFactory.Empty());

        Assert.Contains($"\"format\": \"{Project.FormatId}\"", text);
        Assert.Contains($"\"version\": {Project.FormatVersion}", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void IsStableAcrossRepeatedSerialisation()
    {
        var project = ProjectFactory.Empty();

        Assert.Equal(ProjectCodec.Serialize(project), ProjectCodec.Serialize(project));
    }

    [Fact]
    public void WritesExplicitNullsSoTheFileIsReadableWithoutASchema()
    {
        var text = ProjectCodec.Serialize(ProjectFactory.Empty());

        Assert.Contains("\"parent\": null", text);
        Assert.Contains("\"relativeTo\": null", text);
    }

    [Fact]
    public void OmitsOptionalFieldsThatAreNotSet()
    {
        var text = ProjectCodec.Serialize(ProjectFactory.Empty());

        Assert.DoesNotContain("sizeReference", text);
        Assert.DoesNotContain("stratum", text);
        Assert.DoesNotContain("level", text);
    }

    [Fact]
    public void TheNativeHuntsExampleRoundTripsToIdenticalGeometry()
    {
        var example = NativeHuntsExample.CreateProject();

        var before = LayoutResolver.Resolve(example);
        var after = LayoutResolver.Resolve(Parsed(ProjectCodec.Serialize(example)));

        Assert.Equal(before.Rects, after.Rects);
        Assert.Equal(before.PaintOrder, after.PaintOrder);
    }
}