using System.Collections.ObjectModel;
using FrameForge.Core.Templates;

namespace FrameForge.Core.Semantics.V2;

/// <summary>
/// Computes a transient template-aware view without modifying or flattening the authored document.
/// </summary>
public static class UiTemplateEffectiveProperties
{
    public static EffectiveNodeProperties Resolve(UiNode node, BlizzardTemplateRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(node);

        var provenance = AuthoredProvenance(node.AuthoredProperties);
        var identity = node.BlizzardTemplate;
        if (identity is null && node.Kind == UiNodeKind.Button &&
            node.Editor is { ReferenceOnly: true, ReferenceTemplate: { } referenceTemplate } &&
            registry?.Resolve(referenceTemplate) is not null)
            identity = referenceTemplate;
        if (string.IsNullOrWhiteSpace(identity))
            return Result(node.AuthoredProperties, null, null, provenance, [], [], []);

        var template = registry?.Resolve(identity);
        if (template is null)
        {
            provenance["template"] = EffectivePropertyOrigin.Unresolved;
            var diagnostic = new BlizzardTemplateDiagnostic(BlizzardTemplateDiagnosticSeverity.Error,
                registry is null ? "missing-template-registry" : "unknown-template",
                registry is null
                    ? $"Template '{identity}' cannot be resolved without a registry snapshot."
                    : $"Template '{identity}' is not present in this registry snapshot.",
                identity, Dependency: identity);
            return Result(node.AuthoredProperties, identity, null, provenance, [], [], [diagnostic]);
        }

        var authoredFrame = node.AuthoredProperties.Frame;
        var templateValues = template.EffectiveProperties;
        var effectiveFrame = authoredFrame is null
            ? null
            : authoredFrame with
            {
                Width = authoredFrame.Width ?? templateValues.Width,
                Height = authoredFrame.Height ?? templateValues.Height,
            };
        AddTemplateOrigin("frame.width", authoredFrame?.Width, "width");
        AddTemplateOrigin("frame.height", authoredFrame?.Height, "height");

        var values = node.AuthoredProperties with { Frame = effectiveFrame };
        var unresolved = template.AssetDependencies.Where(item => !item.IsResolved).ToArray();
        return Result(values, identity, templateValues, provenance, unresolved,
            templateValues.PreviewBehaviors, template.Diagnostics);

        void AddTemplateOrigin(string effectivePath, double? authored, string templatePath)
        {
            if (authored is not null)
            {
                provenance[effectivePath] = EffectivePropertyOrigin.Authored;
                return;
            }
            if (!templateValues.Provenance.TryGetValue(templatePath, out var source))
                return;
            provenance[effectivePath] = source.Origin == BlizzardTemplateValueOrigin.Declared &&
                                        source.Definition == identity
                ? EffectivePropertyOrigin.TemplateDeclared
                : EffectivePropertyOrigin.TemplateInherited;
        }
    }

    private static Dictionary<string, EffectivePropertyOrigin> AuthoredProvenance(AuthoredProperties properties)
    {
        var result = new Dictionary<string, EffectivePropertyOrigin>(StringComparer.Ordinal);
        Add("frame.width", properties.Frame?.Width);
        Add("frame.height", properties.Frame?.Height);
        Add("button.enabled", properties.Button?.Enabled);
        Add("button.text", properties.Button?.Text);
        Add("region.width", properties.Region?.Width);
        Add("region.height", properties.Region?.Height);
        return result;

        void Add<T>(string path, T? value)
        {
            if (value is not null)
                result[path] = EffectivePropertyOrigin.Authored;
        }
    }

    private static EffectiveNodeProperties Result(
        AuthoredProperties values,
        string? identity,
        BlizzardButtonProperties? templateValues,
        IDictionary<string, EffectivePropertyOrigin> provenance,
        IEnumerable<BlizzardAssetDependency> unresolved,
        IEnumerable<BlizzardKnownPreviewBehavior> behaviors,
        IEnumerable<BlizzardTemplateDiagnostic> diagnostics) => new(
        values,
        identity,
        templateValues,
        new ReadOnlyDictionary<string, EffectivePropertyOrigin>(
            new Dictionary<string, EffectivePropertyOrigin>(provenance, StringComparer.Ordinal)),
        Array.AsReadOnly(unresolved.ToArray()),
        Array.AsReadOnly(behaviors.ToArray()),
        Array.AsReadOnly(diagnostics.ToArray()));
}
