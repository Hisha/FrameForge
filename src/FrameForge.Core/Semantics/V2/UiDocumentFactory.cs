namespace FrameForge.Core.Semantics.V2;

/// <summary>Creates a valid blank schema-v2 document without involving the v1 model.</summary>
public static class UiDocumentFactory
{
    public static UiDocument Create(
        string rootRuntimeName,
        string externalHostName,
        double designWidth = 1024,
        double designHeight = 768)
    {
        var rootId = SemanticId.New();
        return new UiDocument
        {
            Target = WowTargetProfile.Wow335a12340,
            DocumentId = SemanticId.New(),
            CompositionRoots =
            [
                new CompositionRoot
                {
                    Id = rootId,
                    RuntimeName = rootRuntimeName,
                    ExternalHostName = externalHostName,
                    DesignWidth = designWidth,
                    DesignHeight = designHeight,
                    Sizing = RootSizing.FillHost(),
                },
            ],
            ExternalReferences =
            [
                new ExternalReference
                {
                    GlobalName = externalHostName,
                    Description = "Module-owned composition host.",
                },
            ],
        };
    }
}
