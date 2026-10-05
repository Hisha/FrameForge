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
        var editor = ParseEditor(root, frames, errors);

        if (errors.Count > 0)
            return ParseResult.Failure(errors);

        return ParseResult.Success(new Project
        {
            Name = name,
            Screen = screen,
            Frames = frames,
            Source = source,
            Editor = editor,
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
                if (frame.SourceLocation is { } location)
                {
                    writer.WriteStartObject("sourceLocation");
                    writer.WriteNumber("line", location.Line);
                    writer.WriteNumber("column", location.Column);
                    writer.WriteEndObject();
                }
                if (frame.Visual is { } visual && !visual.IsEmpty)
                    WriteVisual(writer, visual);

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

            if (project.Editor.Groups.Count > 0 || project.Editor.LockedElements.Count > 0
                || project.Editor.DesignObjects.Count > 0 || project.Editor.DesignStates.Count > 0
                || project.Editor.DesignOrder.Count > 0
                || project.Editor.ActiveDesignStateId is not null || project.Editor.Workspace != "design")
            {
                writer.WriteStartObject("editor");
                if (project.Editor.Workspace != "design")
                    writer.WriteString("workspace", project.Editor.Workspace);
                if (project.Editor.ActiveDesignStateId is { Length: > 0 } activeState)
                    writer.WriteString("activeDesignState", activeState);
                if (project.Editor.LockedElements.Count > 0)
                {
                    writer.WriteStartArray("lockedElements");
                    foreach (var name in project.Editor.LockedElements)
                        writer.WriteStringValue(name);
                    writer.WriteEndArray();
                }
                if (project.Editor.Groups.Count > 0)
                {
                    writer.WriteStartArray("groups");
                    foreach (var group in project.Editor.Groups)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", group.Name);
                        if (group.Locked)
                            writer.WriteBoolean("locked", true);
                        if (group.Expanded)
                            writer.WriteBoolean("expanded", true);
                        if (group.Concept is { Length: > 0 } concept)
                            writer.WriteString("concept", concept);
                        if (group.StockIdentity is { Length: > 0 } stockIdentity)
                            writer.WriteString("stockIdentity", stockIdentity);
                        writer.WriteStartArray("members");
                        foreach (var member in group.Members)
                            writer.WriteStringValue(member);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (project.Editor.DesignStates.Count > 0)
                {
                    writer.WriteStartArray("designStates");
                    foreach (var state in project.Editor.DesignStates)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", state.Id);
                        writer.WriteString("name", state.Name);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (project.Editor.DesignObjects.Count > 0)
                {
                    writer.WriteStartArray("designObjects");
                    foreach (var item in project.Editor.DesignObjects)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("frame", item.FrameName);
                        if (item.DisplayName is { Length: > 0 } displayName)
                            writer.WriteString("displayName", displayName);
                        if (item.DesignAsset is { Length: > 0 } designAsset)
                            writer.WriteString("designAsset", designAsset);
                        if (item.TextOverride is not null)
                            writer.WriteString("textOverride", item.TextOverride);
                        if (item.TextStyle is { } textStyle)
                        {
                            writer.WriteStartObject("textStyle");
                            if (textStyle.BaseStyle is { Length: > 0 }) writer.WriteString("baseStyle", textStyle.BaseStyle);
                            if (textStyle.Size is { } size) writer.WriteNumber("size", size);
                            if (textStyle.Color is { } color) WriteColor(writer, "color", color);
                            if (textStyle.Outline is { } outline) writer.WriteString("outline", outline);
                            if (textStyle.Shadow is { } shadow) writer.WriteBoolean("shadow", shadow);
                            if (textStyle.JustifyH is { } justifyH) writer.WriteString("justifyH", justifyH);
                            writer.WriteEndObject();
                        }
                        if (item.StateIds.Count > 0)
                        {
                            writer.WriteStartArray("states");
                            foreach (var stateId in item.StateIds)
                                writer.WriteStringValue(stateId);
                            writer.WriteEndArray();
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (project.Editor.DesignOrder.Count > 0)
                {
                    writer.WriteStartArray("designOrder");
                    foreach (var frameName in project.Editor.DesignOrder)
                        writer.WriteStringValue(frameName);
                    writer.WriteEndArray();
                }
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
            SourceLocation = ReadSourceLocation(element, path, errors),
            Visual = ReadVisual(element, path, errors),
        };
    }

    private static SourceLocation? ReadSourceLocation(JsonElement element, string path, List<string> errors)
    {
        if (!element.TryGetProperty("sourceLocation", out var location) || location.ValueKind == JsonValueKind.Null)
            return null;
        if (location.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.sourceLocation must be an object.");
            return null;
        }
        var line = ReadNumber(location, "line", $"{path}.sourceLocation", errors, required: true);
        var column = ReadNumber(location, "column", $"{path}.sourceLocation", errors, required: true);
        if (line < 1 || column < 1 || line % 1 != 0 || column % 1 != 0)
        {
            errors.Add($"{path}.sourceLocation line and column must be positive whole numbers.");
            return null;
        }
        return new SourceLocation((int)line, (int)column);
    }

    private static EditorMetadata ParseEditor(JsonElement root, IReadOnlyList<FrameDef> frames, List<string> errors)
    {
        if (!root.TryGetProperty("editor", out var editor) || editor.ValueKind == JsonValueKind.Null)
            return new EditorMetadata();
        if (editor.ValueKind != JsonValueKind.Object)
        {
            errors.Add("editor must be an object.");
            return new EditorMetadata();
        }

        var names = frames.Select(frame => frame.Name).ToHashSet(StringComparer.Ordinal);
        var locked = ReadNameArray(editor, "lockedElements", "editor", names, errors);
        var groups = new List<EditorGroup>();
        if (editor.TryGetProperty("groups", out var array) && array.ValueKind != JsonValueKind.Null)
        {
            if (array.ValueKind != JsonValueKind.Array)
                errors.Add("editor.groups must be an array.");
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;
                foreach (var entry in array.EnumerateArray())
                {
                    var groupPath = $"editor.groups[{index++}]";
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"{groupPath} must be an object.");
                        continue;
                    }
                    var name = ReadOptionalString(entry, "name");
                    if (name is null || !seen.Add(name))
                    {
                        errors.Add($"{groupPath}.name must be non-empty and unique.");
                        continue;
                    }
                    groups.Add(new EditorGroup
                    {
                        Name = name,
                        Locked = entry.TryGetProperty("locked", out var isLocked) && isLocked.ValueKind == JsonValueKind.True,
                        Expanded = entry.TryGetProperty("expanded", out var expanded) && expanded.ValueKind == JsonValueKind.True,
                        Concept = ReadOptionalString(entry, "concept"),
                        StockIdentity = ReadOptionalString(entry, "stockIdentity"),
                        Members = ReadNameArray(entry, "members", groupPath, names, errors),
                    });
                }
            }
        }
        var states = new List<DesignState>();
        if (editor.TryGetProperty("designStates", out var stateArray) && stateArray.ValueKind != JsonValueKind.Null)
        {
            if (stateArray.ValueKind != JsonValueKind.Array)
                errors.Add("editor.designStates must be an array.");
            else
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var stateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;
                foreach (var entry in stateArray.EnumerateArray())
                {
                    var statePath = $"editor.designStates[{index++}]";
                    var id = entry.ValueKind == JsonValueKind.Object ? ReadOptionalString(entry, "id") : null;
                    var stateName = entry.ValueKind == JsonValueKind.Object ? ReadOptionalString(entry, "name") : null;
                    if (id is null || stateName is null || !ids.Add(id) || !stateNames.Add(stateName))
                    {
                        errors.Add($"{statePath} requires unique non-empty id and name values.");
                        continue;
                    }
                    states.Add(new DesignState { Id = id, Name = stateName });
                }
            }
        }
        var validStateIds = states.Select(state => state.Id).ToHashSet(StringComparer.Ordinal);
        var designObjects = new List<DesignObjectMetadata>();
        if (editor.TryGetProperty("designObjects", out var objectArray) && objectArray.ValueKind != JsonValueKind.Null)
        {
            if (objectArray.ValueKind != JsonValueKind.Array)
                errors.Add("editor.designObjects must be an array.");
            else
            {
                var seenObjects = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;
                foreach (var entry in objectArray.EnumerateArray())
                {
                    var objectPath = $"editor.designObjects[{index++}]";
                    var frameName = entry.ValueKind == JsonValueKind.Object ? ReadOptionalString(entry, "frame") : null;
                    if (frameName is null || !names.Contains(frameName) || !seenObjects.Add(frameName))
                    {
                        errors.Add($"{objectPath}.frame must reference one unique existing frame.");
                        continue;
                    }
                    designObjects.Add(new DesignObjectMetadata
                    {
                        FrameName = frameName,
                        DisplayName = ReadOptionalString(entry, "displayName"),
                        DesignAsset = ReadOptionalString(entry, "designAsset"),
                        TextOverride = ReadOptionalString(entry, "textOverride"),
                        TextStyle = ReadDesignTextStyle(entry, objectPath, errors),
                        StateIds = ReadStringArray(entry, "states", objectPath, validStateIds, errors),
                    });
                }
            }
        }
        var activeState = ReadOptionalString(editor, "activeDesignState");
        if (activeState is not null && !validStateIds.Contains(activeState))
            errors.Add($"editor.activeDesignState references missing state \"{activeState}\".");
        var workspace = ReadOptionalString(editor, "workspace") ?? "design";
        if (workspace is not ("design" or "inspect"))
            errors.Add("editor.workspace must be design or inspect.");
        return new EditorMetadata
        {
            LockedElements = locked,
            Groups = groups,
            DesignStates = states,
            DesignObjects = designObjects,
            DesignOrder = ReadStringArray(editor, "designOrder", "editor", names, errors),
            ActiveDesignStateId = activeState,
            Workspace = workspace,
        };
    }

    private static DesignTextStyleMetadata? ReadDesignTextStyle(JsonElement entry, string path, List<string> errors)
    {
        if (!entry.TryGetProperty("textStyle", out var style) || style.ValueKind == JsonValueKind.Null)
            return null;
        if (style.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.textStyle must be an object.");
            return null;
        }
        var outline = ReadOptionalString(style, "outline");
        if (outline is not null and not ("NONE" or "NORMAL" or "THICK"))
            errors.Add($"{path}.textStyle.outline must be NONE, NORMAL, or THICK.");
        var justify = ReadOptionalString(style, "justifyH");
        if (justify is not null and not ("LEFT" or "CENTER" or "RIGHT"))
            errors.Add($"{path}.textStyle.justifyH must be LEFT, CENTER, or RIGHT.");
        bool? shadow = style.TryGetProperty("shadow", out var shadowValue)
            && shadowValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? shadowValue.GetBoolean() : null;
        return new DesignTextStyleMetadata
        {
            BaseStyle = ReadOptionalString(style, "baseStyle"),
            Size = ReadOptionalNumber(style, "size", $"{path}.textStyle", errors),
            Color = ReadColor(style, "color", $"{path}.textStyle", errors),
            Outline = outline,
            Shadow = shadow,
            JustifyH = justify,
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string key, string path,
        IReadOnlySet<string> validValues, List<string> errors)
    {
        if (!parent.TryGetProperty(key, out var array) || array.ValueKind == JsonValueKind.Null)
            return [];
        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.{key} must be an array.");
            return [];
        }
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (value is null || !validValues.Contains(value))
                errors.Add($"{path}.{key} contains an unknown value.");
            else if (!values.Contains(value, StringComparer.Ordinal))
                values.Add(value);
        }
        return values;
    }

    private static IReadOnlyList<string> ReadNameArray(JsonElement parent, string key, string path,
        IReadOnlySet<string> validNames, List<string> errors)
    {
        if (!parent.TryGetProperty(key, out var array) || array.ValueKind == JsonValueKind.Null)
            return [];
        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.{key} must be an array of frame names.");
            return [];
        }
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                errors.Add($"{path}.{key} entries must be non-empty strings.");
                continue;
            }
            var name = item.GetString()!;
            if (!validNames.Contains(name))
            {
                errors.Add($"{path}.{key} references missing frame \"{name}\".");
                continue;
            }
            if (!values.Contains(name, StringComparer.Ordinal))
                values.Add(name);
        }
        return values;
    }

    /// <summary>
    /// Writes one frame's retained paint facts.
    /// </summary>
    /// <remarks>
    /// Written as a nested object with only the keys that exist, matching the rest of the format's
    /// rule that absent facts are absent rather than defaulted. That matters here: "this texture
    /// declares no colour" and "this texture is white" are different states, and collapsing them
    /// would lose the ability to tell a round-tripped file from an authored one.
    /// </remarks>
    private static void WriteVisual(Utf8JsonWriter writer, FrameVisual visual)
    {
        writer.WriteStartObject("visual");

        if (visual.Texture is { } texture)
        {
            writer.WriteStartObject("texture");
            WriteNullableString(writer, "file", texture.File);

            if (texture.TexCoords.IsSubRectangle)
            {
                writer.WriteStartObject("texCoords");
                writer.WriteNumber("left", texture.TexCoords.Left);
                writer.WriteNumber("right", texture.TexCoords.Right);
                writer.WriteNumber("top", texture.TexCoords.Top);
                writer.WriteNumber("bottom", texture.TexCoords.Bottom);
                writer.WriteEndObject();
            }

            if (texture.Color is { } color)
                WriteColor(writer, "color", color);
            if (texture.Alpha is { } alpha)
                writer.WriteNumber("alpha", alpha);
            if (texture.NormalizeTexCoords is { } normalize)
                writer.WriteBoolean("normalizeTexCoords", normalize);
            if (texture.BlendMode is { Length: > 0 } blendMode)
                writer.WriteString("blendMode", blendMode);

            writer.WriteEndObject();
        }

        if (visual.Text is { } text)
        {
            writer.WriteStartObject("text");
            WriteNullableString(writer, "text", text.Text);
            if (text.JustifyH is { Length: > 0 } justifyH)
                writer.WriteString("justifyH", justifyH);
            if (text.JustifyV is { Length: > 0 } justifyV)
                writer.WriteString("justifyV", justifyV);
            if (text.FontTemplate is { Length: > 0 } font)
                writer.WriteString("font", font);
            writer.WriteEndObject();
        }

        if (visual.StatusBar is { } bar)
        {
            writer.WriteStartObject("statusBar");
            if (bar.MinValue is { } min)
                writer.WriteNumber("minValue", min);
            if (bar.MaxValue is { } max)
                writer.WriteNumber("maxValue", max);
            if (bar.DefaultValue is { } value)
                writer.WriteNumber("defaultValue", value);
            if (bar.BarTexture is { Length: > 0 } barTexture)
                writer.WriteString("barTexture", barTexture);
            if (bar.BarColor is { } barColor)
                WriteColor(writer, "barColor", barColor);
            writer.WriteEndObject();
        }

        if (visual.DrawLayer is { Length: > 0 } drawLayer)
            writer.WriteString("drawLayer", drawLayer);
        if (visual.Id is { } id)
            writer.WriteNumber("id", id);

        writer.WriteEndObject();
    }

    private static void WriteColor(Utf8JsonWriter writer, string name, ColorRgba color)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("r", color.R);
        writer.WriteNumber("g", color.G);
        writer.WriteNumber("b", color.B);
        writer.WriteNumber("a", color.A);
        writer.WriteEndObject();
    }

    private static FrameVisual? ReadVisual(JsonElement element, string path, List<string> errors)
    {
        if (!element.TryGetProperty("visual", out var visual) || visual.ValueKind == JsonValueKind.Null)
            return null;

        if (visual.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.visual must be an object.");
            return null;
        }

        var result = new FrameVisual
        {
            Texture = ReadTextureVisual(visual, path, errors),
            Text = ReadTextVisual(visual, path, errors),
            StatusBar = ReadStatusBarVisual(visual, path, errors),
            DrawLayer = ReadOptionalString(visual, "drawLayer"),
            Id = ReadOptionalInt(visual, "id"),
        };

        return result.IsEmpty ? null : result;
    }

    private static TextureVisual? ReadTextureVisual(JsonElement visual, string path, List<string> errors)
    {
        if (!visual.TryGetProperty("texture", out var texture) || texture.ValueKind == JsonValueKind.Null)
            return null;

        if (texture.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.visual.texture must be an object.");
            return null;
        }

        var file = ReadOptionalString(texture, "file");
        var color = ReadColor(texture, "color", $"{path}.visual.texture", errors);
        var alpha = ReadOptionalNumber(texture, "alpha", $"{path}.visual.texture", errors);
        var normalize = texture.TryGetProperty("normalizeTexCoords", out var normalizeElement) &&
                        normalizeElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? normalizeElement.ValueKind == JsonValueKind.True
            : (bool?)null;
        var blendMode = ReadOptionalString(texture, "blendMode");

        // Mirrors the importer: a texture that states no paint at all is not carried, while one
        // that states only a colour is.
        if (file is null && color is null && alpha is null && normalize is null && blendMode is null)
            return null;

        return new TextureVisual(
            file,
            ReadTexCoords(texture, $"{path}.visual.texture", errors),
            color,
            alpha,
            normalize,
            blendMode);
    }

    private static TexCoords ReadTexCoords(JsonElement texture, string path, List<string> errors)
    {
        if (!texture.TryGetProperty("texCoords", out var coords) || coords.ValueKind == JsonValueKind.Null)
            return TexCoords.Full;

        if (coords.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.texCoords must be an object.");
            return TexCoords.Full;
        }

        return new TexCoords(
            ReadOptionalNumber(coords, "left", path, errors) ?? 0,
            ReadOptionalNumber(coords, "right", path, errors) ?? 0,
            ReadOptionalNumber(coords, "top", path, errors) ?? 0,
            ReadOptionalNumber(coords, "bottom", path, errors) ?? 0);
    }

    private static TextVisual? ReadTextVisual(JsonElement visual, string path, List<string> errors)
    {
        if (!visual.TryGetProperty("text", out var text) || text.ValueKind == JsonValueKind.Null)
            return null;

        if (text.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.visual.text must be an object.");
            return null;
        }

        return new TextVisual(
            ReadOptionalString(text, "text"),
            ReadOptionalString(text, "justifyH"),
            ReadOptionalString(text, "justifyV"),
            ReadOptionalString(text, "font"));
    }

    private static StatusBarVisual? ReadStatusBarVisual(JsonElement visual, string path, List<string> errors)
    {
        if (!visual.TryGetProperty("statusBar", out var bar) || bar.ValueKind == JsonValueKind.Null)
            return null;

        if (bar.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.visual.statusBar must be an object.");
            return null;
        }

        return new StatusBarVisual(
            ReadOptionalNumber(bar, "minValue", $"{path}.visual.statusBar", errors),
            ReadOptionalNumber(bar, "maxValue", $"{path}.visual.statusBar", errors),
            ReadOptionalNumber(bar, "defaultValue", $"{path}.visual.statusBar", errors),
            ReadOptionalString(bar, "barTexture"),
            ReadColor(bar, "barColor", $"{path}.visual.statusBar", errors));
    }

    private static ColorRgba? ReadColor(JsonElement parent, string key, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(key, out var color) || color.ValueKind == JsonValueKind.Null)
            return null;

        if (color.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}.{key} must be an object with r, g, b and a.");
            return null;
        }

        return new ColorRgba(
            ReadNumber(color, "r", $"{path}.{key}", errors),
            ReadNumber(color, "g", $"{path}.{key}", errors),
            ReadNumber(color, "b", $"{path}.{key}", errors),
            ReadNumber(color, "a", $"{path}.{key}", errors, 1));
    }

    private static int? ReadOptionalInt(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>
    /// Reads a number that is allowed to be absent, preserving the difference between "not
    /// declared" and "declared as zero".
    /// </summary>
    /// <remarks>
    /// <see cref="ReadNumber"/> cannot be used for this: with no fallback it returns 0, which would
    /// turn every undeclared optional number into a declared zero on the way back in. That matters
    /// for status bars in particular - a missing <c>maxValue</c> is WoW's default of 100, not 0 - and
    /// it would silently change what a reopened project says.
    /// </remarks>
    private static double? ReadOptionalNumber(JsonElement element, string key, string path, List<string> errors)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            errors.Add($"{path}.{key} must be a finite number.");
            return null;
        }

        return number;
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
