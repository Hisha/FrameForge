namespace FrameForge.Core.Models;

/// <summary>
/// A FrameForge layout document: the screen it targets plus an ordered frame list.
/// </summary>
/// <remarks>
/// <see cref="Format"/> is a discriminator so an unrelated JSON file is rejected with a
/// clear message instead of half-loading. The on-disk representation is
/// <c>*.fforge.json</c>; see <see cref="Serialization.ProjectCodec"/> and
/// docs/PROJECT_FORMAT.md.
/// </remarks>
/// <param name="Name">Display name.</param>
/// <param name="Screen">The UIParent this layout targets.</param>
/// <param name="Frames">Frames in authored order, which is also the tie-breaker for draw order.</param>
/// <param name="Source">Where the project came from, or null when it was authored here.</param>
public sealed record Project
{
    /// <summary>Discriminator written to every project file.</summary>
    public const string FormatId = "frameforge-project";

    /// <summary>Current on-disk schema version.</summary>
    public const int FormatVersion = 1;

    public string Name { get; init; } = "Untitled";

    public Screen Screen { get; init; } = Screen.Default;

    public IReadOnlyList<FrameDef> Frames { get; init; } = [];

    public ProjectSource? Source { get; init; }

    /// <summary>
    /// Optional functional composition target for a DESIGN-authored project. This is separate
    /// from <see cref="Source"/>: Source means the project itself was imported from FrameXML,
    /// while this profile associates an independently authored design with functional FrameXML.
    /// </summary>
    public FunctionalExportProfile? FunctionalExport { get; init; }

    /// <summary>FrameForge-only groups and locks; never projected into FrameXML semantics.</summary>
    public EditorMetadata Editor { get; init; } = new();

    /// <summary>Finds a frame by name, or null.</summary>
    public FrameDef? Find(string? name) =>
        name is null ? null : Frames.FirstOrDefault(f => f.Name == name);

    /// <summary>True when a frame with this name exists.</summary>
    public bool Contains(string? name) => name is not null && Frames.Any(f => f.Name == name);

    /// <summary>A deep copy, so editor state can never mutate a shared project instance.</summary>
    public Project DeepCopy() => this with
    {
        Frames = Frames.Select(f => f with { }).ToArray(),
        FunctionalExport = FunctionalExport is null ? null : FunctionalExport with
        {
            Source = FunctionalExport.Source with { },
            States = FunctionalExport.States.Select(item => item with { }).ToArray(),
            Values = FunctionalExport.Values.Select(item => item with { }).ToArray(),
        },
        Editor = Editor with
        {
            Groups = Editor.Groups.Select(group => group with { Members = [.. group.Members] }).ToArray(),
            LockedElements = [.. Editor.LockedElements],
            DesignOrder = [.. Editor.DesignOrder],
            DesignObjects = Editor.DesignObjects.Select(item => item with { StateIds = [.. item.StateIds] }).ToArray(),
            DesignStates = Editor.DesignStates.Select(item => item with { }).ToArray(),
        },
    };

    /// <summary>
    /// Value equality over the frame list, frame by frame.
    /// </summary>
    /// <remarks>
    /// The synthesized record equality would compare <see cref="Frames"/> by reference, so two
    /// structurally identical projects - a freshly loaded file and the one it was parsed from -
    /// would never be equal. Tests and the save/load self-check rely on real value equality.
    /// </remarks>
    public bool Equals(Project? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return Name == other.Name
               && Screen == other.Screen
               && Equals(Source, other.Source)
               && FunctionalProfilesEqual(FunctionalExport, other.FunctionalExport)
               && Editor.LockedElements.SequenceEqual(other.Editor.LockedElements)
               && Editor.Workspace == other.Editor.Workspace
               && Editor.ActiveDesignStateId == other.Editor.ActiveDesignStateId
               && Editor.PreviewStateId == other.Editor.PreviewStateId
               && Editor.DesignOrder.SequenceEqual(other.Editor.DesignOrder)
               && Editor.DesignStates.SequenceEqual(other.Editor.DesignStates)
               && Editor.DesignObjects.Count == other.Editor.DesignObjects.Count
               && Editor.DesignObjects.Zip(other.Editor.DesignObjects).All(pair =>
                   pair.First.FrameName == pair.Second.FrameName
                   && pair.First.DisplayName == pair.Second.DisplayName
                   && pair.First.DesignAsset == pair.Second.DesignAsset
                   && pair.First.TextOverride == pair.Second.TextOverride
                   && pair.First.TextStyle == pair.Second.TextStyle
                   && pair.First.RuntimeValueRequired == pair.Second.RuntimeValueRequired
                   && pair.First.RuntimeBinding == pair.Second.RuntimeBinding
                   && pair.First.StateIds.SequenceEqual(pair.Second.StateIds))
               && Editor.Groups.Count == other.Editor.Groups.Count
               && Editor.Groups.Zip(other.Editor.Groups).All(pair =>
                   pair.First.Name == pair.Second.Name
                   && pair.First.Locked == pair.Second.Locked
                   && pair.First.Concept == pair.Second.Concept
                   && pair.First.StockIdentity == pair.Second.StockIdentity
                   && pair.First.Expanded == pair.Second.Expanded
                   && pair.First.Members.SequenceEqual(pair.Second.Members))
               && Frames.SequenceEqual(other.Frames);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Screen);
        hash.Add(Source);
        if (FunctionalExport is { } functional)
        {
            hash.Add(functional.Source);
            hash.Add(functional.HostFrameName);
            foreach (var state in functional.States) hash.Add(state);
            foreach (var value in functional.Values) hash.Add(value);
        }
        hash.Add(Editor.Workspace);
        hash.Add(Editor.ActiveDesignStateId);
        hash.Add(Editor.PreviewStateId);
        foreach (var locked in Editor.LockedElements)
            hash.Add(locked);
        foreach (var ordered in Editor.DesignOrder)
            hash.Add(ordered);
        foreach (var group in Editor.Groups)
        {
            hash.Add(group.Name);
            hash.Add(group.Locked);
            hash.Add(group.Concept);
            hash.Add(group.StockIdentity);
            hash.Add(group.Expanded);
            foreach (var member in group.Members)
                hash.Add(member);
        }
        foreach (var state in Editor.DesignStates)
            hash.Add(state);
        foreach (var item in Editor.DesignObjects)
        {
            hash.Add(item.FrameName);
            hash.Add(item.DisplayName);
            hash.Add(item.DesignAsset);
            hash.Add(item.TextOverride);
            hash.Add(item.TextStyle);
            hash.Add(item.RuntimeValueRequired);
            hash.Add(item.RuntimeBinding);
            foreach (var stateId in item.StateIds)
                hash.Add(stateId);
        }
        foreach (var frame in Frames)
            hash.Add(frame);
        return hash.ToHashCode();
    }

    private static bool FunctionalProfilesEqual(FunctionalExportProfile? left, FunctionalExportProfile? right) =>
        left is null ? right is null : right is not null
            && left.Source == right.Source
            && left.HostFrameName == right.HostFrameName
            && left.States.SequenceEqual(right.States)
            && left.Values.SequenceEqual(right.Values);
}
