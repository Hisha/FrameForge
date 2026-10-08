using System.Text.RegularExpressions;

namespace FrameForge.Core.Models;

/// <summary>A logical editor-only group. It never changes the FrameXML parent hierarchy.</summary>
public sealed record EditorGroup
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Members { get; init; } = [];
    public bool Locked { get; init; }
    /// <summary>Optional editor concept represented by this group (for example a stock framework).</summary>
    public string? Concept { get; init; }
    /// <summary>Stable stock identity. It is metadata only and never embeds client resources.</summary>
    public string? StockIdentity { get; init; }
    /// <summary>Tree presentation state; independent of <see cref="Locked"/>.</summary>
    public bool Expanded { get; init; }
}

/// <summary>A user-facing identity and authored design-state membership for one model element.</summary>
public sealed record DesignObjectMetadata
{
    public required string FrameName { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>
    /// Portable path to project-owned design artwork. This is deliberately distinct from a
    /// FrameXML/WoW texture reference even though the current renderer mirrors it into the
    /// frame's visual file field.
    /// </summary>
    public string? DesignAsset { get; init; }
    /// <summary>
    /// FrameForge-authored visible text. Null means use the imported/source text unchanged;
    /// an empty string is a deliberate blank override. This never rewrites source FrameXML.
    /// </summary>
    public string? TextOverride { get; init; }
    /// <summary>Optional DESIGN-only overrides layered over an authentic client stock style.</summary>
    public DesignTextStyleMetadata? TextStyle { get; init; }
    /// <summary>True when the exported object expects its displayed value from the runtime adapter.</summary>
    public bool RuntimeValueRequired { get; init; }
    /// <summary>
    /// Stable semantic key consumed by a runtime adapter. This is deliberately independent of
    /// editor/source identities and only applies to FontString and StatusBar DESIGN objects.
    /// </summary>
    public string? RuntimeBinding { get; init; }
    /// <summary>Empty means All States. Otherwise these are authored state IDs.</summary>
    public IReadOnlyList<string> StateIds { get; init; } = [];
}

/// <summary>The portable grammar for semantic runtime binding keys.</summary>
public static class RuntimeBindingKeys
{
    public const string GrammarDescription = "dot-separated lowerCamel identifiers (for example hunt.targetName)";

    private static readonly Regex ValidPattern = new(
        "^[a-z][A-Za-z0-9]*(?:\\.[a-z][A-Za-z0-9]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EditorIdentityPattern = new(
        "(?:^|\\.)DesignObject[0-9]*(?:$|\\.)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsValid(string? value) => value is not null && ValidPattern.IsMatch(value);

    public static bool LooksLikeEditorIdentity(string? value) =>
        value is not null && EditorIdentityPattern.IsMatch(value);
}

/// <summary>Portable text-authoring metadata. Null properties inherit from <see cref="BaseStyle"/>.</summary>
public sealed record DesignTextStyleMetadata
{
    public string? BaseStyle { get; init; }
    public double? Size { get; init; }
    public ColorRgba? Color { get; init; }
    /// <summary>Null inherits; NONE explicitly disables; NORMAL and THICK match WoW flags.</summary>
    public string? Outline { get; init; }
    /// <summary>Null inherits the style; true enables its shadow; false disables it.</summary>
    public bool? Shadow { get; init; }
    public string? JustifyH { get; init; }

    public bool HasOverrides => Size is not null || Color is not null || Outline is not null
        || Shadow is not null || JustifyH is not null;
}

/// <summary>An authored, Lua-free composition state.</summary>
public sealed record DesignState
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

/// <summary>Portable FrameForge editor state that has no WoW runtime meaning.</summary>
public sealed record EditorMetadata
{
    public IReadOnlyList<EditorGroup> Groups { get; init; } = [];
    public IReadOnlyList<string> LockedElements { get; init; } = [];
    public IReadOnlyList<DesignObjectMetadata> DesignObjects { get; init; } = [];
    /// <summary>
    /// Custom DESIGN frame names from back to front. Stock-framework members are excluded and
    /// remain a protected foundation; omitted names fall back to deterministic design-object order.
    /// </summary>
    public IReadOnlyList<string> DesignOrder { get; init; } = [];
    public IReadOnlyList<DesignState> DesignStates { get; init; } = [];
    public string? ActiveDesignStateId { get; init; }
    /// <summary>
    /// Selected design-time runtime simulation. This is editor-only metadata and never changes
    /// imported XML visibility, geometry, scripts, or layout-patch output.
    /// </summary>
    public string? PreviewStateId { get; init; }
    public string Workspace { get; init; } = "design";

    public bool IsLocked(string? frameName) => frameName is not null &&
        (LockedElements.Contains(frameName, StringComparer.Ordinal)
         || Groups.Any(group => group.Locked && group.Members.Contains(frameName, StringComparer.Ordinal)));

    public IReadOnlyList<string> GroupsFor(string? frameName) => frameName is null
        ? []
        : [.. Groups.Where(group => group.Members.Contains(frameName, StringComparer.Ordinal)).Select(group => group.Name)];

    public DesignObjectMetadata? DesignObjectFor(string? frameName) => frameName is null
        ? null
        : DesignObjects.FirstOrDefault(item => item.FrameName == frameName);

    public IReadOnlyList<string> EffectiveDesignOrder(Project project)
    {
        var stock = Groups.Where(group => group.Concept == "stock-framework")
            .SelectMany(group => group.Members).ToHashSet(StringComparer.Ordinal);
        var eligible = DesignObjects.Select(item => item.FrameName)
            .Where(name => project.Contains(name) && !stock.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        var result = DesignOrder.Where(eligible.Contains).Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in DesignObjects.Select(item => item.FrameName))
            if (eligible.Contains(name) && !result.Contains(name, StringComparer.Ordinal))
                result.Add(name);
        return result;
    }

    public string DisplayNameFor(FrameDef frame)
    {
        if (!frame.Anonymous && frame.SourceName is { Length: > 0 } sourceName)
        {
            if (!sourceName.StartsWith("$parent", StringComparison.Ordinal))
                return sourceName;
            var suffix = sourceName[7..];
            return Humanize(suffix);
        }
        var assigned = DesignObjectFor(frame.Name)?.DisplayName;
        if (!string.IsNullOrWhiteSpace(assigned))
            return assigned;
        if (!frame.Anonymous)
            return frame.Name;
        if (frame.Kind == FrameKind.TEXTURE && frame.Visual?.Texture?.File is { Length: > 0 } asset)
        {
            var leaf = asset.Replace('\\', '/').Split('/').Last();
            var stem = Path.GetFileNameWithoutExtension(leaf);
            var words = stem.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !word.Equals("ui", StringComparison.OrdinalIgnoreCase))
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]);
            var derived = string.Join(' ', words);
            if (derived.Length > 0)
                return derived;
        }
        var ordinal = new string(frame.Name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var kind = frame.Kind switch
        {
            FrameKind.FONTSTRING => "Text",
            FrameKind.TEXTURE => "Texture",
            FrameKind.BUTTON => "Button",
            _ => frame.Kind.TagName(),
        };
        return ordinal.Length > 0 ? $"{kind} #{ordinal}" : kind;
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Object";
        var spaced = System.Text.RegularExpressions.Regex.Replace(value.Replace('_', ' ').Replace('-', ' '),
            "(?<=[a-z0-9])(?=[A-Z])", " ");
        return string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
