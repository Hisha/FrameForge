using System.Globalization;
using System.Xml.Linq;
using FrameForge.Core.Semantics.V2;

namespace FrameForge.Core.Export;

/// <summary>Cross-checks a native preview result and a generated export against one v2 graph.</summary>
public static class V2LayoutExportConformance
{
    public static IReadOnlyList<V2FrameXmlDiagnostic> Validate(ResolvedUiLayout layout, V2FrameXmlExportPlan plan)
    {
        var diagnostics = new List<V2FrameXmlDiagnostic>();
        if (layout.Document.DocumentId != plan.Document.DocumentId)
        {
            diagnostics.Add(Error("FFV2C-DOCUMENT", "Layout and export were produced from different semantic documents."));
            return diagnostics;
        }
        if (string.IsNullOrWhiteSpace(plan.Xml))
        {
            diagnostics.Add(Error("FFV2C-XML", "Export has no generated XML to validate."));
            return diagnostics;
        }

        var nodes = plan.Document.Nodes.ToDictionary(node => node.Id);
        foreach (var node in nodes.Values)
        {
            if (!layout.Elements.TryGetValue(node.Id, out var element))
                diagnostics.Add(Error("FFV2C-LAYOUT-NODE", $"Native layout omitted '{node.DisplayLabel}'.", node.Id));
            var control = plan.Controls.FirstOrDefault(item => item.Id == node.Id);
            if (control is null)
            {
                diagnostics.Add(Error("FFV2C-EXPORT-NODE", $"Export omitted '{node.DisplayLabel}'.", node.Id));
                continue;
            }
            if (control.OwnerId != node.Owner.Id)
                diagnostics.Add(Error("FFV2C-OWNER", $"Export owner for '{node.DisplayLabel}' differs from authored ownership.", node.Id, "owner"));
            if (element?.Owner != node.Owner)
                diagnostics.Add(Error("FFV2C-LAYOUT-OWNER", $"Layout owner for '{node.DisplayLabel}' differs from authored ownership.", node.Id, "owner"));
        }

        var xml = XDocument.Parse(plan.Xml);
        XNamespace ui = "http://www.blizzard.com/wow/ui/";
        foreach (var node in nodes.Values)
        {
            var runtimeName = plan.RuntimeNames[node.Id];
            var element = xml.Descendants().SingleOrDefault(item => (string?)item.Attribute("name") == runtimeName);
            if (element is null) continue;
            var exportedAnchors = element.Element(ui + "Anchors")?.Elements(ui + "Anchor").Count() ?? 0;
            if (exportedAnchors != node.Anchors.Count)
                diagnostics.Add(Error("FFV2C-ANCHORS",
                    $"'{node.DisplayLabel}' has {node.Anchors.Count} authored anchors but {exportedAnchors} exported anchors.",
                    node.Id, "anchors"));

            if (node.Anchors.Count == 1 && layout.Elements.GetValueOrDefault(node.Id)?.Rect is { } rect)
            {
                var properties = layout.Elements[node.Id].EffectiveProperties?.Values ?? node.AuthoredProperties;
                var width = node.IsRegion ? properties.Region?.Width : properties.Frame?.Width;
                var height = node.IsRegion ? properties.Region?.Height : properties.Frame?.Height;
                if (width is { } expectedWidth && Math.Abs(rect.Width - expectedWidth) > 1e-6)
                    diagnostics.Add(Error("FFV2C-WIDTH", $"Preview width {rect.Width.ToString("0.###", CultureInfo.InvariantCulture)} differs from effective width {expectedWidth.ToString("0.###", CultureInfo.InvariantCulture)}.", node.Id));
                if (height is { } expectedHeight && Math.Abs(rect.Height - expectedHeight) > 1e-6)
                    diagnostics.Add(Error("FFV2C-HEIGHT", $"Preview height {rect.Height.ToString("0.###", CultureInfo.InvariantCulture)} differs from effective height {expectedHeight.ToString("0.###", CultureInfo.InvariantCulture)}.", node.Id));
            }
        }

        ValidateOwnerOrder(plan.Root.Id, plan.Root.RuntimeName, plan.Root.Children);
        foreach (var owner in nodes.Values.Where(node => node.CanOwnChildren))
            ValidateOwnerOrder(owner.Id, plan.RuntimeNames[owner.Id], owner.Children);
        return diagnostics;

        void ValidateOwnerOrder(SemanticId ownerId, string ownerName, IReadOnlyList<SemanticId> children)
        {
            var ownerElement = xml.Descendants().SingleOrDefault(item => (string?)item.Attribute("name") == ownerName);
            if (ownerElement is null) return;
            var expected = children.Where(nodes.ContainsKey).Select(id => plan.RuntimeNames[id]).ToHashSet(StringComparer.Ordinal);
            var actual = ownerElement.Descendants()
                .Where(item => item.Attribute("name") is not null && expected.Contains((string)item.Attribute("name")!))
                .Where(item => item.Ancestors().FirstOrDefault(ancestor => ancestor.Attribute("name") is not null) == ownerElement)
                .Select(item => (string)item.Attribute("name")!).ToArray();
            var expectedExportOrder = children.Where(id => nodes.GetValueOrDefault(id)?.IsRegion == true)
                .Concat(children.Where(id => nodes.GetValueOrDefault(id)?.IsRegion == false))
                .Select(id => plan.RuntimeNames[id]).ToArray();
            if (!actual.SequenceEqual(expectedExportOrder, StringComparer.Ordinal))
                diagnostics.Add(Error("FFV2C-ORDER", $"Exported child ordering under '{ownerName}' differs from the authored semantic ordering.", ownerId, "children"));
        }
    }

    private static V2FrameXmlDiagnostic Error(string code, string message, SemanticId? id = null, string? path = null) =>
        new(DiagnosticSeverity.Error, code, message, id, path);
}
