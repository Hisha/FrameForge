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

        if (errors.Count > 0)
            return ParseResult.Failure(errors);

        return ParseResult.Success(new Project { Name = name, Screen = screen, Frames = frames });
    }

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
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>The platform-appropriate file-type filter for open/save dialogs.</summary>
    public static readonly string[] DialogFilters = [$"FrameForge project (*{FileExtension})", $"*{FileExtension}"];

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
        };
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