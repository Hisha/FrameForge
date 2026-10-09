using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameForge.Core.Semantics.V2;

public sealed record UiDocumentParseResult
{
    private UiDocumentParseResult(UiDocument? document, IReadOnlyList<string> errors)
    {
        Document = document;
        Errors = errors;
    }

    public UiDocument? Document { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool Ok => Document is not null;
    public string ErrorText => string.Join(Environment.NewLine, Errors);

    public static UiDocumentParseResult Success(UiDocument document) => new(document, []);
    public static UiDocumentParseResult Failure(params string[] errors) => new(null, errors);
}

/// <summary>Deterministic codec dedicated to schema v2; it never converts schema v1.</summary>
public static class UiDocumentCodec
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(UiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var envelope = DocumentEnvelope.FromDocument(document);
        return JsonSerializer.Serialize(envelope, Options) + "\n";
    }

    public static bool HasV2FormatMarker(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("format", out var format) &&
                   format.ValueKind == JsonValueKind.String &&
                   format.GetString() == UiDocument.FormatId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static UiDocumentParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return UiDocumentParseResult.Failure("The v2 project document is empty.");

        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return UiDocumentParseResult.Failure("A v2 project must contain a JSON object.");
            if (!json.RootElement.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String)
                return UiDocumentParseResult.Failure($"A v2 project requires \"format\": \"{UiDocument.FormatId}\".");
            if (format.GetString() != UiDocument.FormatId)
                return UiDocumentParseResult.Failure(
                    $"Expected v2 format '{UiDocument.FormatId}', but found '{format.GetString()}'. Schema-v1 projects are not converted automatically.");
            if (!json.RootElement.TryGetProperty("version", out var version) || !version.TryGetInt32(out var versionNumber))
                return UiDocumentParseResult.Failure("A v2 project requires an integer schema version.");
            if (versionNumber != UiDocument.SchemaVersion)
                return UiDocumentParseResult.Failure(
                    $"Schema version {versionNumber} is unsupported; expected {UiDocument.SchemaVersion}. No automatic conversion is performed.");

            var envelope = JsonSerializer.Deserialize<DocumentEnvelope>(text, Options);
            if (envelope is null)
                return UiDocumentParseResult.Failure("The v2 project could not be decoded.");
            if (FindNullRequiredMember(envelope) is { } nullPath)
                return UiDocumentParseResult.Failure($"Malformed v2 project at {nullPath}: a required object or collection cannot be null.");
            return UiDocumentParseResult.Success(envelope.ToDocument());
        }
        catch (JsonException ex)
        {
            var path = string.IsNullOrEmpty(ex.Path) ? string.Empty : $" at {ex.Path}";
            return UiDocumentParseResult.Failure($"Malformed v2 project{path}: {ex.Message}");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new SemanticIdConverter());
        return options;
    }

    private static string? FindNullRequiredMember(DocumentEnvelope envelope)
    {
        if (envelope.Target is null) return "$.target";
        if (envelope.CompositionRoots is null) return "$.compositionRoots";
        if (envelope.Nodes is null) return "$.nodes";
        if (envelope.ExternalReferences is null) return "$.externalReferences";
        if (envelope.Diagnostics is null) return "$.diagnostics";

        for (var index = 0; index < envelope.CompositionRoots.Count; index++)
        {
            var root = envelope.CompositionRoots[index];
            if (root is null) return $"$.compositionRoots[{index}]";
            if (root.Sizing is null) return $"$.compositionRoots[{index}].sizing";
            if (root.Children is null) return $"$.compositionRoots[{index}].children";
        }
        for (var nodeIndex = 0; nodeIndex < envelope.Nodes.Count; nodeIndex++)
        {
            var node = envelope.Nodes[nodeIndex];
            if (node is null) return $"$.nodes[{nodeIndex}]";
            if (node.Owner is null) return $"$.nodes[{nodeIndex}].owner";
            if (node.Children is null) return $"$.nodes[{nodeIndex}].children";
            if (node.Anchors is null) return $"$.nodes[{nodeIndex}].anchors";
            if (node.AuthoredProperties is null) return $"$.nodes[{nodeIndex}].authoredProperties";
            for (var anchorIndex = 0; anchorIndex < node.Anchors.Count; anchorIndex++)
            {
                var anchor = node.Anchors[anchorIndex];
                if (anchor is null) return $"$.nodes[{nodeIndex}].anchors[{anchorIndex}]";
                if (anchor.Target is null) return $"$.nodes[{nodeIndex}].anchors[{anchorIndex}].target";
            }
        }
        for (var index = 0; index < envelope.ExternalReferences.Count; index++)
            if (envelope.ExternalReferences[index] is null) return $"$.externalReferences[{index}]";
        for (var index = 0; index < envelope.Diagnostics.Count; index++)
            if (envelope.Diagnostics[index] is null) return $"$.diagnostics[{index}]";
        return null;
    }

    private sealed record DocumentEnvelope
    {
        [JsonPropertyOrder(-2)]
        public required string Format { get; init; }

        [JsonPropertyOrder(-1)]
        public required int Version { get; init; }

        public required WowTargetProfile Target { get; init; }
        public required SemanticId DocumentId { get; init; }
        public required IReadOnlyList<CompositionRoot> CompositionRoots { get; init; }
        public required IReadOnlyList<UiNode> Nodes { get; init; }
        public required IReadOnlyList<ExternalReference> ExternalReferences { get; init; }
        public DocumentEditorMetadata? Editor { get; init; }
        public required IReadOnlyList<UiDiagnostic> Diagnostics { get; init; }

        public static DocumentEnvelope FromDocument(UiDocument document) => new()
        {
            Format = UiDocument.FormatId,
            Version = document.Version,
            Target = document.Target,
            DocumentId = document.DocumentId,
            CompositionRoots = document.CompositionRoots,
            Nodes = document.Nodes,
            ExternalReferences = document.ExternalReferences,
            Editor = document.Editor is null ? null : new DocumentEditorMetadata
            {
                Values = document.Editor.Values
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            },
            Diagnostics = document.Diagnostics,
        };

        public UiDocument ToDocument() => new()
        {
            Version = Version,
            Target = Target,
            DocumentId = DocumentId,
            CompositionRoots = CompositionRoots,
            Nodes = Nodes,
            ExternalReferences = ExternalReferences,
            Editor = Editor,
            Diagnostics = Diagnostics,
        };
    }

    private sealed class SemanticIdConverter : JsonConverter<SemanticId>
    {
        public override SemanticId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("A semantic identity must be a canonical GUID string.");
            var value = reader.GetString();
            if (value is null)
                throw new JsonException("A semantic identity cannot be null.");
            return new SemanticId(value);
        }

        public override void Write(Utf8JsonWriter writer, SemanticId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
