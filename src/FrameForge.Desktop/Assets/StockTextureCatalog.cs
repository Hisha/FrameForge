namespace FrameForge.Desktop.Assets;

/// <summary>A logical WoW 3.3.5a Interface texture reference offered by the stock chooser.</summary>
/// <param name="InterfacePath">The portable, archive-relative logical path, e.g. <c>Interface/TargetingFrame/UI-StatusBar</c>.</param>
/// <param name="Category">A short grouping label for the chooser.</param>
/// <param name="Notes">A one-line description of what the texture is.</param>
public sealed record StockTextureEntry(string InterfacePath, string Category, string Notes)
{
    public override string ToString() => $"{InterfacePath}  ·  {Category}";
}

/// <summary>
/// A small, curated set of stock WoW 3.3.5a (build-12340) Interface textures that a StatusBar
/// author is likely to want, offered as logical references. FrameForge never copies these into a
/// project and never persists a machine path: the reference is resolved through the configured WoW
/// client / extracted cache on every machine.
/// </summary>
/// <remarks>
/// The list is intentionally conservative. Anything the configured client does not contain is
/// reported as an unresolved asset with the existing missing-client/asset diagnostic instead of a
/// crash: picking an entry chooses a logical reference, and resolution happens against the user's
/// own copy of build-12340.
/// </remarks>
public static class StockTextureCatalog
{
    public static IReadOnlyList<StockTextureEntry> Curated { get; } =
    [
        new StockTextureEntry("Interface/TargetingFrame/UI-StatusBar", "Status bars",
            "The classic WoW health/mana bar fill used by TargetingFrame."),
        new StockTextureEntry("Interface/LFGFrame/Atlas", "Dungeon Finder",
            "Dungeon Finder frame atlas (stock LFG frame art)."),
        new StockTextureEntry("Interface/FriendsFrame/BG-StatusBar", "Status bars",
            "Class-coloured status bar background used by Blizzard frames."),
    ];
}
