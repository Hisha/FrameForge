namespace FrameForge.Core.Models;

/// <summary>Identifies what a project was made from.</summary>
public static class SourceTypes
{
    /// <summary>Authored directly in FrameForge; no external origin.</summary>
    public const string FrameForge = "frameforge-project";

    /// <summary>Imported from a World of Warcraft 3.3.5a <c>FrameXML</c> file.</summary>
    public const string WowFrameXml = "wow-framexml";
}

/// <summary>
/// Where a project came from, kept as metadata rather than as a live handle.
/// </summary>
/// <remarks>
/// Imported XML is READ-ONLY source material. A saved project remains independently openable
/// when that XML is missing, but layout-only export reopens the source for reading and verifies
/// <see cref="Sha256"/> before it can produce a patched copy. The source is never opened for writing.
/// <para>
/// <see cref="ReferencePath"/> is the portable half of the location. When FrameForge saves a
/// project it writes the source location RELATIVE to the project file, falling back to just the
/// file name, so a project plus its XML keep working when the pair is copied or committed
/// somewhere else. Nothing in the project depends on an absolute path existing.
/// </para>
/// </remarks>
/// <param name="Type">One of the <see cref="SourceTypes"/> constants.</param>
/// <param name="FileName">File name only, e.g. <c>NativeHuntsFrame.xml</c>. Always portable.</param>
/// <param name="ReferencePath">
/// Where the source lived when it was read, relative to the project file when that is known.
/// Null when the reference is only informational.
/// </param>
/// <param name="ReadOnly">
/// True when the source must not be modified. FrameForge never writes XML back to the source —
/// even the layout-only patch applier only returns text — but the flag keeps the intent explicit
/// and survives a save/load round trip.
/// </param>
public sealed record ProjectSource
{
    public string Type { get; init; } = SourceTypes.FrameForge;

    public string? FileName { get; init; }

    public string? ReferencePath { get; init; }

    public bool ReadOnly { get; init; }

    /// <summary>
    /// SHA-256 of the exact UTF-8 source text at import time. Layout export requires this value
    /// and refuses a source whose content has drifted since the project was created.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>True when this source is an imported, read-only XML file.</summary>
    public bool IsReadOnlyXml =>
        ReadOnly && string.Equals(Type, SourceTypes.WowFrameXml, StringComparison.Ordinal);

    /// <summary>The name to show in the UI, falling back to the type when no file name is known.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FileName) ? Type : FileName!;
}
