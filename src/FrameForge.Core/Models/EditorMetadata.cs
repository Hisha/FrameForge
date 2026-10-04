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
    /// <summary>Empty means All States. Otherwise these are authored state IDs.</summary>
    public IReadOnlyList<string> StateIds { get; init; } = [];
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
    public IReadOnlyList<DesignState> DesignStates { get; init; } = [];
    public string? ActiveDesignStateId { get; init; }
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
