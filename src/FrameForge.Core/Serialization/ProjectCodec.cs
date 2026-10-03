using System.Globalization;
using System.Text;
using System.Text.Json;
using FrameForge.Core.Models;

namespace FrameForge.Core.Serialization;

/// <summary>Outcome of parsing a project document.</summary>
public sealed record ParseResult
{
    private ParseResult(Project? project, IReadOnlyList<string> errors)
    {
        Project = project;
        Errors = errors;
    }

    /// <summary>The parsed project, or null when parsing failed.</summary>
    public Project? Project { get; }

    /// <summary>Every problem found. Empty on success.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>True when the document parsed cleanly.</summary>
    public bool Ok => Project is not null;

    /// <summary>Errors joined for display.</summary>
    public string ErrorText => string.Join(Environment.NewLine, Errors);

    /// <summary>Successful parse.</summary>
    public static ParseResult Success(Project project) => new(project, []);

    /// <summary>Failed parse.</summary>
    public static ParseResult Failure(IReadOnlyList<string> errors) => new(null, errors.Count == 0 ? ["Unknown error."] : errors);

    /// <summary>Failed parse with a single error.</summary>
    public static ParseResult Failure(string error) => Failure([error]);
}

/// <summary>
/// Reads and writes the FrameForge project format (<c>*.fforge.json</c>).
/// </summary>
/// <remarks>
/// Strict about the fields it needs and tolerant of unknown extra keys, so a file written
/// by a newer FrameForge still opens in an older one as long as nothing it relies on has
/// changed. Output is pretty-printed with a stable key order and an explicit
/// <c>null</c> for absent parent/relativeTo, so the file is readable without a schema.
/// <para>
/// The format stores WoW model values only. No Avalonia or screen coordinate ever reaches
/// this file.
/// </para>
/// </remarks>
public static class ProjectCodec
{
    /// <summary>One entry in a file-type filter: what the dialog shows, and what it matches.</summary>
    /// <param name="Label">Text shown in the file-type dropdown.</param>
    /// <param name="Patterns">
    /// Globs the filter matches, e.g. <c>["*.xml"]</c>. More than one is how a single entry
    /// covers both formats FrameForge opens.
    /// </param>
    public readonly record struct OpenFilter(string Label, string[] Patterns);

    /// <summary>The file extension used by FrameForge projects.</summary>
    public const string FileExtension = ".fforge.json";

    /// <summary>Parses a project document.</summary>
    public static ParseResult Parse(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return ParseResult.Failure($"Not valid JSON: {ex.Message}");
        }

