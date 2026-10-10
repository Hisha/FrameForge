using System.Collections.ObjectModel;

namespace FrameForge.Core.Semantics.V2;

/// <summary>Applies an editor-only state to a transient preview without mutating authored data.</summary>
public static class UiPreviewPresentation
{
    public static UiDocument Apply(UiDocument document, SemanticPreviewState? state)
    {
        if (state is null || state.Overrides.Count == 0) return document;
        var values = state.Overrides.GroupBy(item => item.NodeId).ToDictionary(group => group.Key, group => group.Last());
        return document with
        {
            Nodes = [.. document.Nodes.Select(node => values.TryGetValue(node.Id, out var value)
                ? Apply(node, value)
                : node)],
        };
    }

    public static ResolvedUiLayout ApplyVisibility(ResolvedUiLayout layout, SemanticPreviewState? state)
    {
        var hiddenReferences = layout.Document.Editor?.HiddenReferenceNodes.ToHashSet() ?? [];
        if ((state is null || state.Overrides.All(item => item.Visible is null)) && hiddenReferences.Count == 0)
            return layout;
        var explicitVisibility = (state?.Overrides ?? []).Where(item => item.Visible is not null)
            .ToDictionary(item => item.NodeId, item => item.Visible!.Value);
        var nodes = layout.Document.Nodes.ToDictionary(node => node.Id);
        bool Visible(SemanticId id)
        {
            if (hiddenReferences.Contains(id)) return false;
            if (explicitVisibility.TryGetValue(id, out var own) && !own) return false;
            if (!nodes.TryGetValue(id, out var node)) return true;
            return node.Owner.Kind != OwnerKind.LocalNode || Visible(node.Owner.Id);
        }
        var elements = layout.Elements.ToDictionary(item => item.Key, item =>
        {
            if (item.Value.Node is null) return item.Value;
            var own = explicitVisibility.GetValueOrDefault(item.Key, item.Value.OwnVisible);
            return item.Value with { OwnVisible = own, EffectiveVisible = item.Value.EffectiveVisible && Visible(item.Key) };
        });
        return layout with { Elements = new ReadOnlyDictionary<SemanticId, ResolvedUiElement>(elements) };
    }

    private static UiNode Apply(UiNode node, SemanticPreviewOverride value)
    {
        var properties = node.AuthoredProperties;
        if (value.Visible is { } visible && properties.Frame is { } frame)
            properties = properties with { Frame = frame with { Visible = visible } };
        if (value.Text is not null && properties.FontString is { } font)
            properties = properties with { FontString = font with { Text = value.Text } };
        if (value.Text is not null && properties.Button is { } button)
            properties = properties with { Button = button with { Text = value.Text } };
        if (value.ProgressValue is { } progress && properties.StatusBar is { } status)
            properties = properties with { StatusBar = status with { Value = progress } };
        if (value.TextureReference is not null && properties.Texture is { } texture)
            properties = properties with { Texture = texture with { TextureReference = value.TextureReference } };
        return node with { AuthoredProperties = properties };
    }
}
