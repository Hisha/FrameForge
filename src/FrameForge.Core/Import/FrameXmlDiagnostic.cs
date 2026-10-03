namespace FrameForge.Core.Import;

/// <summary>How serious an import finding is.</summary>
public enum FrameXmlSeverity
{
    /// <summary>Context the user may want: what was ignored and why. Never a correctness claim.</summary>
    Info,

    /// <summary>
    /// Layout-affecting input that FrameForge did not represent exactly. The preview is usable
    /// but must not be mistaken for the file's real runtime layout.
    /// </summary>
    Warning,

    /// <summary>Input that could not be represented at all, or input that failed to parse.</summary>
    Error,
}

/// <summary>
/// One finding from an import: something in the source that FrameForge understood, partly
/// understood, or did not understand at all.
/// </summary>
/// <remarks>
/// The rule the whole importer is built around is that NOTHING layout-affecting may be dropped
/// in silence. A discarded anchor, an unresolvable <c>relativeTo</c>, an unresolved template
/// and an unexecuted <c>&lt;Scripts&gt;</c> block each produce a diagnostic, because a preview
/// that quietly omitted them would look authoritative while being wrong.
/// </remarks>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Code">
/// A stable machine-readable identifier, so tests and future UI filtering do not have to match
/// on prose. Grouped by construct rather than by message.
/// </param>
/// <param name="Message">Human-readable explanation, phrased for the person reading the summary.</param>
/// <param name="Frame">The element the finding belongs to, when there is one.</param>
/// <param name="Element">The FrameXML element or attribute that triggered it.</param>
public sealed record FrameXmlDiagnostic(
    FrameXmlSeverity Severity,
    string Code,
    string Message,
    string? Frame = null,
    string? Element = null)
{
    /// <summary>The severity name plus the code, for a heading.</summary>
    public string Heading => Frame is null ? Code : $"{Code} - {Frame}";

    /// <summary>One line for a list: severity, subject, message.</summary>
    public override string ToString()
    {
        var subject = Frame is null
            ? Element
            : Frame + (Element is null ? string.Empty : $" ({Element})");

        return subject is null
            ? $"[{Severity}] {Code}: {Message}"
            : $"[{Severity}] {subject}: {Message}";
    }
}