        using (document)
            return Parse(document.RootElement);
    }

    /// <summary>Parses a project from an already-decoded JSON element.</summary>
    public static ParseResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return ParseResult.Failure("Project file must contain a JSON object.");

        var errors = new List<string>();

        if (!root.TryGetProperty("format", out var format) ||
            format.ValueKind != JsonValueKind.String ||
            format.GetString() != Project.FormatId)
        {
            var found = format.ValueKind == JsonValueKind.Undefined ? "nothing" : $"\"{format}\"";
            return ParseResult.Failure(
                $"Expected \"format\": \"{Project.FormatId}\" but found {found}. This is not a FrameForge project file.");
        }

        var version = ReadNumber(root, "version", "project", errors, 0);
        if (errors.Count > 0)
            return ParseResult.Failure(errors);

        if (version > Project.FormatVersion)
        {
            return ParseResult.Failure(
                $"Project version {FormatNumber(version)} is newer than this build supports ({Project.FormatVersion}). Update FrameForge.");
        }

        var name = root.TryGetProperty("name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(nameElement.GetString())
            ? nameElement.GetString()!
            : "Untitled";

        var screen = ParseScreen(root, errors);
        var frames = ParseFrames(root, errors);
        var source = ParseSource(root);

        if (errors.Count > 0)
            return ParseResult.Failure(errors);

        return ParseResult.Success(new Project
        {
            Name = name,
            Screen = screen,
            Frames = frames,
            Source = source,
        });
    }

    /// <summary>
    /// Reads the optional source block.
    /// </summary>
    /// <remarks>
    /// Source metadata is informational. It is read best-effort and never fatal: a project that
    /// names an XML file which no longer exists must still open, because the layout in the file
    /// is self-contained and FrameForge never needs the original to resolve it.
    /// </remarks>
    private static ProjectSource? ParseSource(JsonElement root)
    {
        if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            return null;

        var type = source.TryGetProperty("type", out var typeElement) &&
                   typeElement.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(typeElement.GetString())
            ? typeElement.GetString()!
            : SourceTypes.FrameForge;

        return new ProjectSource
        {
            Type = type,
            FileName = ReadOptionalString(source, "fileName"),
            ReferencePath = ReadOptionalString(source, "referencePath"),
            ReadOnly = source.TryGetProperty("readOnly", out var readOnly) &&
                       readOnly.ValueKind == JsonValueKind.True,
        };
    }

    private static string? ReadOptionalString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>Serializes a project to its on-disk representation, trailing newline included.</summary>
    public static string Serialize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Project.FormatId);
            writer.WriteNumber("version", Project.FormatVersion);
            writer.WriteString("name", project.Name);

            writer.WriteStartObject("screen");
            writer.WriteNumber("width", project.Screen.Width);
            writer.WriteNumber("height", project.Screen.Height);
            writer.WriteEndObject();

            writer.WriteStartArray("frames");
            foreach (var frame in project.Frames)
            {
                writer.WriteStartObject();
                writer.WriteString("name", frame.Name);
                WriteNullableString(writer, "parent", frame.Parent);
                writer.WriteNumber("width", frame.Width);
                writer.WriteNumber("height", frame.Height);
                writer.WriteString("point", frame.Point.ToString());
                WriteNullableString(writer, "relativeTo", frame.RelativeTo);
                writer.WriteString("relativePoint", frame.RelativePoint.ToString());
                writer.WriteNumber("offsetX", frame.OffsetX);
                writer.WriteNumber("offsetY", frame.OffsetY);
                writer.WriteBoolean("visible", frame.Visible);
                if (frame.SizeReference is { } sizeReference)
                    writer.WriteString("sizeReference", sizeReference.ToString());
                if (frame.Stratum is { } stratum)
                    writer.WriteString("stratum", stratum.ToString());
                if (frame.Level is { } level)
                    writer.WriteNumber("level", level);

                // Optional keys are omitted when they hold their default, which keeps a
                // hand-authored project file as small as it was in v0.1 and makes an imported
                // file's extra provenance obvious exactly where it exists.
                if (frame.Kind != FrameKind.FRAME)
                    writer.WriteString("kind", frame.Kind.ToString());
                if (frame.SetAllPoints)
                    writer.WriteBoolean("setAllPoints", true);
                if (frame.ExtraAnchors.Count > 0)
                {
                    writer.WriteStartArray("extraAnchors");
                    foreach (var anchor in frame.ExtraAnchors)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("point", anchor.Point.ToString());
                        WriteNullableString(writer, "relativeTo", anchor.RelativeTo);
                        writer.WriteString("relativePoint", anchor.RelativePoint.ToString());
                        writer.WriteNumber("offsetX", anchor.OffsetX);
                        writer.WriteNumber("offsetY", anchor.OffsetY);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                if (frame.SourceName is { Length: > 0 } sourceName)
                    writer.WriteString("sourceName", sourceName);
                if (frame.Anonymous)
                    writer.WriteBoolean("anonymous", true);
                if (frame.Inherits is { Length: > 0 } inherits)
                    writer.WriteString("inherits", inherits);
                if (frame.Placeholder)
                    writer.WriteBoolean("placeholder", true);

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (project.Source is { } source)
            {
                writer.WriteStartObject("source");
                writer.WriteString("type", source.Type);
                if (source.FileName is { Length: > 0 } fileName)
                    writer.WriteString("fileName", fileName);
                if (source.ReferencePath is { Length: > 0 } referencePath)
                    writer.WriteString("referencePath", referencePath);
                if (source.ReadOnly)
                    writer.WriteBoolean("readOnly", true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>The platform-appropriate file-type filter for open/save dialogs.</summary>
    public static readonly string[] DialogFilters = [$"FrameForge project (*{FileExtension})", $"*{FileExtension}"];

    /// <summary>
    /// The filters the Open dialog offers, in the order the dialog receives them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ORDER IS PART OF THE CONTRACT. The first entry is the dialog's default, so it must be
    /// "All Supported Files": Open reads a FrameForge project <i>or</i> a WoW FrameXML document,
    /// and the overwhelmingly common case is clicking Open, navigating to a directory, and seeing
    /// whichever kind of file is actually there. A narrower default would hide one of the two
    /// formats behind a dropdown interaction.
    /// </para>
    /// <para>
    /// The narrower entries stay available for when the user does want to see one format only,
    /// and "All Files" is the escape hatch for an addon directory full of other file types.
    /// </para>
    /// <para>
    /// This is deliberately the FIRST filter rather than the only one. On Linux the Open dialog
    /// is the XDG desktop portal (<c>org.freedesktop.portal.FileChooser</c>), whose
    /// <c>OpenFile</c> call takes the whole filter list once and then hands the dialog to a
    /// remote backend. That backend owns the file-type combo and re-listing its contents, and the
    /// portal exposes no call for changing or refreshing a filter afterwards - so whether
    /// switching the combo immediately re-lists the current directory is entirely the backend's
    /// behaviour, not something a client can drive. See <c>docs/OPEN_DIALOG.md</c>.
    /// </para>
    /// </remarks>
    public static readonly OpenFilter[] OpenDialogFilters =
    [
        new("All Supported Files", [$"*{FileExtension}", $"*{XmlFileExtension}"]),
        new($"FrameForge Projects (*{FileExtension})", [$"*{FileExtension}"]),
        new($"WoW FrameXML (*{XmlFileExtension})", [$"*{XmlFileExtension}"]),
        new("All Files (*)", ["*.*"]),
    ];

    /// <summary>The FrameXML extension the importer accepts.</summary>
    public const string XmlFileExtension = ".xml";

    /// <summary>True when the path looks like a WoW FrameXML document rather than a project.</summary>
    public static bool IsXmlPath(string path) =>
        Path.GetExtension(path).Equals(XmlFileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the path looks like a FrameForge project document.</summary>
    public static bool IsProjectPath(string path) =>
        Path.GetExtension(path).Equals(FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Blocks a write that would land on the imported FrameXML source.
    /// </summary>
    /// <remarks>
    /// FrameForge has no XML writer and never edits a FrameXML file, but "we have no way to do
    /// it" is a weaker guarantee than "the save path is refused", and a future format could
    /// change that. The guard is on the path only: it cannot tell one XML file from another, so
    /// it refuses any <c>.xml</c> target and points the user at the project extension instead.
    /// </remarks>
    public static bool CanSaveTo(string path) => !IsXmlPath(path);

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static Screen ParseScreen(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("screen", out var screen) || screen.ValueKind == JsonValueKind.Null)
            return Screen.Default;

        if (screen.ValueKind != JsonValueKind.Object)
        {
            errors.Add("screen must be an object with width and height.");
            return Screen.Default;
        }

        var width = ReadNumber(screen, "width", "screen", errors, 1024);
        var height = ReadNumber(screen, "height", "screen", errors, 768);

        if (width <= 0 || height <= 0)
        {
            errors.Add("screen.width and screen.height must be greater than zero.");
            return Screen.Default;
        }

        return new Screen(width, height);
    }

    private static IReadOnlyList<FrameDef> ParseFrames(JsonElement root, List<string> errors)
    {
        var frames = new List<FrameDef>();
        if (!root.TryGetProperty("frames", out var array))
        {
            errors.Add("frames must be an array of frame objects.");
            return frames;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("frames must be an array of frame objects.");
            return frames;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var frame = ParseFrame(element, index, errors);
            if (frame is not null)
            {
                if (!seen.Add(frame.Name))
                    errors.Add($"frames[{index}].name \"{frame.Name}\" is duplicated; frame names must be unique.");
                else
                    frames.Add(frame);
            }

            index++;
        }

        foreach (var frame in frames)
        {
            if (frame.Parent is not null && !seen.Contains(frame.Parent))
                errors.Add($"Frame \"{frame.Name}\" has parent \"{frame.Parent}\", which does not exist.");
            if (frame.RelativeTo is not null && !seen.Contains(frame.RelativeTo))
                errors.Add($"Frame \"{frame.Name}\" is anchored to \"{frame.RelativeTo}\", which does not exist.");
        }

        return frames;
    }

    private static FrameDef? ParseFrame(JsonElement element, int index, List<string> errors)
    {
        var path = $"frames[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} must be an object.");
            return null;
        }

        if (!element.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            errors.Add($"{path}.name must be a non-empty string.");
            return null;
        }

        var name = nameElement.GetString()!;

        SizeReference? sizeReference = null;
        if (element.TryGetProperty("sizeReference", out var sizeRefElement) && sizeRefElement.ValueKind != JsonValueKind.Null)
        {
            if (sizeRefElement.ValueKind == JsonValueKind.String &&
                Enum.TryParse<SizeReference>(sizeRefElement.GetString(), out var parsedSizeReference))
            {
                sizeReference = parsedSizeReference;
            }
            else
            {
                errors.Add($"{path}.sizeReference must be PARENT or SCREEN.");
            }
        }

        Stratum? stratum = null;
        if (element.TryGetProperty("stratum", out var stratumElement) && stratumElement.ValueKind != JsonValueKind.Null)
        {
            if (stratumElement.ValueKind == JsonValueKind.String &&
                Enum.TryParse<Stratum>(stratumElement.GetString(), out var parsedStratum))
            {
                stratum = parsedStratum;
            }
            else
            {
                errors.Add($"{path}.stratum must be one of {string.Join(", ", Enum.GetNames<Stratum>())}.");
            }
        }

        var visible = true;
        if (element.TryGetProperty("visible", out var visibleElement) && visibleElement.ValueKind != JsonValueKind.Null)
        {
            if (visibleElement.ValueKind == JsonValueKind.True || visibleElement.ValueKind == JsonValueKind.False)
                visible = visibleElement.GetBoolean();
            else
                errors.Add($"{path}.visible must be true or false.");
        }

        int? level = null;
        if (element.TryGetProperty("level", out var levelElement) && levelElement.ValueKind != JsonValueKind.Null)
        {
            var parsedLevel = ReadNumber(element, "level", path, errors, 0);
            if (parsedLevel % 1 == 0)
                level = (int)parsedLevel;
            else
                errors.Add($"{path}.level must be a whole number.");
        }

        var kind = FrameKind.FRAME;
        if (element.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind != JsonValueKind.Null)
        {
            if (kindElement.ValueKind == JsonValueKind.String &&
                Enum.TryParse<FrameKind>(kindElement.GetString(), ignoreCase: false, out var parsedKind))
            {
                kind = parsedKind;
            }
            else
            {
                errors.Add($"{path}.kind must be one of {string.Join(", ", Enum.GetNames<FrameKind>())}.");
            }
        }

        var setAllPoints = element.TryGetProperty("setAllPoints", out var setAllPointsElement) &&
                           setAllPointsElement.ValueKind == JsonValueKind.True;

        var placeholder = element.TryGetProperty("placeholder", out var placeholderElement) &&
                          placeholderElement.ValueKind == JsonValueKind.True;

        var anonymous = element.TryGetProperty("anonymous", out var anonymousElement) &&
                        anonymousElement.ValueKind == JsonValueKind.True;

        return new FrameDef
        {
            Name = name,
            Parent = ReadFrameName(element, "parent", path, errors),
            Width = ReadNumber(element, "width", path, errors, required: true),
            Height = ReadNumber(element, "height", path, errors, required: true),
            Point = ReadAnchor(element, "point", path, errors, ProjectFactory.DefaultPoint),
            RelativeTo = ReadFrameName(element, "relativeTo", path, errors),
            RelativePoint = ReadAnchor(element, "relativePoint", path, errors, ProjectFactory.DefaultPoint),
            OffsetX = ReadNumber(element, "offsetX", path, errors, 0),
            OffsetY = ReadNumber(element, "offsetY", path, errors, 0),
            Visible = visible,
            SizeReference = sizeReference,
            Stratum = stratum,
            Level = level,
            Kind = kind,
            SetAllPoints = setAllPoints,
            ExtraAnchors = ReadExtraAnchors(element, path, errors),
            SourceName = ReadOptionalString(element, "sourceName"),
            Anonymous = anonymous,
            Inherits = ReadOptionalString(element, "inherits"),
            Placeholder = placeholder,
        };
    }

    private static IReadOnlyList<FrameAnchor> ReadExtraAnchors(JsonElement element, string path, List<string> errors)
    {
        if (!element.TryGetProperty("extraAnchors", out var array) || array.ValueKind == JsonValueKind.Null)
            return [];

        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.extraAnchors must be an array of anchor objects.");
            return [];
        }

        var anchors = new List<FrameAnchor>();
        var index = 0;
        foreach (var entry in array.EnumerateArray())
        {
            var anchorPath = $"{path}.extraAnchors[{index}]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{anchorPath} must be an object.");
                index++;
                continue;
            }

            anchors.Add(new FrameAnchor
            {
                Point = ReadAnchor(entry, "point", anchorPath, errors, ProjectFactory.DefaultPoint),
                RelativeTo = ReadFrameName(entry, "relativeTo", anchorPath, errors),
                RelativePoint = ReadAnchor(entry, "relativePoint", anchorPath, errors, ProjectFactory.DefaultPoint),
                OffsetX = ReadNumber(entry, "offsetX", anchorPath, errors, 0),
                OffsetY = ReadNumber(entry, "offsetY", anchorPath, errors, 0),
            });

            index++;
        }

        return anchors;
    }

    private static AnchorPoint ReadAnchor(JsonElement element, string key, string path, List<string> errors, AnchorPoint fallback)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            return fallback;

        if (value.ValueKind == JsonValueKind.String && AnchorPoints.TryParse(value.GetString(), out var point))
            return point;

        errors.Add($"{path}.{key} must be one of {AnchorPoints.Spelled}.");
        return fallback;
    }

    private static string? ReadFrameName(JsonElement element, string key, string path, List<string> errors)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString();

        errors.Add($"{path}.{key} must be a frame name string or null.");
        return null;
    }

    /// <summary>
    /// Reads a required finite number, or an optional one with a fallback.
    /// </summary>
    private static double ReadNumber(JsonElement element, string key, string path, List<string> errors, double? fallback = null, bool required = false)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                errors.Add($"{path}.{key} must be a finite number.");
                return 0;
            }

            return fallback ?? 0;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            errors.Add($"{path}.{key} must be a finite number.");
            return 0;
        }

        return number;
    }

    /// <summary>Trims float noise (0.30000000000000004 -&gt; 0.3) without losing small real values.</summary>
    public static string FormatNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
}