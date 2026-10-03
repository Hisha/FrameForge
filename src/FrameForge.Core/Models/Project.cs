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

    /// <summary>Finds a frame by name, or null.</summary>
    public FrameDef? Find(string? name) =>
        name is null ? null : Frames.FirstOrDefault(f => f.Name == name);

    /// <summary>True when a frame with this name exists.</summary>
    public bool Contains(string? name) => name is not null && Frames.Any(f => f.Name == name);

    /// <summary>A deep copy, so editor state can never mutate a shared project instance.</summary>
    public Project DeepCopy() => this with { Frames = Frames.Select(f => f with { }).ToArray() };

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
               && Frames.SequenceEqual(other.Frames);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Screen);
        hash.Add(Source);
        foreach (var frame in Frames)
            hash.Add(frame);
        return hash.ToHashCode();
    }
}